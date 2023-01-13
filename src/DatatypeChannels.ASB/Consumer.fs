/// Consumer Implementation
module internal DatatypeChannels.ASB.Consumer

open System
open System.Threading.Tasks
open System.Collections.Concurrent
open System.Diagnostics
open Azure.Messaging.ServiceBus

let mkNew (options:ServiceBusReceiverOptions)
          (startRenewal: ServiceBusReceiver -> (unit -> (Activity * ServiceBusReceivedMessage) option) -> Task)
          (OfReceived ofReceived)
          (withClient: (ServiceBusClient -> _) -> _)
          withBinding =
    let ctxRef = ref Option<ServiceBusReceiver * (unit -> Activity) * ConcurrentDictionary<string, ServiceBusReceivedMessage * Activity * Task>>.None
    let activitySource = Diagnostics.mkActivitySource "Consumer"
    let withCtx cont =
        match ctxRef.Value with
        | Some ((receiver,_,_) as ctx) when not receiver.IsClosed -> Task.FromResult ctx
        | _ -> fun (name:Task<_>) -> backgroundTask {
                    let! name = name
                    let startActivity _ = activitySource |> Diagnostics.startActivity name ActivityKind.Consumer
                    let receiver = withClient (fun client -> client.CreateReceiver(name, options))
                    ctxRef.Value <- Some (receiver, startActivity, ConcurrentDictionary())
                    return ctxRef.Value.Value
               }
               |> withBinding
        |> cont

    backgroundTask {
        do! withCtx (Task.map ignore) // force the subscription to occur

        return { new Consumer<'T> with
            member __.Get timeout =
                withCtx <| fun ctx -> backgroundTask {
                    let! (receiver:ServiceBusReceiver, startActivity, messages: ConcurrentDictionary<_,_>) = ctx
                    let activity = startActivity()
                    match! receiver.ReceiveMessageAsync timeout with
                    | null -> return None
                    | received ->
                        activity.AddTag("msg.Id", received.MessageId) |> ignore
                        let! msg =
                            if options.SubQueue = SubQueue.DeadLetter then ofReceived received |> Some |> Task.FromResult
                            else backgroundTask {
                                try return ofReceived received |> Some
                                with ex ->
                                    if receiver.ReceiveMode = ServiceBusReceiveMode.PeekLock then
                                        do! receiver.DeadLetterMessageAsync (received, ex.Message)
                                        activity.Dispose()
                                    return None
                            }
                        return msg |> Option.map (fun msg -> 
                            let find _ = match messages.TryGetValue received.LockToken with | true, (msg,activity,_) -> Some (activity,msg) | _ -> None
                            if receiver.ReceiveMode = ServiceBusReceiveMode.PeekLock 
                                && not (messages.TryAdd(received.LockToken, (received, activity, startRenewal receiver find))) then failwith "Unable to add the message"
                            { Msg = msg; Id = received.LockToken })
                }

            member __.Ack receivedId =
                withCtx (fun ctx -> backgroundTask {
                    let! (receiver:ServiceBusReceiver, _, messages: ConcurrentDictionary<_,_>) = ctx
                    match messages.TryGetValue receivedId with
                    | true, (received, activity, renewal) ->
                        do! receiver.CompleteMessageAsync received
                        use _ = activity
                        messages.TryRemove receivedId |> ignore
                        do! renewal
                    | _ -> failwithf "Message is not in the current session: %s" receivedId
                })

            member __.Nack receivedId =
                withCtx(fun ctx -> backgroundTask {
                    let! (receiver: ServiceBusReceiver, _, messages: ConcurrentDictionary<_,_>) = ctx
                    match messages.TryGetValue receivedId with
                    | true, (received, activity, renewal) ->
                        do! receiver.AbandonMessageAsync received
                        messages.TryRemove receivedId |> ignore
                        use _ = activity
                        do! renewal
                    | _ -> failwithf "Message is not in the current session: %s" receivedId
                })

            member __.Dispose() =
                match ctxRef.Value with
                | Some (receiver, _, messages) ->
                    for KeyValue(_,(_,activity,renewal)) in messages.ToArray() do
                        try
                            use _ = activity
                            renewal.Dispose()
                        with _ -> ()
                    messages.Clear()
                    receiver.DisposeAsync().AsTask().Wait()
                    ctxRef.Value <- None
                | _ -> ()
        }
    }

module LockDuration =
    let fiveMinutes = TimeSpan.FromMinutes 5.
    let private renew (delay: TimeSpan) (r: ServiceBusReceiver) (getMsg: unit -> (Activity * ServiceBusReceivedMessage) option) = 
        Task.Run(fun _ ->
            backgroundTask {
                do! Task.Delay delay
                for (activity,msg) in Seq.initInfinite (fun _ -> getMsg()) |> Seq.takeWhile Option.isSome |> Seq.choose id do
                    activity.AddEvent(ActivityEvent("Renew")) |>ignore
                    do! r.RenewMessageLockAsync msg
                    do! Task.Delay delay
            } :> Task)

    let noRenew _ _ = Task.CompletedTask

    /// Update the options to maximum valid value and return a function to setup renewal Task
    let makeRenewable lockDuration =
        let renewalPeriod = 
            if lockDuration <= fiveMinutes then None
            else Some (TimeSpan.FromSeconds 10.)
        match renewalPeriod with
        | Some ts ->
            renew ts
        | _ -> noRenew