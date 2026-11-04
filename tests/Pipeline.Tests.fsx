#!/usr/bin/env -S dotnet fsi
#load "../eng/Pipeline.fsx"

open System
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Xml.Linq
open Pipeline

let mutable count = 0
let check name condition =
    if not condition then failwithf "FAIL: %s" name
    count <- count + 1
    printfn "PASS: %s" name

let rejects name action =
    let rejected = try action (); false with _ -> true
    check name rejected

let repository = "Azure/DatatypeChannels.ASB"
for reason in ["IndividualCI"; "BatchedCI"; "Manual"] do
    check $"{reason} master integration" (canTest repository reason "refs/heads/master")
    check $"{reason} tag integration" (canTest repository reason "refs/tags/v3.1.1")

for reason in ["PullRequest"; "Schedule"; "ResourceTrigger"; ""] do
    check $"{reason} cannot integrate" (not (canTest repository reason "refs/tags/v3.1.1"))

for branch in ["refs/heads/feature"; "refs/pull/42/merge"; "refs/tags"; "refs/tags/"; ""] do
    check $"untrusted ref {branch}" (not (canTest repository "Manual" branch))

check "fork cannot integrate" (not (canTest "someone/DatatypeChannels.ASB" "Manual" "refs/heads/master"))
check "manual opted-in tag can release" (canRelease repository "Manual" "refs/tags/v3.1.1" "true")
for reason in ["IndividualCI"; "BatchedCI"; "PullRequest"; "Schedule"] do
    check $"{reason} cannot release" (not (canRelease repository reason "refs/tags/v3.1.1" "true"))
for requested in ["false"; ""; "$(RELEASE_REQUESTED)"] do
    check $"opt-in {requested} fails closed" (not (canRelease repository "Manual" "refs/tags/v3.1.1" requested))
check "master cannot release" (not (canRelease repository "Manual" "refs/heads/master" "true"))
check "fork cannot release" (not (canRelease "fork/repo" "Manual" "refs/tags/v3.1.1" "true"))

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

check "HTTPS feed accepted" (httpsUrl "feed" "https://api.nuget.org/v3/index.json" = "https://api.nuget.org/v3/index.json")
for value in ["http://example.com"; "https://user@example.com"; "$(FEED)"] do
    rejects $"invalid feed {value}" (fun () -> httpsUrl "feed" value |> ignore)

let fixtureRoot =
    match fsi.CommandLineArgs |> Array.skip 1 with
    | [| directory |] -> Path.Combine(Path.GetFullPath directory, Guid.NewGuid().ToString("N"))
    | _ -> failwith "Provide a scratch directory outside the repository for artifact/result fixtures."

let writePackage path id version =
    use archive = ZipFile.Open(path, ZipArchiveMode.Create)
    use writer = new StreamWriter(archive.CreateEntry("DatatypeChannels.ASB.nuspec").Open())
    writer.Write($"<package><metadata><id>{id}</id><version>{version}</version></metadata></package>")

let writeResults path total executed passed failed =
    File.WriteAllText(path,
        $"<TestRun><ResultSummary><Counters total='{total}' executed='{executed}' passed='{passed}' failed='{failed}' /></ResultSummary></TestRun>")

