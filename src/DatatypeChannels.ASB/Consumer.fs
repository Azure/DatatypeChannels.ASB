/// Consumer Implementation
module internal DatatypeChannels.ASB.Consumer

open System
open System.Threading.Tasks
open System.Collections.Concurrent
open Azure.Messaging.ServiceBus

let mkNew (options:ServiceBusReceiverOptions)
          (startRenewal: ServiceBusReceiver -> (unit -> ServiceBusReceivedMessage option) -> Task)
          (OfReceived ofReceived)
          (withClient: (ServiceBusClient -> _) -> _)
          withBinding =
    let ctxRef = ref Option<ServiceBusReceiver * ConcurrentDictionary<string, ServiceBusReceivedMessage * Task>>.None
    let withCtx cont =
        match ctxRef.Value with
        | Some ((receiver,_) as ctx) when not receiver.IsClosed -> Task.FromResult ctx
        | _ -> fun (name:Task<_>) -> backgroundTask {
                    let! name = name
                    let receiver = withClient (fun client -> client.CreateReceiver(name, options))
                    ctxRef.Value <- Some (receiver, ConcurrentDictionary())
                    return ctxRef.Value.Value
               }
               |> withBinding
        |> cont

    backgroundTask {
        do! withCtx (Task.map ignore) // force the subscription to occur

        return { new Consumer<'T> with
            member __.Get timeout =
                withCtx <| fun ctx -> backgroundTask {
                    let! (receiver:ServiceBusReceiver, messages: ConcurrentDictionary<_,_>) = ctx
                    match! receiver.ReceiveMessageAsync timeout with
                    | null -> return None
                    | received ->
                        let! msg =
                            if options.SubQueue = SubQueue.DeadLetter then ofReceived received |> Some |> Task.FromResult
                            else backgroundTask {
                                try return ofReceived received |> Some
                                with ex ->
                                    if receiver.ReceiveMode = ServiceBusReceiveMode.PeekLock then
                                        do! receiver.DeadLetterMessageAsync (received, ex.Message)
                                    return None
                            }
                        return msg |> Option.map (fun msg -> 
                            let find _ = match messages.TryGetValue received.LockToken with | true, m -> Some (fst m) | _ -> None
                            if receiver.ReceiveMode = ServiceBusReceiveMode.PeekLock 
                                && not (messages.TryAdd(received.LockToken, (received, startRenewal receiver find))) then failwith "Unable to add the message"
                            { Msg = msg; Id = received.LockToken })
                }

            member __.Ack receivedId =
                withCtx (fun ctx -> backgroundTask {
                    let! (receiver:ServiceBusReceiver, messages: ConcurrentDictionary<_,_>) = ctx
                    match messages.TryGetValue receivedId with
                    | true, received ->
                        do! receiver.CompleteMessageAsync (fst received)
                        messages.TryRemove receivedId |> ignore
                        do! snd received
                    | _ -> failwithf "Message is not in the current session: %s" receivedId
                })

            member __.Nack receivedId =
                withCtx(fun ctx -> backgroundTask {
                    let! (receiver: ServiceBusReceiver, messages: ConcurrentDictionary<_,_>) = ctx
                    match messages.TryGetValue receivedId with
                    | true, received ->
                        do! receiver.AbandonMessageAsync (fst received)
                        messages.TryRemove receivedId |> ignore
                        do! snd received
                    | _ -> failwithf "Message is not in the current session: %s" receivedId
                })

            member __.Dispose() =
                match ctxRef.Value with
                | Some (receiver,messages) ->
                    messages.Clear()
                    receiver.DisposeAsync().AsTask().Wait()
                    ctxRef.Value <- None
                | _ -> ()
        }
    }

module LockDuration =
    let fiveMinutes = TimeSpan.FromMinutes 5.
    let private renew (delay: TimeSpan) (r: ServiceBusReceiver) (getMsg: unit -> ServiceBusReceivedMessage option) = 
        Task.Run(fun _ ->
            backgroundTask {
                do! Task.Delay delay
                for msg in Seq.initInfinite (fun _ -> getMsg()) |> Seq.takeWhile Option.isSome |> Seq.choose id do
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