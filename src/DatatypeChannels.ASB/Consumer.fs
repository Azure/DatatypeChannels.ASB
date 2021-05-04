/// Consumer Implementation
module internal DatatypeChannels.ASB.Consumer

open System
open System.Threading.Tasks
open System.Collections.Concurrent
open Azure.Messaging.ServiceBus
open FSharp.Control.Tasks.Builders

let mkNew (options:ServiceBusReceiverOptions)
          (maxRetries: uint16)
          (startRenewal: ServiceBusReceiver -> (unit -> ServiceBusReceivedMessage option) -> Task)
          (OfReceived ofReceived)
          (withClient: (ServiceBusClient -> _) -> _)
          withBinding =
    let ctxRef = ref Option<ServiceBusReceiver * ConcurrentDictionary<string, ServiceBusReceivedMessage * Task>>.None
    let withCtx cont =
        match !ctxRef with
        | Some ((receiver,_) as ctx) when not receiver.IsClosed -> Task.FromResult ctx
        | _ -> fun (name:Task<_>) -> task {
                    let! name = name
                    let receiver = withClient (fun client -> client.CreateReceiver(name, options))
                    ctxRef := Some (receiver, ConcurrentDictionary())
                    return (!ctxRef).Value
               }
               |> withBinding
        |> cont

    (withCtx (Task.map ignore)).Wait() // force the subscription to occur

    { new Consumer<'T> with
        member __.Get timeout =
            fun ctx -> task {
                let! (receiver:ServiceBusReceiver, messages: ConcurrentDictionary<_,_>) = ctx
                match! Nullable timeout |> Task.withRetries maxRetries (fun t -> receiver.ReceiveMessageAsync t) with
                | null -> return None
                | received ->
                    let! msg =
                        if options.SubQueue = SubQueue.DeadLetter then ofReceived received |> Some |> Task.FromResult
                        else task {
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
            |> withCtx

        member __.Ack receivedId =
            fun ctx -> task {
                let! (receiver:ServiceBusReceiver, messages: ConcurrentDictionary<_,_>) = ctx
                match messages.TryGetValue receivedId with
                | true, received ->
                    do! fst received |> Task.withRetries maxRetries (receiver.CompleteMessageAsync >> Task.ignore)
                    messages.TryRemove receivedId |> ignore
                    do! snd received
                | _ -> failwithf "Message is not in the current session: %s" receivedId
            }
            |> withCtx

        member __.Nack receivedId =
            fun ctx -> task {
                let! (receiver: ServiceBusReceiver, messages: ConcurrentDictionary<_,_>) = ctx
                match messages.TryGetValue receivedId with
                | true, received ->
                    do! fst received |> Task.withRetries maxRetries (receiver.AbandonMessageAsync >> Task.ignore)
                    messages.TryRemove receivedId |> ignore
                    do! snd received
                | _ -> failwithf "Message is not in the current session: %s" receivedId
            }
            |> withCtx

        member __.Dispose() =
            match !ctxRef with
            | Some (receiver,messages) ->
               messages.Clear()
               receiver.DisposeAsync().AsTask().Wait()
               ctxRef := None
            | _ -> ()
    }

module LockDuration =
    let fiveMinutes = TimeSpan.FromMinutes 5.
    let private renew (delay: TimeSpan) (retries: uint16) (r: ServiceBusReceiver) (getMsg: unit -> ServiceBusReceivedMessage option) = 
        Task.Run(fun _ ->
            task {
                do! Task.Delay delay
                for msg in Seq.initInfinite (fun _ -> getMsg()) |> Seq.takeWhile Option.isSome |> Seq.choose id do
                    do! msg |> Task.withRetries retries (fun m -> r.RenewMessageLockAsync m |> Task.ignore)
                    do! Task.Delay delay
            } :> Task)

    let noRenew _ _ = Task.CompletedTask

    /// Update the options to maximum valid value and return a function to setup renewal Task
    let makeRenewable (retries: uint16) lockDuration =
        let renewalPeriod = 
            if lockDuration <= fiveMinutes then None
            else Some (TimeSpan.FromSeconds 10.)
        match renewalPeriod with
        | Some ts ->
            renew ts retries
        | _ -> noRenew