Directory.CreateDirectory fixtureRoot |> ignore
try
    stageReleaseTools fixtureRoot
    for relativePath in
        ["eng/Pipeline.fsx"; "eng/RunPipeline.fsx"; "global.json"; "RELEASE_NOTES.md"] do
        let actual = File.ReadAllBytes(Path.Combine(fixtureRoot, relativePath))
        let expected = File.ReadAllBytes(Path.Combine(Pipeline.root, relativePath))
        check $"release artifact includes exact {relativePath}" (actual = expected)
    check "release artifact has no source checkout"
        (not (Directory.Exists(Path.Combine(fixtureRoot, ".git"))))
    // A real artifact-hosted publisher must reach configuration validation
    // without source checkout. An empty feed/key makes any package push impossible.
    let packages = Path.Combine(fixtureRoot, "nuget")
    Directory.CreateDirectory packages |> ignore
    let artifactVersion =
        packageVersion (File.ReadAllText(Path.Combine(fixtureRoot, "RELEASE_NOTES.md"))) "42" "0"
    writePackage (Path.Combine(packages, $"DatatypeChannels.ASB.{artifactVersion}.nupkg"))
        "DatatypeChannels.ASB" artifactVersion
    let start =
        ProcessStartInfo("dotnet", WorkingDirectory = fixtureRoot, UseShellExecute = false,
                         RedirectStandardOutput = true, RedirectStandardError = true)
    for argument in
        [System.Reflection.Assembly.GetEntryAssembly().Location
         Path.Combine(fixtureRoot, "eng/RunPipeline.fsx"); "publish"; fixtureRoot] do
        start.ArgumentList.Add argument
    for name, value in
        ["BUILD_REPOSITORY_NAME", repository; "BUILD_REASON", "Manual"
         "BUILD_SOURCEBRANCH", "refs/tags/v3.1.42"; "RELEASE_REQUESTED", "true"
         "BUILD_BUILDID", "42"; "PACKAGE_PATCH_OFFSET", "0"
         "NUGET_REPO_URL", ""; "NUGET_REPO_KEY", ""] do
        start.Environment[name] <- value
    use child = Process.Start start
    let output = child.StandardOutput.ReadToEndAsync()
    let errors = child.StandardError.ReadToEndAsync()
    child.WaitForExit()
    let messages = output.Result + errors.Result
    if not (messages.Contains("Configure NUGET_REPO_URL")) then
        printfn "Artifact publisher diagnostic: %s" messages
    check "artifact publisher rejects missing feed without requiring git"
        (child.ExitCode <> 0 && messages.Contains("Configure NUGET_REPO_URL"))
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
let yaml = File.ReadAllText(Path.Combine(root, "azure-pipelines.yml")).Replace("\r\n", "\n")
let readOptional name =
    let path = Path.Combine(root, name)
    if File.Exists path then (File.ReadAllText path).Replace("\r\n", "\n") else ""

let prYaml = readOptional "azure-pipelines-pr.yml"
let buildYaml = readOptional "eng/pipelines/build.yml"
let combinedYaml = String.concat "\n" [yaml; prYaml; buildYaml]

// These are repository contracts, not a YAML parser or 1ES expansion engine.
let section (start: string) (finish: string) (text: string) =
    let first = text.IndexOf(start, StringComparison.Ordinal)
    if first < 0 then ""
    else
        let last = text.IndexOf(finish, first + start.Length, StringComparison.Ordinal)
        if last < 0 then text.Substring first else text.Substring(first, last - first)

let release = section "      - stage: Release" "\n    - stage:" yaml
let integration = section "    - stage: Integration" "\n    - ${{" yaml

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
        (text.Contains("- template: eng/pipelines/build.yml@self"))
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
check "trusted triggers retain master and tags"
    (yaml.Contains("    - master\n  tags:\n    include:\n    - '*'"))
check "trusted build excludes untrusted invocation"
    (yaml.Contains("condition: >-\n          and(succeeded(),")
     && yaml.Contains("in(variables['Build.Reason'], 'IndividualCI', 'BatchedCI', 'Manual')"))
for forbidden in
    ["AzureCLI@"; "azureSubscription:"; "NUGET_REPO_KEY"; "- group:"; "environment:";
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
     && release.Contains("dotnet fsi eng/RunPipeline.fsx publish \"$(Pipeline.Workspace)/drop\""))
check "release cannot rebuild or restore"
    (not (release.Contains(" build ") || release.Contains(" pack ") || release.Contains(" restore ")))
check "release key is scoped to release"
    (release.Contains("NUGET_REPO_KEY: $(NUGET_REPO_KEY)")
     && not (integration.Contains("NUGET_REPO_KEY")))
check "Actions Azure workflow retired" (not (File.Exists(Path.Combine(root, ".github/workflows/azure.datatypechannels.asb.yml"))))
check "release default is off" (yaml.Contains("default: false"))
check "manual release condition" (yaml.Contains("eq(variables['Build.Reason'], 'Manual')"))
check "successful integration required" (yaml.Contains("eq(dependencies.Integration.result, 'Succeeded')"))
check "no missing-test success" (yaml.Contains("failTaskOnMissingResultsFile: true"))
check "release approval environment" (yaml.Contains("environment: DatatypeChannels-ASB-Release"))
check "no SDK installer" (not (combinedYaml.Contains("UseDotNet@")))
check "no GitHub identity" (not (combinedYaml.Contains("CLIENT_ID") || combinedYaml.Contains("id-token")))
printfn "%d checks passed" count
