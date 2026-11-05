#!/usr/bin/env -S dotnet fsi
// Preseed these existing build dependencies through NuGet.config on every agent image.
#r "nuget: Fake.Core.Target, 6.2.0"
#r "nuget: Fake.IO.FileSystem, 6.2.0"
#r "nuget: Fake.DotNet.Cli, 6.2.0"
#r "nuget: Fake.Core.ReleaseNotes, 6.2.0"
#r "nuget: Fake.Tools.Git, 6.2.0"
#r "nuget: Farmer, 1.9.36"
#r "nuget: Microsoft.Extensions.Configuration.Json, 10.0.14"
#r "nuget: Microsoft.Extensions.Configuration.Binder, 10.0.14"
#r "nuget: MSBuild.StructuredLogger, 2.2.291"

open Fake.Core
open Fake.Core.TargetOperators
open Fake.DotNet
open Fake.IO
open Fake.IO.Globbing.Operators
open Fake.Tools
open System
open Farmer
open Farmer.Arm
open Farmer.Builders

let gitName = "DatatypeChannels.ASB"
let gitOwner = "Azure"
let gitHome = sprintf "https://github.com/%s" gitOwner
let gitIO = sprintf "https://%s.github.io/%s" gitOwner gitName

let gitRepo = sprintf "git@github.com:%s/%s.git" gitOwner gitName
let gitContent = sprintf "https://raw.githubusercontent.com/%s/%s" gitOwner gitName

let root = System.IO.Path.GetFullPath __SOURCE_DIRECTORY__
let release = lazy (ReleaseNotes.load (System.IO.Path.Combine(root, "RELEASE_NOTES.md")))
let ver = lazy (
    match Environment.environVarOrNone "BUILD_NUMBER" with
    | Some n -> { release.Value.SemVer with Patch = uint32 n; Original = None }
    | _ -> SemVer.parse "0.0.0")

// Loading the real target definitions for contract tests must not run the default.
let isEntryPoint =
    System.IO.Path.GetFullPath fsi.CommandLineArgs[0] = System.IO.Path.Combine(root, __SOURCE_FILE__)
(if isEntryPoint then fsi.CommandLineArgs |> Array.skip 1 else [||])
|> Array.toList
|> Context.FakeExecutionContext.Create false __SOURCE_FILE__
|> Context.RuntimeContext.Fake
|> Context.setExecutionContext

[<AutoOpen>]
module Shell =
    let sh cmd args cwd = 
        Shell.Exec (cmd,args,cwd)
        |> function 0 -> () | code -> failwithf "%s %s (in %s) exited with code: %d" cmd args cwd code

    let psh cmd args cwd parse = 
        CreateProcess.fromRawCommandLine cmd args
        |> CreateProcess.withWorkingDirectory cwd
        |> CreateProcess.redirectOutput
        |> CreateProcess.ensureExitCode
        |> CreateProcess.map parse
        |> Proc.run

    module Parse =
        let plain r = String.trimChars [|' '; '\n'|] r.Result.Output

module Az =
    let query args =
        psh (if OperatingSystem.IsWindows() then "cmd.exe" else "az")
            (if OperatingSystem.IsWindows() then $"/c az.cmd {args}" else args)
    let currentUserId () =
        query "ad signed-in-user show --query id -o tsv" "." (Parse.plain >> Guid)

module Settings =
    open Microsoft.Extensions.Configuration
    let [<Literal>] TestSettingsJson = "tests/DatatypeChannels.ASB.Tests/local.settings.json"
    type [<CLIMutable>] TestSettings = { ServiceBus: string }

    let testSettings = 
        lazy
            let config = ConfigurationBuilder().AddJsonFile((Path.getFullName ".", TestSettingsJson) ||> Path.combine, false).Build()
            let testSettings = { ServiceBus = "" }
            config.Bind testSettings
            testSettings

