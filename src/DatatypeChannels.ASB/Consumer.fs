/// Consumer Implementation
module internal DatatypeChannels.ASB.Consumer

open System
open System.Threading
open System.Threading.Tasks
open System.Collections.Concurrent
open System.Diagnostics
open Azure.Messaging.ServiceBus

type internal MessageContext =
    { Message: ServiceBusReceivedMessage
      CancellationTokenSource: CancellationTokenSource
      Activity: Activity
      Reciever: ServiceBusReceiver }
    member x.Close() =
        if not x.CancellationTokenSource.IsCancellationRequested then x.CancellationTokenSource.Cancel()
        x.Activity.Stop()

    static member From (msg: ServiceBusReceivedMessage)
                       (activity: Activity)
                       (reciever: ServiceBusReceiver) =
        { Message = msg
          Activity = activity
          CancellationTokenSource = new CancellationTokenSource()
          Reciever =  reciever }


let mkNew (options:ServiceBusReceiverOptions)
          (startRenewal: MessageContext -> Task)
          (OfReceived ofReceived)
          (withClient: (ServiceBusClient -> _) -> _)
          withBinding =
    let ctxRef = ref Option<ServiceBusReceiver * (unit -> Activity) * ConcurrentDictionary<string, MessageContext>>.None
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
                        return msg |> Option.map (fun msg -> 
                            let msgCtx = MessageContext.From received activity receiver
                            if receiver.ReceiveMode = ServiceBusReceiveMode.PeekLock then
                                if msgCtxs.TryAdd(received.LockToken, msgCtx) then startRenewal msgCtx |> ignore
                                else failwith "Unable to add the message"
                            { Msg = msg; Id = received.LockToken })
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
                    msgCtxs.Clear()
                    receiver.DisposeAsync().AsTask().Wait()
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
                        do! msgCtx.Reciever.RenewMessageLockAsync(msgCtx.Message, msgCtx.CancellationTokenSource.Token)
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