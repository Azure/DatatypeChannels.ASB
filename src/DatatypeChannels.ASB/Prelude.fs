[<AutoOpen>]
module internal Prelude


[<RequireQualifiedAccess>]
module Task =
    open System
    open System.Threading
    open System.Threading.Tasks

    /// Convert `Task` to `Task<unit>`
    let ignore (t: Task) =
        task { return! t }

    /// Monadic map
    let map (continuation: 'a -> 'b) (t: Task<'a>) =
        task { let! x = t in return continuation x }

    /// Monadic bind
    let bind (continuation: 'a -> Task<'b>) (t: Task<'a>) : Task<'b> =
        task { let! x = t in return! continuation x }

module Assembly =
    open System.Runtime.CompilerServices
    
    [<InternalsVisibleTo("DatatypeChannels.ASB.Tests")>]
    ()