module ARM = // workaround for https://github.com/dotnet/fsharp/issues/6434
    let AzureServiceBusDataOwner = Guid "090c5cfd-751d-490a-894a-3ce6f1109419"

    let roleAssingment scope principalType assignmentId principalId roleDefinitionId dependsOn =
        {| apiVersion = "2020-04-01-preview"
           ``type`` = "Microsoft.Authorization/roleAssignments"
           name = string assignmentId
           scope = scope
           properties = 
               {| roleDefinitionId = sprintf "[resourceId('Microsoft.Authorization/roleDefinitions','%O')]" roleDefinitionId
                  principalType = principalType
                  principalId = principalId |}
           dependsOn = [| dependsOn |] |}

    let init (principalId, principalType) loc rg ns =
        let sb = serviceBus {
            name ns
            sku ServiceBus.Sku.Standard
        }
        let sbDataOwner = 
            roleAssingment (sprintf "[concat('Microsoft.ServiceBus/namespaces', '/', '%s')]" sb.Name.Value)
                           principalType
                           (sprintf "[guid(resourceGroup().id, '%O', '%O')]" principalId AzureServiceBusDataOwner)
                           (string principalId)
                           AzureServiceBusDataOwner
                           ((ResourceId.create (ServiceBus.namespaces, sb.Name)).Eval())
            |> Resource.ofObj
        let deployment = arm {
            location loc
            add_resource sb
            add_resource sbDataOwner
        }
        deployment |> Deploy.execute rg [] |> ignore

        printfn "Deployed, generating local settings..."
        [ sprintf """{ "ServiceBus": "%s.servicebus.windows.net" }""" sb.Name.Value ]
        |> File.write false Settings.TestSettingsJson

