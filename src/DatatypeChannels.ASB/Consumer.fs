/// Consumer Implementation
module internal DatatypeChannels.ASB.Consumer

open System
open System.Threading
open System.Threading.Tasks
open System.Collections.Concurrent
open System.Diagnostics
open Azure.Messaging.ServiceBus

type MessageContext =
    { Message: ServiceBusReceivedMessage
      CancellationTokenSource: CancellationTokenSource
      Activity: Activity
      GetReciever: unit -> Task<ServiceBusReceiver> }
    member x.Close() =
        if not x.CancellationTokenSource.IsCancellationRequested then x.CancellationTokenSource.Cancel()
        x.Activity.Stop()
    member x.Renew() = 
        backgroundTask { 
            let! receiver = x.GetReciever()
            return! receiver.RenewMessageLockAsync(x.Message, x.CancellationTokenSource.Token)
        }

    static member From (msg: ServiceBusReceivedMessage)
                       (activity: Activity)
                       (withCtx: (_ -> Task<_>) -> Task<ServiceBusReceiver>) =
        { Message = msg
          Activity = activity
          CancellationTokenSource = new CancellationTokenSource()
          GetReciever = fun () -> withCtx (Task.map (fun (r,_,_) -> r)) }

type internal Options() =
    inherit ServiceBusReceiverOptions()
    member val IgnoreDuplicates = true with get,set

let mkNew (options: Options)
          (startRenewal: MessageContext -> Task)
          (OfReceived ofReceived)
          (withClient: (ServiceBusClient -> _) -> _)
          withBinding =
    let ctxRef = ref Option<ServiceBusReceiver * (unit -> Activity) * ConcurrentDictionary<string, MessageContext>>.None
    let activitySource = Diagnostics.mkActivitySource "Consumer"
    let withCtx cont =
        let startCtx msgs (name:Task<_>) =
            backgroundTask {
                use activity = activitySource |> Diagnostics.startActivity (Diagnostics.formatName "NewConsumerCtx") ActivityKind.Consumer
                let! name = name
                activity.AddTag("binding", $"{name}/{options.SubQueue}") |> ignore
                let startActivity _ = activitySource |> Diagnostics.startActivity name ActivityKind.Consumer
                let receiver = withClient (fun client -> client.CreateReceiver(name, options))
                ctxRef.Value <- Some (receiver, startActivity, msgs)
                return ctxRef.Value.Value
            }
        match ctxRef.Value with
        | Some (receiver,_,msgs) when receiver.IsClosed -> 
            startCtx msgs |> withBinding
        | Some (_ as ctx) -> Task.FromResult ctx
        | _ -> startCtx (ConcurrentDictionary()) |> withBinding
        |> cont

    backgroundTask {
        do! withCtx (Task.map ignore) // force the subscription to occur

        return { new Consumer<'T> with
            member __.Get timeout =
                withCtx <| fun ctx -> backgroundTask {
                    let! (receiver:ServiceBusReceiver, startActivity, msgCtxs: ConcurrentDictionary<_,_>) = ctx
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
                        return msg |> Option.bind (fun msg -> 
                            let msgCtx = MessageContext.From received activity withCtx
                            match receiver.ReceiveMode with
                            | ServiceBusReceiveMode.ReceiveAndDelete ->
                                Some { Msg = msg; Id = received.MessageId }
                            | ServiceBusReceiveMode.PeekLock when msgCtxs.TryAdd(received.MessageId, msgCtx) ->
                                startRenewal msgCtx |> ignore
                                Some { Msg = msg; Id = received.MessageId }
                            | ServiceBusReceiveMode.PeekLock when not options.IgnoreDuplicates ->
                                failwith $"Unable to add the message, duplicate id: {received.MessageId}"
                            | _ -> None 
                        )
                }

            member __.Ack receivedId =
                withCtx (fun ctx -> backgroundTask {
                    let! (receiver:ServiceBusReceiver, _, msgCtxs: ConcurrentDictionary<_,_>) = ctx
                    match msgCtxs.TryGetValue receivedId with
                    | true, msgCtx ->
                        do! receiver.CompleteMessageAsync msgCtx.Message
                        msgCtxs.TryRemove receivedId |> ignore
                        msgCtx.Close()
                    | _ -> failwithf "Message is not in the current session: %s" receivedId
                })

            member __.Nack receivedId =
                withCtx(fun ctx -> backgroundTask {
                    let! (receiver: ServiceBusReceiver, _, msgCtxs: ConcurrentDictionary<_,_>) = ctx
                    match msgCtxs.TryGetValue receivedId with
                    | true, msgCtx ->
                        do! receiver.AbandonMessageAsync msgCtx.Message
                        msgCtxs.TryRemove receivedId |> ignore
                        msgCtx.Close()
                    | _ -> failwithf "Message is not in the current session: %s" receivedId
                })

            member __.Dispose() =
                match ctxRef.Value with
                | Some (receiver, _, msgCtxs) ->
                    for KeyValue(_,msgCtx) in msgCtxs.ToArray() do
                        msgCtx.Close()
                    receiver.CloseAsync().Wait()
                    ctxRef.Value <- None
                | _ -> ()
        }
    }

module Renewable =
    let maxLockDuration = TimeSpan.FromMinutes 5.
    let private op (delay: TimeSpan) (msgCtx: MessageContext) = 
        Task.Run(Func<Task>(fun _ ->
            backgroundTask {
                while not msgCtx.CancellationTokenSource.IsCancellationRequested do
                    try 
                        do! Task.Delay(delay, msgCtx.CancellationTokenSource.Token)
                        msgCtx.Activity.AddEvent(ActivityEvent("Renewing")) |>ignore
                        do! msgCtx.Renew()
                    with 
                        :? TaskCanceledException as e -> ()
                        | e -> 
                            msgCtx.Activity.AddEvent(ActivityEvent("Error renewing", tags = Diagnostics.ActivityTagsCollection.ofException e)) |> ignore
            }), msgCtx.CancellationTokenSource.Token)

    let noop _ = Task.CompletedTask

    /// Update the options to maximum valid value and return a function to setup renewal Task
    let mkNew lockDuration =
        let renewalPeriod = 
            if lockDuration <= maxLockDuration then None
            else Some (TimeSpan.FromSeconds 30.)
        match renewalPeriod with
        | Some ts -> op ts
        | _ -> noop