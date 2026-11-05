#!/usr/bin/env -S dotnet fsi
#load "../build.fsx"

open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Text.Json
open System.Xml.Linq
open Build.Pipeline

let mutable count = 0
let check name condition =
    if not condition then failwithf "FAIL: %s" name
    count <- count + 1
    printfn "PASS: %s" name

let rejects name action =
    let rejected = try action (); false with _ -> true
    check name rejected

let configPath = Path.Combine(Build.root, "NuGet.config")
check "repository has explicit NuGet configuration" (File.Exists configPath)
let consumptionFeed =
    "https://pkgs.dev.azure.com/avs-oss/Public/_packaging/OSS-FSharp-Consumption/nuget/v3/index.json"
let config = XDocument.Load configPath
let sources = config.Root.Element(XName.Get "packageSources").Elements() |> Seq.toList
check "NuGet clears inherited package sources first"
    (sources.Head.Name.LocalName = "clear")
check "only the team consumption feed is configured"
    (sources.Length = 2 && sources[1].Name.LocalName = "add"
     && sources[1].Attribute(XName.Get "key").Value = "OSS-FSharp-Consumption"
     && sources[1].Attribute(XName.Get "value").Value = consumptionFeed)
check "NuGet does not inherit public audit sources"
    (config.Root.Element(XName.Get "auditSources").Elements()
     |> Seq.map (fun element -> element.Name.LocalName) |> Seq.toList = ["clear"])
check "NuGet configuration contains no credentials"
    (config.Root.Elements()
     |> Seq.forall (fun element -> List.contains element.Name.LocalName ["packageSources"; "auditSources"]))

let effectiveSources directory =
    let start =
        ProcessStartInfo("dotnet", WorkingDirectory = directory, UseShellExecute = false,
                        RedirectStandardOutput = true, RedirectStandardError = true)
    for argument in ["nuget"; "list"; "source"; "--format"; "short"] do
        start.ArgumentList.Add argument
    use child = Process.Start start
    let output = child.StandardOutput.ReadToEndAsync()
    let errors = child.StandardError.ReadToEndAsync()
    if not (child.WaitForExit(30000)) then
        child.Kill(true)
        failwith "NuGet source probe timed out."
    check "NuGet source probe succeeded" (child.ExitCode = 0)
    errors.GetAwaiter().GetResult() |> ignore
    output.GetAwaiter().GetResult().Trim()

check "normal SDK commands use only the repository feed"
    (effectiveSources Build.root = $"E {consumptionFeed}")
check "FSI test script directory resolves only the repository feed"
    (effectiveSources (Path.Combine(Build.root, "tests")) = $"E {consumptionFeed}")

let repository = "Azure/DatatypeChannels.ASB"
for reason in ["IndividualCI"; "BatchedCI"; "Manual"] do
    check $"{reason} master integration" (canTest repository reason "refs/heads/master")
    check $"{reason} tag cannot integrate" (not (canTest repository reason "refs/tags/v3.1.1"))

for reason in ["PullRequest"; "Schedule"; "ResourceTrigger"; ""] do
    check $"{reason} cannot integrate" (not (canTest repository reason "refs/tags/v3.1.1"))

for branch in ["refs/heads/feature"; "refs/pull/42/merge"; "refs/tags"; "refs/tags/"; ""] do
    check $"untrusted ref {branch}" (not (canTest repository "Manual" branch))

check "fork cannot integrate" (not (canTest "someone/DatatypeChannels.ASB" "Manual" "refs/heads/master"))
check "manual opted-in master can release" (canRelease repository "Manual" "refs/heads/master" "true")
for reason in ["IndividualCI"; "BatchedCI"; "PullRequest"; "Schedule"] do
    check $"{reason} cannot release" (not (canRelease repository reason "refs/heads/master" "true"))
for requested in ["false"; ""; "$(RELEASE_REQUESTED)"] do
    check $"opt-in {requested} fails closed" (not (canRelease repository "Manual" "refs/heads/master" requested))
