open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.IO.Compression
open System.Text.Json
open System.Text.RegularExpressions
open System.Xml.Linq

let isTag (branch: string) =
    branch.StartsWith("refs/tags/", StringComparison.Ordinal) && branch.Length > 10

let canTest repository reason branch =
    repository = "Azure/DatatypeChannels.ASB"
    && List.contains reason ["IndividualCI"; "BatchedCI"; "Manual"]
    && (branch = "refs/heads/master" || isTag branch)

let canRelease repository reason branch requested =
    canTest repository reason branch && reason = "Manual" && isTag branch
    && String.Equals(requested, "true", StringComparison.OrdinalIgnoreCase)

let required name =
    match Environment.GetEnvironmentVariable name with
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

let httpsUrl name (value: string) =
    match Uri.TryCreate(value, UriKind.Absolute) with
    | true, uri when uri.Scheme = Uri.UriSchemeHttps && uri.UserInfo = "" -> value
    | _ -> failwithf "%s must be an HTTPS URL without embedded credentials." name

let metadata (version: string) (note: string) =
    let repository = "https://github.com/Azure/DatatypeChannels.ASB"
    let properties =
        [ "Copyright", "(c) Microsoft Corporation. All rights reserved."
          "Authors", "Eugene Tolmachev"
          "PackageProjectUrl", "https://Azure.github.io/DatatypeChannels.ASB"
          "RepositoryUrl", repository
          "PackageLicense", "MIT"
          "PackageReleaseNotes", note
          "PackageIconUrl", "https://raw.githubusercontent.com/Azure/DatatypeChannels.ASB/master/docs/content/logo.png"
          "PackageTags", "Azure Service Bus;Datatype Channel;fsharp"
          "Version", version ]
    let elements = properties |> List.map (fun (name, value) -> XElement(XName.Get name, value))
    XElement(XName.Get "Project", XElement(XName.Get "PropertyGroup", elements)).ToString()

let run (directory: string) (program: string) (arguments: string list) =
    let start = ProcessStartInfo(program, WorkingDirectory = directory, UseShellExecute = false)
    arguments |> List.iter start.ArgumentList.Add
    use child = Process.Start start
    child.WaitForExit()
    // Arguments can contain the release key.
    if child.ExitCode <> 0 then failwithf "%s failed with exit code %d." program child.ExitCode

let dotnet directory arguments = run directory "dotnet" arguments
let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let solution = Path.Combine(root, "DatatypeChannels.ASB.sln")
let library = Path.Combine(root, "src/DatatypeChannels.ASB/DatatypeChannels.ASB.fsproj")

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
    let entry = archive.Entries |> Seq.find (fun entry -> entry.FullName = "DatatypeChannels.ASB.nuspec")
    use stream = entry.Open()
    let document = XDocument.Load stream
    let value name = document.Descendants() |> Seq.find (fun node -> node.Name.LocalName = name) |> fun node -> node.Value
    value "id", value "version"

let packageFile (directory: string) version =
    let expected = Path.Combine(directory, $"DatatypeChannels.ASB.{version}.nupkg")
    if Directory.GetFiles(directory, "*.nupkg") <> [|expected|] then
        failwith "Expected exactly the package produced by this run."
    if packageIdentity expected <> ("DatatypeChannels.ASB", version) then
        failwith "Package identity does not match this run."
    expected

let build artifact =
    let source = required "NUGET_RESTORE_SOURCE" |> httpsUrl "NUGET_RESTORE_SOURCE"
    writeMetadata ()
    dotnet root ["restore"; solution; "--source"; source]
    dotnet root ["publish"; solution; "-c"; "Release"; "--no-restore"]
    let packages = Path.Combine(artifact, "nuget")
    dotnet root ["pack"; library; "-c"; "Release"; "--no-build"; "--no-restore"; "--output"; packages]
    packageFile packages (currentVersion ()) |> ignore
    copyDirectory (Path.Combine(root, "tests/DatatypeChannels.ASB.Tests/bin/Release/net10.0")) (Path.Combine(artifact, "tests"))

let assertTrusted () =
    if not (canTest (required "BUILD_REPOSITORY_NAME") (required "BUILD_REASON") (required "BUILD_SOURCEBRANCH")) then
        failwith "Integration requires a trusted master or tag run."
    // Tags must point to code already reviewed on master.
    run root "git" ["merge-base"; "--is-ancestor"; "HEAD"; "origin/master"]

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

let test artifact =
    assertTrusted ()
    let directory = Path.Combine(artifact, "tests")
    let settings = Path.Combine(directory, "local.settings.json")
    let results = Path.Combine(artifact, "results")
    let serviceBus = required "SERVICE_BUS_NAMESPACE" |> namespaceFqdn
    File.WriteAllText(settings, JsonSerializer.Serialize {| ServiceBus = serviceBus |})
    try
        dotnet directory ["vstest"; "DatatypeChannels.ASB.Tests.dll"; "--logger:trx"; $"--ResultsDirectory:{results}"]
        assertResults results
    finally
        File.Delete settings

let publish artifact =
    if not (canRelease (required "BUILD_REPOSITORY_NAME") (required "BUILD_REASON")
                (required "BUILD_SOURCEBRANCH") (required "RELEASE_REQUESTED")) then
        failwith "Publishing requires explicit opt-in on a manual tag run."
    assertTrusted ()
    let package = packageFile (Path.Combine(artifact, "nuget")) (currentVersion ())
    let source = required "NUGET_REPO_URL" |> httpsUrl "NUGET_REPO_URL"
    let key = required "NUGET_REPO_KEY"
    dotnet root ["nuget"; "push"; package; "--source"; source; "--api-key"; key]
