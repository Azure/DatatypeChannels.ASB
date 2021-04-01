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
        let rec mapResolved (task: Task) =
            match task.Status with
            | TaskStatus.RanToCompletion -> Task.FromResult(())
            | TaskStatus.Faulted -> Task.FromException<unit> (flattenExns task.Exception)
            | TaskStatus.Canceled -> Task.FromCanceled<unit> CancellationToken.None
            | _ -> task.ContinueWith(mapResolved).Unwrap()
        mapResolved t

    /// Monadic map
    let map (continuation: 'a -> 'b) (t: Task<'a>) =
        let rec mapResolved (task: Task<'a>) =
            match task.Status with
            | TaskStatus.RanToCompletion -> try Task.FromResult(continuation task.Result) with ex -> Task.FromException<'b> ex 
            | TaskStatus.Faulted -> Task.FromException<'b> (flattenExns task.Exception)
            | TaskStatus.Canceled -> Task.FromCanceled<'b> CancellationToken.None
            | _ -> task.ContinueWith(mapResolved).Unwrap()
        mapResolved t

    /// Monadic bind
    let bind (continuation: 'a -> Task<'b>) (t: Task<'a>) : Task<'b> =
        let rec mapResolved (task: Task<'a>) =
            match task.Status with
            | TaskStatus.RanToCompletion -> task.ContinueWith(Func<Task<'a>,_> (fun t -> t.Result |> continuation)).Unwrap()
            | TaskStatus.Faulted -> Task.FromException<'b> (flattenExns task.Exception)
            | TaskStatus.Canceled -> Task.FromCanceled<'b> CancellationToken.None
            | _ -> task.ContinueWith(mapResolved).Unwrap()
        mapResolved t

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