check "tag cannot release" (not (canRelease repository "Manual" "refs/tags/v3.1.1" "true"))
check "fork cannot release" (not (canRelease "fork/repo" "Manual" "refs/heads/master" "true"))

check "run-number version preserved" (packageVersion "### 3.1.0\n* note" "42" "0" = "3.1.42")
check "offset prevents cutover collisions" (packageVersion "### 3.1.0" "42" "1000" = "3.1.1042")
check "prerelease preserved" (packageVersion "### 1.0.0-beta-1" "7" "0" = "1.0.7-beta-1")
for number in [""; "0"; "-1"; "x"; "4294967296"] do
    rejects $"invalid build number {number}" (fun () -> packageVersion "### 3.1.0" number "0" |> ignore)
rejects "overflow" (fun () -> packageVersion "### 3.1.0" "4294967295" "1" |> ignore)
rejects "invalid notes" (fun () -> packageVersion "not a version" "1" "0" |> ignore)

check "namespace accepted" (namespaceFqdn "datatype-channels-ci.servicebus.windows.net" = "datatype-channels-ci.servicebus.windows.net")
for value in [""; "$(SERVICE_BUS_NAMESPACE)"; "Endpoint=sb://x"; "https://x.servicebus.windows.net"; "x.example.com"] do
    rejects $"invalid namespace {value}" (fun () -> namespaceFqdn value |> ignore)

let xml = XDocument.Parse(metadata "3.1.42" "A & B < C")
let property name = xml.Root.Element(XName.Get "PropertyGroup").Element(XName.Get name).Value
check "metadata version" (property "Version" = "3.1.42")
check "release notes XML escaped" (property "PackageReleaseNotes" = "A & B < C")
check "package author retained" (property "Authors" = "Eugene Tolmachev")
check "repository metadata retained" (property "RepositoryUrl" = "https://github.com/Azure/DatatypeChannels.ASB")

let fixtureRoot =
    match fsi.CommandLineArgs |> Array.skip 1 with
    | [| directory |] -> Path.Combine(Path.GetFullPath directory, Guid.NewGuid().ToString("N"))
    | _ -> failwith "Provide a scratch directory outside the repository for artifact/result fixtures."

let writePackage path id version =
    use archive = ZipFile.Open(path, ZipArchiveMode.Create)
    let writeEntry name content =
        use writer = new StreamWriter(archive.CreateEntry(name).Open())
        writer.Write(content: string)
    writeEntry "DatatypeChannels.ASB.nuspec"
        $"<package><metadata><id>{id}</id><version>{version}</version></metadata></package>"
    writeEntry "lib/net10.0/DatatypeChannels.ASB.dll" "fixture-library"

let writeResults path total executed passed failed =
    File.WriteAllText(path,
        $"<TestRun><ResultSummary><Counters total='{total}' executed='{executed}' passed='{passed}' failed='{failed}' /></ResultSummary></TestRun>")

