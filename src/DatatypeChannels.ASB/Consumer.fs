/// Consumer implementation.
module internal DatatypeChannels.ASB.Consumer

open System
open System.Collections.Generic
open System.Diagnostics
open System.Runtime.ExceptionServices
open System.Threading
open System.Threading.Tasks

open Azure.Messaging.ServiceBus

type MessageContext =
    { Message: ServiceBusReceivedMessage
      Receiver: ServiceBusReceiver
      CancellationTokenSource: CancellationTokenSource
      Activity: Activity
      Log: Log }

    member this.Identity =
        struct (this.Receiver.FullyQualifiedNamespace, this.Receiver.EntityPath,
                this.Message.MessageId, this.Message.SequenceNumber)

    member this.Close() = this.CancellationTokenSource.Cancel()

    member this.Renew() =
        this.Receiver.RenewMessageLockAsync(this.Message, this.CancellationTokenSource.Token)

    member this.Report(name, error: exn option) =
        let (Log log) = this.Log
        let detail = error |> Option.map _.Message |> Option.defaultValue ""
        let tags =
            error |> Option.map Diagnostics.ActivityTagsCollection.ofException
            |> Option.defaultWith (fun () -> ActivityTagsCollection())
        this.Activity.AddEvent(ActivityEvent(name, tags = tags)) |> ignore
        let format =
            "Consumer {event}: entity={entity}, message={message}, sequence={sequence}, "
            + "delivery={delivery}, lockedUntil={lockedUntil}, detail={detail}"
        log(format,
            [| name; this.Receiver.EntityPath; this.Message.MessageId;
               this.Message.SequenceNumber; this.Message.DeliveryCount;
               this.Message.LockedUntil; detail |])

type internal Options() =
    inherit ServiceBusReceiverOptions()
    member val IgnoreDuplicates = true with get, set
    member val PassiveNack = false with get, set
    member val Clock = TimeProvider.System with get, set
    member val Log =
        Log(fun (message, values) -> Trace.TraceWarning(message + " " + String.Join(", ", values)))
        with get, set

type private Delivery =
    { Context: MessageContext
      Renewal: Task }

[<RequireQualifiedAccess>]
module private Cleanup =
    let capture (operation: unit -> Task) =
        backgroundTask {
            try
                do! operation ()
                return None
            with error -> return Some (ExceptionDispatchInfo.Capture error)
        }

    // Both operations must run, and neither error may hide the other.
    let finish operation cleanup =
        backgroundTask {
            let! failure = capture operation
            let! cleanupFailure = capture cleanup
            match failure, cleanupFailure with
            | Some first, Some second ->
                return raise (AggregateException(first.SourceException, second.SourceException))
            | Some error, None | None, Some error -> error.Throw()
            | None, None -> ()
        }

    let stop delivery =
        backgroundTask {
            try
                delivery.Context.Close()
                do! delivery.Renewal
            finally
                delivery.Context.Activity.Dispose()
                delivery.Context.CancellationTokenSource.Dispose()
        }

    let report (context: MessageContext) name error =
        finish
            (fun () -> Task.FromException error)
            (fun () -> context.Report(name, Some error); Task.CompletedTask)

type private CompletedMessages(clock: TimeProvider, subQueue: SubQueue) =
    let entries = HashSet<struct (string * string * string * int64)>()
    let expirations = PriorityQueue<struct (string * string * string * int64), DateTimeOffset>()

    member private this.Expire() =
        match expirations.TryPeek() with
        | true, identity, expiry when expiry <= clock.GetUtcNow() ->
            expirations.Dequeue() |> ignore
            entries.Remove identity |> ignore
            this.Expire()
        | _ -> ()

    member this.Contains identity =
        this.Expire()
        entries.Contains identity

    member this.Add(context: MessageContext) =
        this.Expire()
        // Retain through broker TTL, with a lock-duration grace for overlapping receives.
        let expiry =
            if subQueue = SubQueue.DeadLetter then DateTimeOffset.MaxValue
            else max context.Message.ExpiresAt (clock.GetUtcNow().AddMinutes 5.)
        entries.Add context.Identity |> ignore
        expirations.Enqueue(context.Identity, expiry)

    member _.Clear() =
        entries.Clear()
        expirations.Clear()