module Pipeline =
    open System.Diagnostics
    open System.Globalization
    open System.IO
    open System.IO.Compression
    open System.Security.Cryptography
    open System.Text.Json
    open System.Text.RegularExpressions
    open System.Xml.Linq

    let canTest repository reason branch =
        repository = "Azure/DatatypeChannels.ASB"
        && List.contains reason ["IndividualCI"; "BatchedCI"; "Manual"]
        && branch = "refs/heads/master"

    let canRelease repository reason branch requested =
        canTest repository reason branch && reason = "Manual"
        && String.Equals(requested, "true", StringComparison.OrdinalIgnoreCase)

    let required name =
        match System.Environment.GetEnvironmentVariable name with
        | value when String.IsNullOrWhiteSpace value || value.Contains("$(") ->
            failwithf "Configure %s before running this operation." name
        | value -> value

    let unsigned name (value: string) =
        match UInt32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
        | true, number -> number
        | _ -> failwithf "%s must be an unsigned 32-bit integer." name

    let packageVersion (notes: string) buildNumber offset =
        let version = Regex.Match(notes, @"(?m)^### (\d+)\.(\d+)\.\d+([-\+][^\s]+)?\s*$")
        if not version.Success then failwith "No release version found in RELEASE_NOTES.md."
        let number = unsigned "BUILD_BUILDID" buildNumber
        if number = 0u then failwith "BUILD_BUILDID must be positive."
        let patch = uint64 number + uint64 (unsigned "PACKAGE_PATCH_OFFSET" offset)
        if patch > uint64 UInt32.MaxValue then failwith "Package patch version overflow."
        sprintf "%s.%s.%d%s" version.Groups.[1].Value version.Groups.[2].Value patch version.Groups.[3].Value

    let namespaceFqdn (value: string) =
        if not (Regex.IsMatch(value, @"^[a-zA-Z0-9][a-zA-Z0-9-]*\.servicebus\.windows\.net$")) then
            failwith "SERVICE_BUS_NAMESPACE must be an existing public Azure Service Bus namespace FQDN."
        value

    let metadata (version: string) (note: string) =
        let repository = "https://github.com/Azure/DatatypeChannels.ASB"
        let properties =
            [ "Copyright", "(c) Microsoft Corporation. All rights reserved."
              "Authors", "Eugene Tolmachev"
              "PackageProjectUrl", "https://Azure.github.io/DatatypeChannels.ASB"
              "RepositoryUrl", repository
              "PackageLicense", "MIT"
              "PackageReleaseNotes", note
              "PackageIconUrl", gitContent + "/master/docs/content/logo.png"
              "PackageTags", "Azure Service Bus;Datatype Channel;fsharp"
              "Version", version ]
        let elements = properties |> List.map (fun (name, value) -> XElement(XName.Get name, value))
        XElement(XName.Get "Project", XElement(XName.Get "PropertyGroup", elements)).ToString()

    let run (directory: string) (program: string) (arguments: string list) =
        let start = ProcessStartInfo(program, WorkingDirectory = directory, UseShellExecute = false)
        arguments |> List.iter start.ArgumentList.Add
        // Keep subprocess diagnostics to tool/exit code; never echo credentials or arguments.
        use child =
            try Process.Start start
            with :? System.ComponentModel.Win32Exception as error
                    when error.NativeErrorCode = 2 || error.NativeErrorCode = 3 ->
                failwithf "Required preinstalled tool '%s' is unavailable." program
        child.WaitForExit()
        if child.ExitCode <> 0 then failwithf "%s failed with exit code %d." program child.ExitCode

    let dotnet directory arguments = run directory "dotnet" arguments
    let solution = Path.Combine(root, "DatatypeChannels.ASB.sln")
    let library = Path.Combine(root, "src/DatatypeChannels.ASB/DatatypeChannels.ASB.fsproj")
    let artifactDirectory () = required "ARTIFACT_DIRECTORY" |> Path.GetFullPath
    let nugetConfig = Path.Combine(root, "NuGet.config")

    let currentVersion () =
        packageVersion (File.ReadAllText(Path.Combine(root, "RELEASE_NOTES.md")))
            (required "BUILD_BUILDID") (required "PACKAGE_PATCH_OFFSET")

    let writeMetadata () =
        let notes = File.ReadAllLines(Path.Combine(root, "RELEASE_NOTES.md"))
        let note = notes |> Array.find (fun line -> line.StartsWith("* ")) |> fun line -> line.Substring 2
        File.WriteAllText(Path.Combine(root, "Directory.Build.props"), metadata (currentVersion ()) note)

    let copyDirectory (source: string) (destination: string) =
        for file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories) do
            let target = Path.Combine(destination, Path.GetRelativePath(source, file))
            Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
            File.Copy(file, target, true)

    let packageIdentity path =
        use archive = ZipFile.OpenRead path
        let entry = archive.GetEntry("DatatypeChannels.ASB.nuspec")
        use stream = entry.Open()
        let document = XDocument.Load stream
        let value name =
            document.Descendants()
            |> Seq.find (fun node -> node.Name.LocalName = name)
            |> fun node -> node.Value
        value "id", value "version"

    let packageFile (directory: string) version =
        let expected = Path.Combine(directory, $"DatatypeChannels.ASB.{version}.nupkg")
        if Directory.GetFiles(directory, "*.nupkg") <> [|expected|] then
            failwith "Expected exactly the package produced by this run."
        if packageIdentity expected <> ("DatatypeChannels.ASB", version) then
            failwith "Package identity does not match this run."
        expected

    let packageHash path =
        use stream = File.OpenRead path
        SHA256.HashData stream |> Convert.ToHexString

    let assertTestLibrary artifact package =
        use archive = ZipFile.OpenRead package
        use assembly = archive.GetEntry("lib/net10.0/DatatypeChannels.ASB.dll").Open()
        let packagedHash = SHA256.HashData assembly |> Convert.ToHexString
        let testedHash = packageHash (Path.Combine(artifact, "tests/DatatypeChannels.ASB.dll"))
        if packagedHash <> testedHash then
            failwith "The package library differs from the integration test library."

    let verifyArtifact artifact =
        let package = packageFile (Path.Combine(artifact, "nuget")) (currentVersion ())
        let expectedHash = File.ReadAllText(Path.Combine(artifact, "package.sha256")).Trim()
        if packageHash package <> expectedHash then failwith "Package hash differs from the build artifact."
        assertTestLibrary artifact package
        package

    let stageReleaseTools artifact =
        // 1ES release jobs have no checkout. Use this exact entrypoint, not a second runner.
        for relativePath in ["build.fsx"; "global.json"; "NuGet.config"; "RELEASE_NOTES.md"] do
            File.Copy(Path.Combine(root, relativePath), Path.Combine(artifact, relativePath), true)

    let stageArtifact artifact =
        let package = packageFile (Path.Combine(artifact, "nuget")) (currentVersion ())
        copyDirectory (Path.Combine(root, "tests/DatatypeChannels.ASB.Tests/bin/Release/net10.0"))
            (Path.Combine(artifact, "tests"))
        assertTestLibrary artifact package
        File.WriteAllText(Path.Combine(artifact, "package.sha256"), packageHash package)
        stageReleaseTools artifact

    let assertTrusted () =
        if not (canTest (required "BUILD_REPOSITORY_NAME") (required "BUILD_REASON")
                    (required "BUILD_SOURCEBRANCH")) then
            failwith "Integration requires a trusted master run."
        // Require code already reviewed on master before Azure login.
        run root "git" ["merge-base"; "--is-ancestor"; "HEAD"; "origin/master"]

    let assertRelease () =
        if not (canRelease (required "BUILD_REPOSITORY_NAME") (required "BUILD_REASON")
                    (required "BUILD_SOURCEBRANCH") (required "RELEASE_REQUESTED")) then
            failwith "Publishing requires explicit opt-in on a manual master run."
        if required "INTEGRATION_SUCCEEDED" <> "true" then
            failwith "Publishing requires successful integration from this run."

    let assertResults (directory: string) =
        let files = Directory.GetFiles(directory, "*.trx")
        if files.Length = 0 then failwith "Integration produced no TRX results."
        for file in files do
            let results = XDocument.Load file
            let counters = results.Descendants() |> Seq.find (fun node -> node.Name.LocalName = "Counters")
            let count name = counters.Attribute(XName.Get name).Value |> Int32.Parse
            let total = count "total"
            if total <= 0 || count "executed" <> total || count "failed" <> 0 || count "passed" <> total then
                failwith "Integration must execute and pass every test."

    let testArtifact artifact =
        // FAKE supports --single-target; the privileged boundary must still fail closed.
        assertTrusted ()
        let directory = Path.Combine(artifact, "tests")
        let settings = Path.Combine(directory, "local.settings.json")
        let results = Path.Combine(artifact, "results")
        let serviceBus = required "SERVICE_BUS_NAMESPACE" |> namespaceFqdn
        if File.Exists settings then failwith "Refusing to overwrite existing integration settings."
        // Do not accept stale results from an earlier attempt.
        if Directory.Exists results then Directory.Delete(results, true)
        File.WriteAllText(settings, JsonSerializer.Serialize {| ServiceBus = serviceBus |})
        try
            dotnet directory ["vstest"; "DatatypeChannels.ASB.Tests.dll"; "--logger:trx"; $"--ResultsDirectory:{results}"]
            assertResults results
        finally
            File.Delete settings

    let preparePublication artifact =
        assertRelease ()
        let package = verifyArtifact artifact
        required "NUGET_PUBLISH_SERVICE_CONNECTION" |> ignore
        // 1ES owns the push via its supported NuGet task; do not hide a raw push in a script.
        printfn "Validated %s (SHA256 %s)." (Path.GetFileName package) (packageHash package)
        printfn "Ready for 1ES.PublishNuGet@1. This target does not publish packages."