Directory.CreateDirectory fixtureRoot |> ignore
try
    stageReleaseTools fixtureRoot
    for relativePath in
        ["build.fsx"; "global.json"; "NuGet.config"; "RELEASE_NOTES.md"] do
        let actual = File.ReadAllBytes(Path.Combine(fixtureRoot, relativePath))
        let expected = File.ReadAllBytes(Path.Combine(Build.root, relativePath))
        check $"release artifact includes exact {relativePath}" (actual = expected)
    check "checkout-free artifact resolves only the repository feed"
        (effectiveSources fixtureRoot = $"E {consumptionFeed}")
    let parent = Path.Combine(fixtureRoot, "source-parent")
    let child = Path.Combine(parent, "child")
    Directory.CreateDirectory child |> ignore
    File.WriteAllText(Path.Combine(parent, "NuGet.config"),
        "<configuration><packageSources><add key='inherited' value='https://inherited.example.invalid/v3/index.json' /></packageSources></configuration>")
    File.Copy(configPath, Path.Combine(child, "NuGet.config"))
    check "repository config clears an inherited source in real NuGet resolution"
        (effectiveSources child = $"E {consumptionFeed}")
    check "release artifact has no source checkout"
        (not (Directory.Exists(Path.Combine(fixtureRoot, ".git"))))
    // The real artifact-hosted release target must validate without checkout or
    // publishing. Only the governed YAML task can acquire feed credentials.
    let packages = Path.Combine(fixtureRoot, "nuget")
    Directory.CreateDirectory packages |> ignore
    let artifactVersion =
        packageVersion (File.ReadAllText(Path.Combine(fixtureRoot, "RELEASE_NOTES.md"))) "42" "0"
    let artifactPackage = Path.Combine(packages, $"DatatypeChannels.ASB.{artifactVersion}.nupkg")
    writePackage artifactPackage "DatatypeChannels.ASB" artifactVersion
    Directory.CreateDirectory(Path.Combine(fixtureRoot, "tests")) |> ignore
    let testLibrary = Path.Combine(fixtureRoot, "tests/DatatypeChannels.ASB.dll")
    File.WriteAllText(testLibrary, "fixture-library")
    let originalHash = packageHash artifactPackage
    File.WriteAllText(Path.Combine(fixtureRoot, "package.sha256"), originalHash)
    let releaseEnvironment =
        ["BUILD_REPOSITORY_NAME", repository; "BUILD_REASON", "Manual"
         "BUILD_SOURCEBRANCH", "refs/heads/master"; "RELEASE_REQUESTED", "true"
         "BUILD_BUILDID", "42"; "PACKAGE_PATCH_OFFSET", "0"
         "ARTIFACT_DIRECTORY", fixtureRoot; "INTEGRATION_SUCCEEDED", "true"]
    let invoke arguments environment =
        let start =
            ProcessStartInfo(Environment.ProcessPath, WorkingDirectory = fixtureRoot, UseShellExecute = false,
                             RedirectStandardOutput = true, RedirectStandardError = true)
        for argument in
            [System.Reflection.Assembly.GetEntryAssembly().Location
             Path.Combine(fixtureRoot, "build.fsx")] @ arguments do
            start.ArgumentList.Add argument
        // Never inherit usable publication or Azure configuration from the caller.
        for name in
            ["BUILD_REPOSITORY_NAME"; "BUILD_REASON"; "BUILD_SOURCEBRANCH"; "BUILD_BUILDID"
             "PACKAGE_PATCH_OFFSET"; "RELEASE_REQUESTED"; "INTEGRATION_SUCCEEDED"
             "ARTIFACT_DIRECTORY"; "CHECK_DIRECTORY"
             "NUGET_PUBLISH_SERVICE_CONNECTION"
             "SERVICE_BUS_NAMESPACE"; "AZURE_CLIENT_ID"; "AZURE_TENANT_ID"
             "AZURE_CLIENT_SECRET"; "AZURE_FEDERATED_TOKEN_FILE"] do
            start.Environment.Remove name |> ignore
        start.Environment["AZURE_CONFIG_DIR"] <- Path.Combine(fixtureRoot, "no-azure-session")
        // FSI uses the absolute installed host. No build, Git or Azure CLI is on PATH.
        start.Environment["PATH"] <- Path.Combine(fixtureRoot, "no-tools")
        for name, value in environment do start.Environment[name] <- value
        start.Environment["NUGET_REPO_URL"] <- ""
        start.Environment["NUGET_REPO_KEY"] <- ""
        use child = Process.Start start
        let output = child.StandardOutput.ReadToEndAsync()
        let errors = child.StandardError.ReadToEndAsync()
        if not (child.WaitForExit(120000)) then
            child.Kill(true)
            failwith "Build probe timed out."
        child.ExitCode, output.Result + errors.Result

    let expect name expectedExit message arguments environment =
        let code, output = invoke arguments environment
        if (code = 0) <> expectedExit || not (output.Contains(message: string)) then
            printfn "Build probe diagnostic: %s" output
        check name ((code = 0) = expectedExit && output.Contains message)
        output

    // No release notes or solution exist during introspection; dependency config remains.
    let notes = Path.Combine(fixtureRoot, "RELEASE_NOTES.md")
    let notesText = File.ReadAllText notes
    File.Delete notes
    expect "actual help without application settings or release notes" true "Target Module options" ["--help"] [] |> ignore
    let listing = expect "actual list advertises unified targets" true "integration" ["--list"] []
    check "list includes artifact publisher" (listing.Contains("publish") && listing.Contains("ci-package"))
    let graph =
        expect "actual graph without application settings or release notes" true "DependencyGraph for Target publish"
            ["-t"; "graph"; "publish"; "ci"; "integration"; "test"] []
    check "graph did not run build or cloud targets"
        (not (graph.Contains("Starting target 'ci-build'") || graph.Contains("Starting target 'integration'")))
    let info = Path.Combine(fixtureRoot, "targets.json")
    expect "FAKE machine-readable graph export" true "" ["--write-info"; info] [] |> ignore
    use document = JsonDocument.Parse(File.ReadAllText info)
    let targets =
        document.RootElement.GetProperty("targets").EnumerateArray()
        |> Seq.map (fun target -> target.GetProperty("name").GetString(), target)
        |> Map.ofSeq
    let dependencies name =
        targets[name].GetProperty("hardDependencies").EnumerateArray()
        |> Seq.map (fun target -> target.GetProperty("name").GetString())
        |> Set.ofSeq
    let rec closure name =
        dependencies name
        |> Set.fold (fun found dependency -> Set.union found (closure dependency)) (Set.singleton name)
    for KeyValue(name, target) in targets do
        check $"description for {name}"
            (not (String.IsNullOrWhiteSpace(target.GetProperty("description").GetString())))
    check "publish exact no-rebuild closure"
        (closure "publish" = set ["publish"; "publish-artifact"; "publish-check"])
    check "release exact no-rebuild closure"
        (closure "release" = set ["release"; "publish"; "publish-artifact"; "publish-check"])
    check "CI exact credential-free closure"
        (closure "ci" =
            set ["ci"; "ci-stage"; "ci-package"; "ci-build"; "ci-restore"; "ci-meta"; "check"; "ci-preflight"])
    check "integration exact tested-artifact closure"
        (closure "integration" = set ["integration"; "integration-artifact"; "check-trust"])
    check "pack has an explicit compiled-build dependency" (dependencies "ci-package" = set ["ci-build"])
    check "local test graph preserved" (closure "test" = set ["test"; "build"; "meta"; "restore"; "clean"])
    check "local package graph preserved" (closure "package" = set ["package"; "build"; "meta"; "restore"; "clean"])
    check "offline checks independent of build/auth" (closure "check" = set ["check"])
    File.WriteAllText(notes, notesText)

    expect "CI missing configuration fails before writing output" false
        "Configure ARTIFACT_DIRECTORY" ["-t"; "ci-preflight"] [] |> ignore
    let missingTool = Path.Combine(fixtureRoot, "missing-tool.exe")
    let missingToolMessage =
        try run fixtureRoot missingTool []; ""
        with error -> error.Message
    check "missing preinstalled tool has explicit diagnostic"
        (missingToolMessage.Contains("Required preinstalled tool") && missingToolMessage.Contains("is unavailable"))
    check "preflight did not write build metadata"
        (not (File.Exists(Path.Combine(fixtureRoot, "Directory.Build.props"))))
    let messages =
        expect "release target reaches missing-connection rejection" false "Configure NUGET_PUBLISH_SERVICE_CONNECTION"
            ["-t"; "publish"] releaseEnvironment
    check "release target rejects missing connection without requiring git"
        (messages.Contains("Configure NUGET_PUBLISH_SERVICE_CONNECTION") && not (messages.Contains("git failed")))
    expect "release target prepares but does not push" true "This target does not publish packages."
        ["-t"; "publish"]
        (releaseEnvironment @ [("NUGET_PUBLISH_SERVICE_CONNECTION", "nonexistent-test-connection")]) |> ignore
    check "publication preflight preserves exact tested package hash" (packageHash artifactPackage = originalHash)
    expect "release requires successful integration" false "successful integration"
        ["-t"; "publish"] (releaseEnvironment @ [("INTEGRATION_SUCCEEDED", "false")]) |> ignore
    expect "release execution requires explicit opt-in" false "manual master run"
        ["-t"; "publish"] (releaseEnvironment @ [("RELEASE_REQUESTED", "false")]) |> ignore
    expect "fork execution cannot release" false "manual master run"
        ["-t"; "publish"] (releaseEnvironment @ [("BUILD_REPOSITORY_NAME", "fork/repo")]) |> ignore
    for reason in ["PullRequest"; "IndividualCI"] do
        expect $"{reason} execution cannot publish" false "manual master run"
            ["-t"; "publish"] (releaseEnvironment @ [("BUILD_REASON", reason)]) |> ignore
    for branch in ["refs/heads/feature"; "refs/tags/v3.1.42"] do
        expect $"{branch} execution cannot publish" false "manual master run" ["-t"; "publish"]
            (releaseEnvironment @ [("BUILD_SOURCEBRANCH", branch)]) |> ignore
    expect "single-target cannot bypass release guard" false "manual master run" ["-s"; "-t"; "publish"]
        (releaseEnvironment @ [("BUILD_REASON", "PullRequest")]) |> ignore
    expect "untrusted integration denied before tools" false "trusted master run"
        ["-t"; "integration"] (releaseEnvironment @ [("BUILD_REASON", "PullRequest")]) |> ignore
    expect "single-target cannot bypass integration guard" false "trusted master run"
        ["-s"; "-t"; "integration"] (releaseEnvironment @ [("BUILD_REASON", "PullRequest")]) |> ignore
    File.WriteAllText(testLibrary, "different-library")
    expect "different tested library rejected" false "differs from the integration test library"
        ["-t"; "publish"] releaseEnvironment |> ignore
    File.WriteAllText(testLibrary, "fixture-library")
    File.AppendAllText(artifactPackage, "changed bytes")
    expect "tampered package rejected before feed lookup" false "Package hash differs"
        ["-t"; "publish"] releaseEnvironment |> ignore
    let package = Path.Combine(fixtureRoot, "DatatypeChannels.ASB.3.1.42.nupkg")
    rejects "missing package" (fun () -> packageFile fixtureRoot "3.1.42" |> ignore)
    writePackage package "DatatypeChannels.ASB" "3.1.42"
    check "exact artifact accepted" (packageFile fixtureRoot "3.1.42" = package)
    rejects "wrong run version" (fun () -> packageFile fixtureRoot "3.1.43" |> ignore)
    let extra = Path.Combine(fixtureRoot, "extra.nupkg")
    File.Copy(package, extra)
    rejects "extra package rejected" (fun () -> packageFile fixtureRoot "3.1.42" |> ignore)
    File.Delete extra
    File.Delete package
    writePackage package "Other.Package" "3.1.42"
    rejects "wrong nuspec ID" (fun () -> packageFile fixtureRoot "3.1.42" |> ignore)
    File.Delete package
    writePackage package "DatatypeChannels.ASB" "3.1.43"
    rejects "wrong nuspec version" (fun () -> packageFile fixtureRoot "3.1.42" |> ignore)
    rejects "missing TRX" (fun () -> assertResults fixtureRoot)
    let results = Path.Combine(fixtureRoot, "tests.trx")
    writeResults results 2 2 2 0
    assertResults fixtureRoot
    check "passing integration results accepted" true
    for (name, total, executed, passed, failed) in
        [ "zero tests", 0, 0, 0, 0
          "failed tests", 2, 2, 1, 1
          "skipped tests", 2, 1, 1, 0
          "inconsistent counters", 0, 1, 0, 0
          "negative counters", -1, -1, -1, 0 ] do
        writeResults results total executed passed failed
        rejects name (fun () -> assertResults fixtureRoot)
