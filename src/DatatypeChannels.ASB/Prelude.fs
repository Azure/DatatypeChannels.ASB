[<AutoOpen>]
module internal Prelude


[<RequireQualifiedAccess>]
module Task =
    open System
    open System.Threading
    open System.Threading.Tasks

    let private flattenExns (e: AggregateException) =
        e.Flatten().InnerExceptions.[0]

    /// Convert `Task` to `Task<unit>`
    let ignore (t: Task) =
        task { return! t }

    /// Monadic map
    let map (continuation: 'a -> 'b) (t: Task<'a>) =
        task { let! x = t in return continuation x }

    /// Monadic bind
    let bind (continuation: 'a -> Task<'b>) (t: Task<'a>) : Task<'b> =
        task { let! x = t in return! continuation x }
        

    /// Retry the task up to `n` times with progressive backoff
    let withRetries (n: uint16) (mkTask: 'a -> Task<'r>) (arg: 'a) =
        let rec mapResolved remaining (task: Task<'r>) =
            match task.Status with
            | TaskStatus.RanToCompletion -> Task.FromResult task.Result
            | TaskStatus.Faulted when remaining > 0us ->
                1000us * (n - remaining) |> int |> Task.Delay |> ignore |> bind (fun _ -> mkTask arg) |> mapResolved (remaining - 1us)
            | TaskStatus.Faulted -> Task.FromException<'r> (flattenExns task.Exception)
            | TaskStatus.Canceled -> Task.FromCanceled<'r> CancellationToken.None
            | _ -> task.ContinueWith(mapResolved remaining).Unwrap()
        mkTask arg |> mapResolved n

module Assembly =
    open System.Runtime.CompilerServices
    
    [<InternalsVisibleTo("DatatypeChannels.ASB.Tests")>]
    ()