Target.description "Remove local build output (no Azure operations)."
Target.create "clean" (fun _ ->
    !! "**/bin"
    ++ "**/obj"
    ++ "out"
    |> Seq.iter Shell.cleanDir
)

Target.description "Restore local solution dependencies."
Target.create "restore" (fun _ ->
    DotNet.restore (fun options -> { options with ConfigFile = Some Pipeline.nugetConfig }) "."
)

Target.description "Build/publish local solution output without running integration tests."
Target.create "build" (fun _ ->
    let args = "--no-restore"
    DotNet.publish (fun a -> a.WithCommon (fun c -> { c with CustomParams = Some args})) "."
)

Target.description "Run local integration tests; requires settings and Azure identity (default)."
Target.create "test" (fun _ ->
    let args = "--no-restore"
    DotNet.test (fun a -> a.WithCommon (fun c -> { c with CustomParams = Some args})) "."
)

Target.description "Build local development packages and copy them to out."
Target.create "package" (fun _ ->
    let args = sprintf "/p:Version=%s --no-restore" ver.Value.AsString
    DotNet.pack (fun a -> a.WithCommon (fun c -> { c with CustomParams = Some args })) "."
    !! "src/DatatypeChannels.ASB/bin/Release/*.nupkg"
    |> Shell.copyFiles "out"
)