finally
    Directory.Delete(fixtureRoot, true)

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let yaml =
    File.ReadAllText(Path.Combine(root, ".pipelines/azure-pipelines.yml")).Replace("\r\n", "\n")
let readOptional name =
    let path = Path.Combine(root, name)
    if File.Exists path then (File.ReadAllText path).Replace("\r\n", "\n") else ""

let prYaml = readOptional ".pipelines/azure-pipelines-pr.yml"
let buildYaml = readOptional ".pipelines/build.yml"
let combinedYaml = String.concat "\n" [yaml; prYaml; buildYaml]

for path in ["azure-pipelines.yml"; "azure-pipelines-pr.yml"; "eng/pipelines/build.yml"] do
    check $"pipeline YAML moved out of {path}" (not (File.Exists(Path.Combine(root, path))))
let dependabot = readOptional ".github/dependabot.yml"
check "Dependabot configuration retained"
    (dependabot =
        ("""version: 2
updates:
  - package-ecosystem: "github-actions"
    directory: "/"
    groups:
      github-actions:
        patterns: ["*"]
    schedule:
      interval: "weekly"
    cooldown:
      default-days: 7
""").Replace("\r\n", "\n"))

// These are repository contracts, not a YAML parser or 1ES expansion engine.
let section (start: string) (finish: string) (text: string) =
    let first = text.IndexOf(start, StringComparison.Ordinal)
    if first < 0 then ""
    else
        let last = text.IndexOf(finish, first + start.Length, StringComparison.Ordinal)
        if last < 0 then text.Substring first else text.Substring(first, last - first)

