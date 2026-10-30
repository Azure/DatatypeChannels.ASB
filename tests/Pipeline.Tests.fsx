#!/usr/bin/env -S dotnet fsi
#load "../eng/Pipeline.fsx"

open System
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
let yaml = File.ReadAllText(Path.Combine(root, "azure-pipelines.yml"))
check "Actions Azure workflow retired" (not (File.Exists(Path.Combine(root, ".github/workflows/azure.datatypechannels.asb.yml"))))
check "release default is off" (yaml.Contains("default: false"))
check "manual release condition" (yaml.Contains("eq(variables['Build.Reason'], 'Manual')"))
check "successful integration required" (yaml.Contains("eq(dependencies.Integration.result, 'Succeeded')"))
check "no missing-test success" (yaml.Contains("failTaskOnMissingResultsFile: true"))
check "release consumes current artifact" (yaml.Contains("download: current"))
check "release approval environment" (yaml.Contains("environment: DatatypeChannels-ASB-Release"))
check "no SDK installer" (not (yaml.Contains("UseDotNet@")))
check "no GitHub identity" (not (yaml.Contains("CLIENT_ID") || yaml.Contains("id-token")))
printfn "%d checks passed" count