Target.description "Generate local package and documentation metadata."
Target.create "meta" (fun _ ->
    [ "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">"
      "<PropertyGroup>"
      "<Copyright>(c) Microsoft Corporation. All rights reserved.</Copyright>"
      "<Authors>Eugene Tolmachev</Authors>"
      sprintf "<PackageProjectUrl>%s</PackageProjectUrl>" gitIO
      sprintf "<RepositoryUrl>%s/%s</RepositoryUrl>" gitHome gitName
      "<PackageLicense>MIT</PackageLicense>"
      sprintf "<PackageReleaseNotes>%s</PackageReleaseNotes>" (List.head release.Value.Notes)
      sprintf "<PackageIconUrl>%s/master/docs/content/logo.png</PackageIconUrl>" gitContent
      "<PackageTags>Azure Service Bus;Datatype Channel;fsharp</PackageTags>"
      sprintf "<Version>%s</Version>" (string ver.Value)
      sprintf "<FsDocsLogoSource>%s/master/docs/img/logo.png</FsDocsLogoSource>" gitContent
      sprintf "<FsDocsLicenseLink>%s/%s/blob/master/LICENSE</FsDocsLicenseLink>" gitHome gitName
      sprintf "<FsDocsReleaseNotesLink>%s/%s/blob/master/RELEASE_NOTES.md</FsDocsReleaseNotesLink>" gitHome gitName
      "<FsDocsNavbarPosition>fixed-right</FsDocsNavbarPosition>"
      "<FsDocsWarnOnMissingDocs>true</FsDocsWarnOnMissingDocs>"
      "<FsDocsTheme>default</FsDocsTheme>"
      "</PropertyGroup>"
      "</Project>"]
    |> File.write false "Directory.Build.props"
)

Target.description "Generate documentation after local integration tests."
Target.create "generateDocs" (fun _ ->
   Shell.cleanDir ".fsdocs"
   DotNet.exec id "fsdocs" "build --clean" |> ignore
)

Target.description "Watch documentation using the preinstalled/restored fsdocs tool."
Target.create "watchDocs" (fun _ ->
   Shell.cleanDir ".fsdocs"
   DotNet.exec id "fsdocs" "watch" |> ignore
)

Target.description "Publish documentation to gh-pages; requires Git push permission."
Target.create "releaseDocs" (fun _ ->
    let tempDocsDir = "tmp/gh-pages"
    Shell.cleanDir tempDocsDir
    Git.Repository.cloneSingleBranch "" gitRepo "gh-pages" tempDocsDir
    Git.Repository.fullClean tempDocsDir
    Shell.copyRecursive "output" tempDocsDir true |> Trace.tracefn "%A"
    Git.Staging.stageAll tempDocsDir
    Git.Commit.exec tempDocsDir (sprintf "Update generated documentation for version %s" release.Value.NugetVersion)
    Git.Branches.push tempDocsDir
)

Target.description "Explicit local Azure provisioning: init <region> <resource-group> <namespace>."
Target.create "init" (fun p ->
    let loc, rg, privateName =
        match p.Context.Arguments with
        | [location; rg; name] -> Location location, rg, name
        | _ -> failwith "Init requires parameters: Azure region, resource group to deploy into and Service Bus namespace to create."
    let currentUser = Az.currentUserId()
    ARM.init (currentUser, "User") loc rg privateName
)

Target.description "Retired CI provisioning; always fails. Administrators supply the test namespace."
Target.create "initCI" (fun _ ->
    failwith "CI provisioning is retired. See docs/azure-pipelines.md.")

Target.description "Validate CI configuration and the preinstalled SDK before writing build output."
Target.create "ci-preflight" (fun _ ->
    Pipeline.artifactDirectory () |> ignore
    if not (System.IO.File.Exists Pipeline.nugetConfig) then failwith "NuGet.config is required."
    Pipeline.required "CHECK_DIRECTORY" |> ignore
    Pipeline.currentVersion () |> ignore
    Pipeline.dotnet root ["--version"]
)