let release = section "      - stage: Release" "\n    - stage:" yaml
let integration = section "    - stage: Integration" "\n    - ${{" yaml

check "trusted build authenticates before FSI dependency resolution"
    (yaml.Contains("authenticateRestore: true")
     && buildYaml.Contains("- ${{ if eq(parameters.authenticateRestore, true) }}:\n      - task: NuGetAuthenticate@1")
     && buildYaml.IndexOf("task: NuGetAuthenticate@1", StringComparison.Ordinal)
        < buildYaml.IndexOf("dotnet fsi", StringComparison.Ordinal))
check "integration authenticates before its first FSI invocation"
    (integration.Contains("task: NuGetAuthenticate@1")
     && integration.IndexOf("task: NuGetAuthenticate@1", StringComparison.Ordinal)
        < integration.IndexOf("dotnet fsi", StringComparison.Ordinal))
check "PR restore authentication defaults to off"
    (not (prYaml.Contains("authenticateRestore:"))
     && buildYaml.Contains("name: authenticateRestore\n  type: boolean\n  default: false"))
check "release cannot authenticate dependency acquisition"
    (not (release.Contains("NuGetAuthenticate@") || release.Contains("NUGET_RESTORE_SERVICE_CONNECTION")))

check "official trusted template"
    (yaml.Contains("template: v1/1ES.Official.PipelineTemplate.yml@1esPipelines"))