type private Session<'T>(
    options: Options, startRenewal: MessageContext -> Task, deserialize: OfReceived<'T>,
    createReceiver: string -> ServiceBusReceiver, binding: unit -> Task<string>) =

    let (OfReceived ofReceived) = deserialize
    let gate = new SemaphoreSlim(1, 1)
    let lifetime = new CancellationTokenSource()
    let lifetimeToken = lifetime.Token
    let completion = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
    let source = Diagnostics.mkActivitySource "Consumer"
    let active = Dictionary<string, Delivery>()
    let ended = CompletedMessages(options.Clock, options.SubQueue)
    let receivers = ResizeArray<ServiceBusReceiver>()
    let mutable currentReceiver: ServiceBusReceiver option = None
    let mutable disposed = false

    member private _.CheckDisposed() =
        if disposed then
            raise (OperationCanceledException("Consumer disposed", lifetimeToken))

    member private this.Exclusive(operation) =
        backgroundTask {
            do! gate.WaitAsync()
            try
                this.CheckDisposed()
                return! operation ()
            finally gate.Release() |> ignore
        }

    member private _.Receiver() =
        backgroundTask {
            match currentReceiver with
            | Some receiver when not receiver.IsClosed -> return receiver
            | _ ->
                let! name = binding ()
                let receiver = createReceiver name
                receivers.Add receiver
                currentReceiver <- Some receiver
                return receiver
        }

    member private _.Context(receiver: ServiceBusReceiver, message: ServiceBusReceivedMessage) =
        let activity = source |> Diagnostics.startActivity receiver.EntityPath ActivityKind.Consumer
        activity.AddTag("msg.Id", message.MessageId) |> ignore
        activity.AddTag("msg.SequenceNumber", message.SequenceNumber) |> ignore
        { Message = message
          Receiver = receiver
          CancellationTokenSource = new CancellationTokenSource()
          Activity = activity
          Log = options.Log }

    member private _.Renewal(context) =
        backgroundTask {
            try do! startRenewal context
            with
            | :? OperationCanceledException as error
                when context.CancellationTokenSource.IsCancellationRequested
                     && error.CancellationToken = context.CancellationTokenSource.Token -> ()
            | error ->
                completion.TrySetException error |> ignore
                return raise error
        }

    member private this.Track(context: MessageContext) =
        let delivery = { Context = context; Renewal = this.Renewal context }
        active[context.Message.MessageId] <- delivery

    member private this.Replace(context: MessageContext, previous: Delivery) =
        backgroundTask {
            if not options.IgnoreDuplicates || previous.Context.Identity <> context.Identity then
                invalidOp $"Duplicate ID is not a redelivery: {context.Message.MessageId}"
            if context.Message.DeliveryCount < previous.Context.Message.DeliveryCount then
                context.Report("Older delivery ignored", None)
            else
                let stopping = Cleanup.stop previous
                this.Track context
                do!
                    Cleanup.finish
                        (fun () -> context.Report("Delivery replaced", None); Task.CompletedTask)
                        (fun () -> stopping)
            return None
        }

    member private _.Deserialize(context: MessageContext) =
        backgroundTask {
            try return Some (ofReceived context.Message)
            with error when options.SubQueue <> SubQueue.DeadLetter ->
                context.Report("Deserialization failed", Some error)
                if context.Receiver.ReceiveMode = ServiceBusReceiveMode.PeekLock then
                    do! context.Receiver.DeadLetterMessageAsync(context.Message, error.Message)
                return None
        }

    member private this.Admit(context: MessageContext) =
        backgroundTask {
            let! value = this.Deserialize context
            match value with
            | Some message ->
                if context.Receiver.ReceiveMode = ServiceBusReceiveMode.PeekLock then
                    this.Track context
                return Some { Msg = message; Id = context.Message.MessageId }
            | None -> return None
        }

    member private this.Register(context: MessageContext) =
        backgroundTask {
            if context.Receiver.ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete then
                return! this.Admit context
            elif ended.Contains context.Identity then
                do! this.SettleDelivery(true, context)
                return None
            else
                match active.TryGetValue context.Message.MessageId with
                | true, previous -> return! this.Replace(context, previous)
                | false, _ -> return! this.Admit context
        }

    member private this.Accept(receiver, received) =
        backgroundTask {
            if completion.Task.IsFaulted then do! completion.Task
            if isNull received then return None
            else
                let context = this.Context(receiver, received)
                try return! this.Register context
                finally
                    match active.TryGetValue received.MessageId with
                    | true, delivery when obj.ReferenceEquals(delivery.Context, context) -> ()
                    | _ ->
                        context.Close()
                        context.Activity.Dispose()
                        context.CancellationTokenSource.Dispose()
        }

    member private this.Receive(timeout: TimeSpan) =
        backgroundTask {
            let! receiver = this.Exclusive this.Receiver
            try
                let! received = receiver.ReceiveMessageAsync(timeout, lifetimeToken)
                return! this.Exclusive(fun () -> this.Accept(receiver, received))
            with :? ObjectDisposedException as error when lifetimeToken.IsCancellationRequested ->
                return raise (OperationCanceledException("Consumer disposed", error, lifetimeToken))
        }

    member private _.SettleDelivery(isAck, context: MessageContext) =
        backgroundTask {
            try
                if isAck then do! context.Receiver.CompleteMessageAsync context.Message
                elif not options.PassiveNack then
                    do! context.Receiver.AbandonMessageAsync context.Message
            with error ->
                do! Cleanup.report context (if isAck then "ACK failed" else "NACK failed") error
        }

    member private this.Settle(isAck, id) =
        this.Exclusive(fun () ->
            backgroundTask {
                match active.TryGetValue id with
                | false, _ -> invalidOp $"Message is not in the current session: {id}"
                | true, delivery ->
                    // ACK ends application ownership even when its transport settlement fails.
                    if isAck then ended.Add delivery.Context
                    active.Remove id |> ignore
                    do!
                        Cleanup.finish
                            (fun () -> this.SettleDelivery(isAck, delivery.Context))
                            (fun () -> Cleanup.stop delivery)
            })

    member this.Start() = this.Exclusive(fun () -> this.Receiver() |> Task.map ignore)

    member private _.Shutdown() =
        let pending = active.Values |> Seq.map Cleanup.stop |> Seq.toArray
        active.Clear()
        ended.Clear()
        Cleanup.finish
            (fun () -> Task.WhenAll pending)
            (fun () -> receivers |> Seq.map _.CloseAsync() |> Task.WhenAll)

    member private this.Dispose() =
        gate.Wait()
        try
            if not disposed then
                disposed <- true
                lifetime.Cancel()
                try this.Shutdown().GetAwaiter().GetResult()
                finally
                    completion.TrySetResult() |> ignore
                    source.Dispose()
                    lifetime.Dispose()
        finally
            // In-flight receives still need this gate to observe the disposal fence.
            gate.Release() |> ignore

    interface Consumer<'T> with
        member this.Get timeout = this.Receive timeout
        member this.Ack id = this.Settle(true, id)
        member this.Nack id = this.Settle(false, id)
        member this.Dispose() = this.Dispose()

    interface ConsumerSupervision with
        member _.Completion = completion.Task