Target.description "Run offline pipeline/target contracts; CHECK_DIRECTORY must be external scratch."
Target.create "check" (fun _ ->
    Pipeline.dotnet root
        ["fsi"; System.IO.Path.Combine(root, "tests/Pipeline.Tests.fsx"); Pipeline.required "CHECK_DIRECTORY"]
)

Target.description "Generate CI metadata using Build.BuildId plus PACKAGE_PATCH_OFFSET."
Target.create "ci-meta" (fun _ -> Pipeline.writeMetadata ())

Target.description "Restore the solution using the repository's internal-only NuGet.config."
Target.create "ci-restore" (fun _ ->
    Pipeline.dotnet root ["restore"; Pipeline.solution; "--configfile"; Pipeline.nugetConfig]
)

Target.description "Compile Release library and integration binaries without restoring."
Target.create "ci-build" (fun _ ->
    Pipeline.dotnet root ["publish"; Pipeline.solution; "-c"; "Release"; "--no-restore"]
)

Target.description "Pack the already compiled library without building or restoring."
Target.create "ci-package" (fun _ ->
    let packages = System.IO.Path.Combine(Pipeline.artifactDirectory (), "nuget")
    Pipeline.dotnet root
        ["pack"; Pipeline.library; "-c"; "Release"; "--no-build"; "--no-restore"; "--output"; packages]
)

Target.description "Stage package, matching test binaries, hash and this entrypoint into the 1ES drop."
Target.create "ci-stage" (fun _ -> Pipeline.stageArtifact (Pipeline.artifactDirectory ()))

Target.description "Build/check/package graph without Azure or publication credentials."
Target.create "ci" ignore

Target.description "Require trusted master and ancestry on master before Azure login."
Target.create "check-trust" (fun _ -> Pipeline.assertTrusted ())

Target.description "Validate the current-run package hash, identity and matching test library."
Target.create "integration-artifact" (fun _ ->
    Pipeline.verifyArtifact (Pipeline.artifactDirectory ()) |> ignore
)

Target.description "Test the immutable drop against existing Service Bus; requires trusted Azure login."
Target.create "integration" (fun _ -> Pipeline.testArtifact (Pipeline.artifactDirectory ()))

Target.description "Require manual protected master, explicit opt-in and successful integration."
Target.create "publish-check" (fun _ -> Pipeline.assertRelease ())

Target.description "Verify the tested artifact without checkout, restore, clean, build or pack."
Target.create "publish-artifact" (fun _ ->
    Pipeline.verifyArtifact (Pipeline.artifactDirectory ()) |> ignore
)

Target.description "Validate the tested package for 1ES.PublishNuGet@1; the governed task owns the push."
Target.create "publish" (fun _ -> Pipeline.preparePublication (Pipeline.artifactDirectory ()))

Target.description "Release preparation alias for the artifact-only publish graph (never builds or pushes)."
Target.create "release" ignore

Target.description "Show FAKE dependency graphs without running target bodies: graph [target ...]."
Target.create "graph" (fun parameters ->
    match parameters.Context.Arguments with
    | [] ->
        Target.listAvailable ()
        printfn "Usage: dotnet fsi build.fsx -t graph <target> [<target> ...]"
    | targets -> targets |> List.iter (Target.printDependencyGraph true)
)

"clean"
  ==> "restore"
  ==> "meta"
  ==> "build"
  ==> "test"
  ==> "generateDocs"

"build"
  ==> "package"

"releaseDocs"
  <== ["test"; "generateDocs" ]

// Trust domains are separate graphs, not conditionals inside a second runner.
"ci-meta" <== ["ci-preflight"; "check"]

"ci-meta"
  ==> "ci-restore"
  ==> "ci-build"
  ==> "ci-package"
  ==> "ci-stage"
  ==> "ci"

"check-trust"
  ==> "integration-artifact"
  ==> "integration"

// Integration and approvals are enforced by the official YAML across jobs.
// Never attach build/pack/clean or live integration to the publication graph.
"publish-check"
  ==> "publish-artifact"
  ==> "publish"
  ==> "release"

if isEntryPoint then Target.runOrDefaultWithArguments "test"