check "unofficial PR template"
    (prYaml.Contains("template: v1/1ES.Unofficial.PipelineTemplate.yml@1esPipelines"))
for name, text in ["trusted", yaml; "PR", prYaml] do
    check $"{name} approved template repository"
        (text.Contains("repository: 1esPipelines\n    type: git")
         && text.Contains("name: 1ESPipelineTemplates/1ESPipelineTemplates")
         && text.Contains("ref: refs/tags/release"))
    check $"{name} stages are nested under extends parameters"
        (text.Contains("extends:\n  template:")
         && text.Contains("\n  parameters:\n")
         && text.Contains("\n    stages:\n")
         && not (text.Contains("\nstages:")))
    check $"{name} shares only build stage"
        (text.Contains("- template: /.pipelines/build.yml@self"))
    check $"{name} explicit Windows pool and source analysis"
        (text.Contains("\n    pool:\n      name: $(")
         && text.Contains("\n      image: $(")
         && text.Contains("\n      os: windows")
         && text.Contains("sourceAnalysisPool:\n        name: $(")
         && text.Contains("\n        image: $(")
         && text.Contains("\n        os: windows"))
    check $"{name} no copied Management resources"
        (not (text.Contains("avs-management-") || text.Contains("Scripting Metadata")))

check "PR has no CI trigger" (prYaml.Contains("\ntrigger: none\n"))
check "PR targets master and skips drafts"
    (prYaml.Contains("\npr:\n  autoCancel: true\n  drafts: false\n  branches:\n    include:\n    - master"))
