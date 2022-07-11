[<AutoOpen>]
module internal Prelude
let backgroundTask = FSharp.Control.Tasks.Builders.NonAffine.TaskBuilder() // until https://github.com/dotnet/fsharp/issues/12761 is sorted out

[<RequireQualifiedAccess>]
module Task =
    open System.Threading.Tasks

    /// Convert `Task` to `Task<unit>`
    let ignore (t: Task) =
        backgroundTask { return! t }

    /// Monadic map
    let map (continuation: 'a -> 'b) (t: Task<'a>) =
        backgroundTask { let! x = t in return continuation x }

    /// Monadic bind
    let bind (continuation: 'a -> Task<'b>) (t: Task<'a>) : Task<'b> =
        backgroundTask { let! x = t in return! continuation x }

module Assembly =
    open System.Runtime.CompilerServices
    
    [<InternalsVisibleTo("DatatypeChannels.ASB.Tests")>]
    ()