let mkNew (options: Options) startRenewal ofReceived withClient withBinding =
    backgroundTask {
        let createReceiver name =
            withClient (fun (client: ServiceBusClient) -> client.CreateReceiver(name, options))
        let session = new Session<_>(options, startRenewal, ofReceived, createReceiver,
                                     fun () -> withBinding id)
        do! session.Start()
        return session :> Consumer<_>
    }

[<RequireQualifiedAccess>]
module Renewable =
    let maxLockDuration = TimeSpan.FromMinutes 5.

    let private renew (context: MessageContext) =
        backgroundTask {
            try
                let! _ = context.Renew()
                ()
            with
            | :? ServiceBusException as error
                when error.Reason = ServiceBusFailureReason.MessageLockLost ->
                context.Report("Delivery ownership lost", Some error)
                context.Close()
            | :? ServiceBusException as error when error.IsTransient ->
                context.Report("Transient renewal failure", Some error)
        }

    let start (delay: CancellationToken -> Task) (context: MessageContext) : Task =
        backgroundTask {
            let token = context.CancellationTokenSource.Token
            try
                while not token.IsCancellationRequested do
                    do! delay token
                    token.ThrowIfCancellationRequested()
                    do! renew context
            with
            | :? OperationCanceledException as error
                when token.IsCancellationRequested && error.CancellationToken = token -> ()
            | error ->
                do! Cleanup.report context "Renewal failed" error
        }

    let noop _ = Task.CompletedTask

    /// Renew until settlement/disposal; application deadlines remain caller-owned.
    let mkNew lockDuration =
        if lockDuration <= maxLockDuration then noop
        else start (fun token -> Task.Delay(TimeSpan.FromSeconds 30., token))