check "trusted definition disables PRs" (yaml.Contains("\npr: none\n"))
check "trusted triggers master only, never tags"
    (yaml.Contains("trigger:\n  branches:\n    include:\n    - master")
     && not (yaml.Contains("startsWith(variables['Build.SourceBranch'], 'refs/tags/')")
             || yaml.Contains("\n  tags:")))
check "trusted build excludes untrusted invocation"
    (yaml.Contains("condition: >-\n          and(succeeded(),")
     && yaml.Contains("in(variables['Build.Reason'], 'IndividualCI', 'BatchedCI', 'Manual')"))
for forbidden in
    ["AzureCLI@"; "azureSubscription:"; "NUGET_REPO_KEY"; "publishFeedCredentials:"; "- group:"; "environment:";
     "publishPackage"; "TRUSTED_"; "stage: Integration"; "stage: Release"] do
    check $"PR/shared build cannot reference {forbidden}"
        (not (prYaml.Contains forbidden || buildYaml.Contains forbidden))

check "governed artifact output"
    (buildYaml.Contains("templateContext:\n      outputs:\n      - output: pipelineArtifact")
     && buildYaml.Contains("artifactName: drop")
     && buildYaml.Contains("targetPath: $(Build.ArtifactStagingDirectory)/drop"))
check "no direct artifact publisher"
    (not (combinedYaml.Contains("- publish:") || combinedYaml.Contains("PublishPipelineArtifact@")))
check "no SDL or signing bypass"
    (not (combinedYaml.Contains("skipSDL:") || combinedYaml.Contains("enabled: false")
          || combinedYaml.Contains("isProduction: false") || combinedYaml.Contains("sbomEnabled: false")))
check "official signing validation explicitly required"
    (yaml.Contains("codeSignValidation:\n        enabled: true\n        break: true"))
check "integration verifies ancestry before Azure login"
    (integration.IndexOf("check-trust", StringComparison.Ordinal) >= 0
     && integration.IndexOf("check-trust", StringComparison.Ordinal)
        < integration.IndexOf("task: AzureCLI@2", StringComparison.Ordinal))
check "integration uses current drop"
    (integration.Contains("download: current\n          artifact: drop"))
check "Azure login does not export credentials"
    (integration.Contains("addSpnToEnvironment: false") && integration.Contains("useGlobalConfig: false"))
check "release compiled only with explicit opt-in"
    (yaml.Contains("    - ${{ if eq(parameters.publishPackage, true) }}:"))
check "release depends on both build and integration"
    (release.Contains("dependsOn:\n        - Build\n        - Integration"))
check "release is a production governed job"
    (release.Contains("templateContext:\n            type: releaseJob\n            isProduction: true"))
check "release consumes same-run governed input"
    (release.Contains("input: pipelineArtifact\n              artifactName: drop\n              buildType: current")
     && release.Contains("targetPath: $(Pipeline.Workspace)/drop"))
check "release has no checkout or replacement download"
    (release.Contains("checkout: none") && not (release.Contains("checkout: self"))
     && not (release.Contains("download:")))
check "release uses artifact tools and SDK pin"
    (release.Contains("workingDirectory: $(Pipeline.Workspace)/drop")
     && release.Contains("dotnet fsi build.fsx -t publish")
     && release.Contains("ARTIFACT_DIRECTORY: $(Pipeline.Workspace)/drop"))
check "release cannot rebuild or restore"
    (not (release.Contains(" build ") || release.Contains(" pack ") || release.Contains(" restore ")))
check "release credentials belong only to the governed task"
    (release.Contains("task: 1ES.PublishNuGet@1")
     && release.Contains("publishFeedCredentials: $(NUGET_PUBLISH_SERVICE_CONNECTION)")
     && not (combinedYaml.Contains("NUGET_REPO_KEY") || integration.Contains("publishFeedCredentials:")))
