module internal DatatypeChannels.ASB.Diagnostics

open System.Diagnostics

let formatName name : string =
    $"DatatypeChannels.ASB.{name}"

let mkActivitySource name : ActivitySource =
    new ActivitySource(formatName name)

let createActivity name (kind: ActivityKind) (source: ActivitySource) : Activity =
    match source.CreateActivity(name, kind) with
    | null -> new Activity(name)
    | a -> a

let startActivity name (kind: ActivityKind) (source: ActivitySource) : Activity =
    source
    |> createActivity name kind
    |> fun a -> a.Start()

[<RequireQualifiedAccess>]
module ActivityTagsCollection =
    let ofException (e: exn) =
        ActivityTagsCollection([System.Collections.Generic.KeyValuePair("Exception",e.Message)])