check "governed publisher uses only the validated package folder"
    (release.Contains("packagesToPush: '$(Pipeline.Workspace)/drop/nuget/*.nupkg'")
     && release.Contains("packageParentPath: $(Pipeline.Workspace)/drop/nuget")
     && release.IndexOf("build.fsx -t publish", StringComparison.Ordinal)
        < release.IndexOf("task: 1ES.PublishNuGet@1", StringComparison.Ordinal))
check "governed publisher uses installed dotnet and external NuGet connection"
    (release.Contains("useDotNetTask: true") && release.Contains("nuGetFeedType: external")
     && not (release.Contains("allowPackageConflicts: true") || release.Contains("NuGetToolInstaller@")))
check "Actions Azure workflow retired" (not (File.Exists(Path.Combine(root, ".github/workflows/azure.datatypechannels.asb.yml"))))
check "release default is off" (yaml.Contains("default: false"))
check "manual release condition" (yaml.Contains("eq(variables['Build.Reason'], 'Manual')"))
check "release requires protected master" (release.Contains("eq(variables['Build.SourceBranch'], 'refs/heads/master')"))
check "production branch validation is requested" (yaml.Contains("branchValidation: enforceInReleaseJobs"))
check "SBOM generation is explicit" (yaml.Contains("sbom:\n        enabled: true"))
check "SBOM validation is explicit"
    (yaml.Contains("sbom:\n        enabled: true\n        validate: true"))
check "SBOM generation fails closed"
    (yaml.Contains("validate: true\n        continueOnError: false"))
check "successful integration required" (yaml.Contains("eq(dependencies.Integration.result, 'Succeeded')"))
check "no missing-test success" (yaml.Contains("failTaskOnMissingResultsFile: true"))
check "release approval environment" (yaml.Contains("environment: DatatypeChannels-ASB-Release"))
check "no SDK installer" (not (combinedYaml.Contains("UseDotNet@")))
check "no GitHub identity" (not (combinedYaml.Contains("CLIENT_ID") || combinedYaml.Contains("id-token")))
check "only build.fsx orchestrates the build"
    (not (File.Exists(Path.Combine(root, "eng/RunPipeline.fsx")))
     && not (File.Exists(Path.Combine(root, "eng/Pipeline.fsx")))
     && not (combinedYaml.Contains("RunPipeline.fsx")))
check "shared build invokes the CI graph"
    (buildYaml.Contains("dotnet fsi build.fsx -t ci")
     && not (buildYaml.Contains("dotnet fsi tests/")))
check "integration invokes the artifact graph" (integration.Contains("dotnet fsi build.fsx -t integration"))
check "release maps successful stage prerequisite" (release.Contains("INTEGRATION_SUCCEEDED: 'true'"))
let buildSource = File.ReadAllText(Path.Combine(root, "build.fsx"))
check "CI restore explicitly consumes repository config"
    (buildSource.Contains("""["restore"; Pipeline.solution; "--configfile"; Pipeline.nugetConfig]"""))
check "local FAKE restore explicitly consumes repository config"
    (buildSource.Contains("ConfigFile = Some Pipeline.nugetConfig"))
check "no pipeline source override or FSI feed injection"
    (not (buildSource.Contains("NUGET_RESTORE_SOURCE") || combinedYaml.Contains("NUGET_RESTORE_SOURCE")
          || buildSource.Contains("\"--source\"") || buildSource.Contains("#i \"nuget:")))
check "PR does not request feed credentials and shared logic exports no raw token"
    (not (prYaml.Contains("NuGetAuthenticate@") || prYaml.Contains("NUGET_RESTORE_SERVICE_CONNECTION")
          || buildYaml.Contains("System.AccessToken")))
check "no raw publisher or key lookup is hidden in the script"
    (not (buildSource.Contains("\"nuget\"; \"push\"") || buildSource.Contains("NUGET_REPO_KEY")))
printfn "%d checks passed" count
