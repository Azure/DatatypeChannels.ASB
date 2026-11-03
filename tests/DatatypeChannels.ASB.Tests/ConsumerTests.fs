module DatatypeChannels.ASB.ConsumerTests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks

open Azure.Messaging.ServiceBus
open Expecto
open Swensen.Unquote

let private timeout = TimeSpan.FromSeconds 10.

let private signal<'T> () =
    TaskCompletionSource<'T>(TaskCreationOptions.RunContinuationsAsynchronously)

let private messageWithCount deliveryCount sequence =
    ServiceBusModelFactory.ServiceBusReceivedMessage(
        messageId = "work-item",
        sequenceNumber = sequence,
        deliveryCount = deliveryCount,
        lockTokenGuid = Guid.NewGuid(),
        enqueuedTime = DateTimeOffset.UtcNow,
        timeToLive = TimeSpan.FromHours 1.,
        lockedUntil = DateTimeOffset.UtcNow.AddMinutes 5.)

let private message sequence = messageWithCount 1 sequence

type private Receiver(?mode, ?entity) =
    inherit ServiceBusReceiver()
    let messages = ConcurrentQueue<ServiceBusReceivedMessage>()
    let completed = ConcurrentQueue<ServiceBusReceivedMessage>()
    let abandoned = ConcurrentQueue<ServiceBusReceivedMessage>()
    let renewed = ConcurrentQueue<ServiceBusReceivedMessage>()
    let mutable closed = false
    member val ReceiveOverride: (unit -> Task<ServiceBusReceivedMessage>) option = None with get, set
    member val Complete: ServiceBusReceivedMessage -> Task = fun _ -> Task.CompletedTask with get, set
    member val Abandon: ServiceBusReceivedMessage -> Task = fun _ -> Task.CompletedTask with get, set
    member val Renew: ServiceBusReceivedMessage -> Task<DateTimeOffset> =
        fun _ -> Task.FromResult(DateTimeOffset.UtcNow.AddMinutes 5.) with get, set
    member val DeadLetters = 0 with get, set
    member val CloseCalls = 0 with get, set
    member this.Enqueue(sequence, ?deliveryCount) =
        let received = messageWithCount (defaultArg deliveryCount 1) sequence
        messages.Enqueue received
        received
    member _.Completed = completed.ToArray()
    member _.Abandoned = abandoned.ToArray()
    member _.Renewed = renewed.ToArray()
    member _.MarkClosed() = closed <- true
    override _.EntityPath = defaultArg entity "test-queue"
    override _.FullyQualifiedNamespace = "test.servicebus.windows.net"
    override _.ReceiveMode = defaultArg mode ServiceBusReceiveMode.PeekLock
    override _.IsClosed = closed
    override this.ReceiveMessageAsync(_, _) =
        match this.ReceiveOverride with
        | Some receive -> receive ()
        | None ->
            match messages.TryDequeue() with
            | true, received -> Task.FromResult received
            | false, _ -> Task.FromResult<ServiceBusReceivedMessage> null
    override this.CompleteMessageAsync(received, _) =
        completed.Enqueue received
        this.Complete received
    override this.AbandonMessageAsync(received, _, _) =
        abandoned.Enqueue received
        this.Abandon received
    override this.RenewMessageLockAsync(received, token) =
        if token.IsCancellationRequested then Task.FromCanceled<DateTimeOffset> token
        else
            renewed.Enqueue received
            this.Renew received
    override this.DeadLetterMessageAsync(_, _: string, _: string, _: CancellationToken) =
        this.DeadLetters <- this.DeadLetters + 1
        Task.CompletedTask
    override this.CloseAsync(_) =
        closed <- true
        this.CloseCalls <- this.CloseCalls + 1
        Task.CompletedTask

type private Client(next: unit -> ServiceBusReceiver) =
    inherit ServiceBusClient()
    override _.CreateReceiver(_: string, _: ServiceBusReceiverOptions) = next ()
    override _.DisposeAsync() = ValueTask.CompletedTask

type private Renewals() =
    let contexts = ConcurrentQueue<Consumer.MessageContext * CancellationToken>()
    member _.Contexts = contexts.ToArray()
    member _.Start(context: Consumer.MessageContext) : Task =
        let token = context.CancellationTokenSource.Token
        contexts.Enqueue(context, token)
        Task.Delay(Timeout.InfiniteTimeSpan, token)

type private Clock() =
    inherit TimeProvider()
    let mutable now = DateTimeOffset.UtcNow
    override _.GetUtcNow() = now
    member _.Advance period = now <- now + period

type private RenewalSteps() =
    let waits = Channel.CreateUnbounded<TaskCompletionSource<unit>>()
    member _.Delay(token: CancellationToken) : Task =
        let next = signal<unit> ()
        waits.Writer.TryWrite next |> ignore
        next.Task.WaitAsync token
    member _.Next() = waits.Reader.ReadAsync().AsTask().WaitAsync timeout

let private create options renew decode next =
    let client = new Client(next)
    Consumer.mkNew options renew (OfReceived decode)
        (fun useClient -> useClient client)
        (fun binding -> binding (Task.FromResult "test-queue"))

let private consumer options renew receiver =
    create options renew (fun received -> received.MessageId) (fun () -> receiver)

let private get (consumer: Consumer<'T>) = consumer.Get(TimeSpan.FromMilliseconds 1.)

let private run name work =
    testCase name (fun () -> (work () : Task).WaitAsync(timeout).GetAwaiter().GetResult())

let private fails<'T when 'T :> exn> (work: Task) =
    task {
        let mutable error = None
        try do! work
        with :? 'T as caught -> error <- Some caught
        return error |> Option.defaultWith (fun () -> failtest $"Expected {typeof<'T>.Name}")
    }

[<Tests>]
let tests =
    testList "consumer" [
        run "redelivery replaces renewal and settlement without re-emitting work" <| fun () -> task {
            let receiver = new Receiver()
            let renewals = Renewals()
            use! instance = consumer (Consumer.Options()) renewals.Start receiver
            let first = receiver.Enqueue 1L
            let! original = get instance
            let replacement = receiver.Enqueue 1L
            let! duplicate = get instance
            original.IsSome =! true
            duplicate =! None
            renewals.Contexts.Length =! 2
            (snd renewals.Contexts[0]).IsCancellationRequested =! true
            (snd renewals.Contexts[1]).IsCancellationRequested =! false
            do! instance.Ack first.MessageId
            receiver.Completed[0].LockToken =! replacement.LockToken
            (snd renewals.Contexts[1]).IsCancellationRequested =! true
        }

        for isAck in [ true; false ] do
            for lockLost in [ true; false ] do
                run $"settlement cleanup ack={isAck} lockLost={lockLost}" <| fun () -> task {
                    let receiver = new Receiver()
                    let renewals = Renewals()
                    let error: exn =
                        if lockLost then
                            ServiceBusException("expired", ServiceBusFailureReason.MessageLockLost)
                        else InvalidOperationException("unexpected broker failure")
                    let fail _ = Task.FromException error
                    receiver.Complete <- fail
                    receiver.Abandon <- fail
                    use! instance = consumer (Consumer.Options()) renewals.Start receiver
                    receiver.Enqueue 1L |> ignore
                    let! _ = get instance
                    let! caught = fails<exn> (if isAck then instance.Ack "work-item" else instance.Nack "work-item")
                    obj.ReferenceEquals(caught, error) =! true
                    (snd renewals.Contexts[0]).IsCancellationRequested =! true
                    let! _ = fails<exn> (instance.Ack "work-item")
                    receiver.Completed.Length + receiver.Abandoned.Length =! 1
                }

        run "late redelivery after failed ACK is suppressed" <| fun () -> task {
            let receiver = new Receiver()
            use! instance = consumer (Consumer.Options()) Consumer.Renewable.noop receiver
            receiver.Enqueue 1L |> ignore
            let! _ = get instance
            receiver.Complete <- fun _ ->
                Task.FromException(ServiceBusException("expired", ServiceBusFailureReason.MessageLockLost))
            let! _ = fails<ServiceBusException> (instance.Ack "work-item")
            receiver.Complete <- fun _ -> Task.CompletedTask
            receiver.Enqueue 1L |> ignore
            let! received = get instance
            received =! None
            receiver.Completed.Length =! 2
        }

        run "completed identities do not suppress a different broker message with the same ID" <| fun () -> task {
            let receiver = new Receiver()
            use! instance = consumer (Consumer.Options()) Consumer.Renewable.noop receiver
            receiver.Enqueue 1L |> ignore
            let! _ = get instance
            do! instance.Ack "work-item"
            receiver.Enqueue 2L |> ignore
            let! distinct = get instance
            distinct.IsSome =! true
            do! instance.Ack "work-item"
            receiver.Enqueue 1L |> ignore
            let! firstAgain = get instance
            firstAgain =! None
            receiver.Enqueue 2L |> ignore
            let! secondAgain = get instance
            secondAgain =! None
        }

        run "active MessageId collision fails without acknowledging unrelated work" <| fun () -> task {
            let receiver = new Receiver()
            use! instance = consumer (Consumer.Options()) Consumer.Renewable.noop receiver
            let first = receiver.Enqueue 1L
            let! _ = get instance
            receiver.Enqueue 2L |> ignore
            let! _ = fails<InvalidOperationException> (get instance)
            receiver.Completed.Length =! 0
            do! instance.Ack "work-item"
            receiver.Completed[0].LockToken =! first.LockToken
        }

        run "NACK permits another application attempt" <| fun () -> task {
            let receiver = new Receiver()
            let renewals = Renewals()
            use! instance = consumer (Consumer.Options(PassiveNack = true)) renewals.Start receiver
            receiver.Enqueue 1L |> ignore
            let! _ = get instance
            do! instance.Nack "work-item"
            receiver.Abandoned.Length =! 0
            (snd renewals.Contexts[0]).IsCancellationRequested =! true
            receiver.Enqueue 1L |> ignore
            let! retry = get instance
            retry.IsSome =! true
        }

        run "settlement and renewal use the delivery's originating receiver" <| fun () -> task {
            let first = new Receiver()
            let second = new Receiver()
            let receivers = ConcurrentQueue<ServiceBusReceiver>([ first; second ])
            let renewals = Renewals()
            use! instance =
                create (Consumer.Options()) renewals.Start (fun m -> m.MessageId)
                    (fun () -> let ok, receiver = receivers.TryDequeue() in if ok then receiver else failtest "extra receiver")
            first.Enqueue 1L |> ignore
            let! _ = get instance
            first.MarkClosed()
            let! _ = get instance
            do! instance.Ack "work-item"
            first.Completed.Length =! 1
            second.Completed.Length =! 0
        }

        run "dispose fences an in-flight receive" <| fun () -> task {
            let receiver = new Receiver()
            let entered = signal<unit> ()
            let received = signal<ServiceBusReceivedMessage> ()
            receiver.ReceiveOverride <- Some (fun () -> entered.SetResult(); received.Task)
            let renewals = Renewals()
            use! instance = consumer (Consumer.Options()) renewals.Start receiver
            let pending = get instance
            do! entered.Task.WaitAsync timeout
            instance.Dispose()
            received.SetResult(message 1L)
            let! _ = fails<OperationCanceledException> pending
            renewals.Contexts.Length =! 0
            receiver.CloseCalls =! 1
            let! _ = fails<OperationCanceledException> (get instance)
            ()
        }

        run "dispose fences an already-completed receive before admission" <| fun () -> task {
            let receiver = new Receiver()
            use captured = new ManualResetEventSlim()
            use release = new ManualResetEventSlim()
            receiver.ReceiveOverride <- Some (fun () ->
                let ready = Task.FromResult(message 1L)
                captured.Set()
                if not (release.Wait timeout) then failtest "receive barrier timed out"
                ready)
            let renewals = Renewals()
            use! instance = consumer (Consumer.Options()) renewals.Start receiver
            let pending = Task.Run<Envelope<string> option>(fun () -> get instance)
            try
                captured.Wait timeout =! true
                instance.Dispose()
            finally release.Set()
            let! _ = fails<OperationCanceledException> pending
            renewals.Contexts.Length =! 0
        }

        run "ACK serializes replacement admission" <| fun () -> task {
            let receiver = new Receiver()
            let entered = signal<unit> ()
            let release = signal<unit> ()
            receiver.Complete <- fun _ -> entered.TrySetResult() |> ignore; release.Task
            let renewals = Renewals()
            use! instance = consumer (Consumer.Options()) renewals.Start receiver
            receiver.Enqueue 1L |> ignore
            let! _ = get instance
            let ack = instance.Ack "work-item"
            do! entered.Task.WaitAsync timeout
            receiver.Enqueue 1L |> ignore
            let pending = get instance
            release.SetResult()
            do! ack
            let! duplicate = pending
            duplicate =! None
            renewals.Contexts.Length =! 1
            receiver.Completed.Length =! 2
        }

        run "ReceiveAndDelete still emits messages without renewal" <| fun () -> task {
            let receiver = new Receiver(ServiceBusReceiveMode.ReceiveAndDelete)
            let renewals = Renewals()
            use! instance = consumer (Consumer.Options()) renewals.Start receiver
            receiver.Enqueue 1L |> ignore
            let! received = get instance
            received.IsSome =! true
            renewals.Contexts.Length =! 0
        }

        run "deserialization failures dead-letter without admitting work" <| fun () -> task {
            let receiver = new Receiver()
            let renewals = Renewals()
            use! instance =
                create (Consumer.Options()) renewals.Start
                    (fun _ -> invalidOp "invalid payload") (fun () -> receiver)
            receiver.Enqueue 1L |> ignore
            let! received = get instance
            received =! None
            receiver.DeadLetters =! 1
            renewals.Contexts.Length =! 0
        }

        run "renewal lock loss is logged and a replacement token is renewed" <| fun () -> task {
            let receiver = new Receiver()
            let steps = RenewalSteps()
            let events = ConcurrentQueue<string>()
            let options = Consumer.Options(Log = Log(fun (_, values) -> events.Enqueue(string values[0])))
            let lost = signal<unit> ()
            receiver.Renew <- fun _ ->
                lost.TrySetResult() |> ignore
                Task.FromException<DateTimeOffset>(
                    ServiceBusException("expired", ServiceBusFailureReason.MessageLockLost))
            use! instance = consumer options (Consumer.Renewable.start steps.Delay) receiver
            receiver.Enqueue 1L |> ignore
            let! _ = get instance
            let! firstTick = steps.Next()
            firstTick.SetResult()
            do! lost.Task.WaitAsync timeout
            receiver.Renew <- fun _ -> Task.FromResult(DateTimeOffset.UtcNow.AddMinutes 5.)
            let replacement = receiver.Enqueue 1L
            let! duplicate = get instance
            duplicate =! None
            let! nextTick = steps.Next()
            nextTick.SetResult()
            let! _ = steps.Next()
            receiver.Renewed.Length =! 2
            receiver.Renewed[1].LockToken =! replacement.LockToken
            Expect.contains (events.ToArray()) "Delivery ownership lost" "lock loss is observable"
            do! instance.Ack "work-item"
            receiver.Completed[0].LockToken =! replacement.LockToken
        }

        run "unexpected renewal failure faults supervision immediately and survives cleanup" <| fun () -> task {
            let receiver = new Receiver()
            let steps = RenewalSteps()
            let error = InvalidOperationException("unexpected renewal failure")
            receiver.Renew <- fun _ -> Task.FromException<DateTimeOffset> error
            let! instance = consumer (Consumer.Options()) (Consumer.Renewable.start steps.Delay) receiver
            receiver.Enqueue 1L |> ignore
            let! _ = get instance
            let supervision = (instance :?> ConsumerSupervision).Completion
            let! tick = steps.Next()
            tick.SetResult()
            let! caught = fails<InvalidOperationException> (supervision.WaitAsync timeout)
            obj.ReferenceEquals(caught, error) =! true
            let! duringGet = fails<InvalidOperationException> (get instance)
            obj.ReferenceEquals(duringGet, error) =! true
            let! duringAck = fails<InvalidOperationException> (instance.Ack "work-item")
            obj.ReferenceEquals(duringAck, error) =! true
            instance.Dispose()
            supervision.IsFaulted =! true
        }

        run "unexpected cancellation is not treated as normal renewal shutdown" <| fun () -> task {
            let receiver = new Receiver()
            let steps = RenewalSteps()
            receiver.Renew <- fun _ ->
                Task.FromException<DateTimeOffset>(OperationCanceledException("unrelated cancellation"))
            let! instance = consumer (Consumer.Options()) (Consumer.Renewable.start steps.Delay) receiver
            receiver.Enqueue 1L |> ignore
            let! _ = get instance
            let! tick = steps.Next()
            tick.SetResult()
            let supervision = (instance :?> ConsumerSupervision).Completion
            let! _ = fails<OperationCanceledException> (supervision.WaitAsync timeout)
            supervision.IsFaulted =! true
            let! _ = fails<OperationCanceledException> (instance.Nack "work-item")
            instance.Dispose()
        }

        run "transient renewal failure retries and healthy work has no arbitrary time cutoff" <| fun () -> task {
            let receiver = new Receiver()
            let steps = RenewalSteps()
            let clock = Clock()
            let options = Consumer.Options(Clock = clock)
            use! instance = consumer options (Consumer.Renewable.start steps.Delay) receiver
            receiver.Enqueue 1L |> ignore
            let! _ = get instance
            receiver.Renew <- fun _ ->
                Task.FromException<DateTimeOffset>(
                    ServiceBusException(true, "temporarily unavailable", "test-queue",
                        ServiceBusFailureReason.ServiceBusy))
            let! tick = steps.Next()
            tick.SetResult()
            let! retry = steps.Next()
            receiver.Renew <- fun _ -> Task.FromResult(clock.GetUtcNow().AddMinutes 5.)
            let mutable next = retry
            for step in 1..620 do
                clock.Advance(TimeSpan.FromSeconds 30.)
                next.SetResult()
                let! pending = steps.Next()
                next <- pending
            receiver.Renewed.Length =! 621
            (instance :?> ConsumerSupervision).Completion.IsCompleted =! false
            do! instance.Ack "work-item"
        }

        run "redelivery preserves the caller's original deadline and cancellation stops current renewal" <| fun () -> task {
            let receiver = new Receiver()
            let steps = RenewalSteps()
            let clock = Clock()
            let options = Consumer.Options(Clock = clock, PassiveNack = true)
            use! instance = consumer options (Consumer.Renewable.start steps.Delay) receiver
            receiver.Enqueue 1L |> ignore
            let! admitted = get instance
            admitted.IsSome =! true
            let deadline = clock.GetUtcNow().AddHours 1.
            let! oldTick = steps.Next()
            clock.Advance(TimeSpan.FromMinutes 55.)
            receiver.Enqueue 1L |> ignore
            let! duplicate = get instance
            duplicate =! None
            let! currentTick = steps.Next()
            clock.Advance(TimeSpan.FromMinutes 5.)
            clock.GetUtcNow() =! deadline
            do! instance.Nack "work-item"
            oldTick.TrySetResult() |> ignore
            currentTick.TrySetResult() |> ignore
            // Nack awaits the generation, so even an already-ready timer cannot renew afterward.
            receiver.Renewed.Length =! 0
            instance.Dispose()
            (instance :?> ConsumerSupervision).Completion.IsCompletedSuccessfully =! true
        }

        run "old in-flight renewal cannot clean up the replacement context" <| fun () -> task {
            let receiver = new Receiver()
            let renewals = Renewals()
            let entered = signal<unit> ()
            let release = signal<unit> ()
            let start (context: Consumer.MessageContext) : Task =
                if context.Message.DeliveryCount = 1 then
                    task {
                        use registration = context.CancellationTokenSource.Token.Register(fun () -> entered.SetResult())
                        do! release.Task
                    }
                else renewals.Start context
            use! instance = consumer (Consumer.Options()) start receiver
            receiver.ReceiveOverride <- Some (fun () ->
                Task.FromResult(ServiceBusModelFactory.ServiceBusReceivedMessage(
                    messageId = "work-item", sequenceNumber = 1L, deliveryCount = 1)))
            let! _ = get instance
            receiver.ReceiveOverride <- None
            let replacement = receiver.Enqueue(1L, deliveryCount = 2)
            let pending = get instance
            do! entered.Task.WaitAsync timeout
            let ack = instance.Ack "work-item"
            ack.IsCompleted =! false
            release.SetResult()
            let! duplicate = pending
            duplicate =! None
            do! ack
            receiver.Completed[0].LockToken =! replacement.LockToken
            (snd renewals.Contexts[0]).IsCancellationRequested =! true
        }

        run "completed retention expires after broker TTL and overlapping-receive grace" <| fun () -> task {
            let receiver = new Receiver()
            let clock = Clock()
            use! instance = consumer (Consumer.Options(Clock = clock)) Consumer.Renewable.noop receiver
            receiver.Enqueue 1L |> ignore
            let! _ = get instance
            do! instance.Ack "work-item"
            clock.Advance(TimeSpan.FromMinutes 30.)
            receiver.Enqueue 1L |> ignore
            let! retained = get instance
            retained =! None
            clock.Advance(TimeSpan.FromHours 1.)
            receiver.Enqueue 1L |> ignore
            let! expired = get instance
            expired.IsSome =! true
        }

        run "dead-letter redelivery stays suppressed beyond the message TTL" <| fun () -> task {
            let receiver = new Receiver()
            let clock = Clock()
            let options = Consumer.Options(Clock = clock, SubQueue = SubQueue.DeadLetter)
            use! instance = consumer options Consumer.Renewable.noop receiver
            receiver.Enqueue 1L |> ignore
            let! _ = get instance
            do! instance.Ack "work-item"
            clock.Advance(TimeSpan.FromDays 2.)
            receiver.Enqueue 1L |> ignore
            let! duplicate = get instance
            duplicate =! None
        }

        run "disposal does not hide an unexpected receive error" <| fun () -> task {
            let receiver = new Receiver()
            let entered = signal<unit> ()
            let received = signal<ServiceBusReceivedMessage> ()
            receiver.ReceiveOverride <- Some (fun () -> entered.SetResult(); received.Task)
            use! instance = consumer (Consumer.Options()) Consumer.Renewable.noop receiver
            let pending = get instance
            do! entered.Task.WaitAsync timeout
            instance.Dispose()
            let error = InvalidOperationException("unexpected receive error")
            received.SetException error
            let! caught = fails<InvalidOperationException> pending
            obj.ReferenceEquals(caught, error) =! true
        }

        run "replacement after receiver recreation renews and settles on the new receiver" <| fun () -> task {
            let first = new Receiver()
            let second = new Receiver()
            let receivers = ConcurrentQueue<ServiceBusReceiver>([ first; second ])
            let steps = RenewalSteps()
            use! instance =
                create (Consumer.Options()) (Consumer.Renewable.start steps.Delay) (fun m -> m.MessageId)
                    (fun () -> let ok, receiver = receivers.TryDequeue() in if ok then receiver else failtest "extra receiver")
            first.Enqueue 1L |> ignore
            let! _ = get instance
            let! _ = steps.Next()
            first.MarkClosed()
            let replacement = second.Enqueue 1L
            let! duplicate = get instance
            duplicate =! None
            let! tick = steps.Next()
            tick.SetResult()
            let! _ = steps.Next()
            first.Renewed.Length =! 0
            second.Renewed[0].LockToken =! replacement.LockToken
            do! instance.Nack "work-item"
            second.Abandoned[0].LockToken =! replacement.LockToken
            first.Abandoned.Length =! 0
        }

        run "completed identities include the entity when a receiver is recreated" <| fun () -> task {
            let first = new Receiver(entity = "first-queue")
            let second = new Receiver(entity = "second-queue")
            let receivers = ConcurrentQueue<ServiceBusReceiver>([ first; second ])
            use! instance =
                create (Consumer.Options()) Consumer.Renewable.noop (fun m -> m.MessageId)
                    (fun () -> let ok, receiver = receivers.TryDequeue() in if ok then receiver else failtest "extra receiver")
            first.Enqueue 1L |> ignore
            let! _ = get instance
            do! instance.Ack "work-item"
            first.MarkClosed()
            second.Enqueue 1L |> ignore
            let! received = get instance
            received.IsSome =! true
        }

        run "settlement and renewal errors both survive cleanup" <| fun () -> task {
            let receiver = new Receiver()
            let failed = signal<unit> ()
            let renewalError = InvalidOperationException("renewal error")
            let settlementError = InvalidOperationException("settlement error")
            receiver.Complete <- fun _ -> Task.FromException settlementError
            use! instance = consumer (Consumer.Options()) (fun _ -> failed.Task) receiver
            receiver.Enqueue 1L |> ignore
            let! _ = get instance
            failed.SetException renewalError
            let! _ = fails<InvalidOperationException> ((instance :?> ConsumerSupervision).Completion)
            let! aggregate = fails<AggregateException> (instance.Ack "work-item")
            Expect.contains aggregate.InnerExceptions renewalError "renewal error is retained"
            Expect.contains aggregate.InnerExceptions settlementError "settlement error is retained"
        }

        run "public factory preserves options and exposes the optional supervision contract" <| fun () -> task {
            let receiver = new Receiver()
            let client = new Client(fun () -> receiver)
            let events = ConcurrentQueue<obj[]>()
            let log = Log(fun (_, values) -> events.Enqueue values)
            use channels =
                Channels.mkNew (fun () -> client) (fun () -> failtest "unexpected admin client")
                    log { ChannelOptions.Default with PassiveNack = true }
            use! instance = channels.GetConsumer (OfReceived(fun m -> m.MessageId)) (DeadLetter "test-queue")
            let supervision = (instance :?> ConsumerSupervision).Completion
            receiver.Enqueue 1L |> ignore
            let! _ = get instance
            receiver.Enqueue 1L |> ignore
            let! _ = get instance
            events.Count =! 1
            do! instance.Nack "work-item"
            receiver.Abandoned.Length =! 0
            instance.Dispose()
            do! supervision.WaitAsync timeout
        }

        run "disabled duplicate suppression rejects active redelivery and retains the original" <| fun () -> task {
            let receiver = new Receiver()
            let renewals = Renewals()
            use! instance = consumer (Consumer.Options(IgnoreDuplicates = false)) renewals.Start receiver
            let first = receiver.Enqueue 1L
            let! _ = get instance
            receiver.Enqueue 1L |> ignore
            let! _ = fails<InvalidOperationException> (get instance)
            renewals.Contexts.Length =! 1
            do! instance.Ack "work-item"
            receiver.Completed[0].LockToken =! first.LockToken
        }

        run "unrelated cancellation concurrent with NACK remains an error" <| fun () -> task {
            let receiver = new Receiver()
            let cancelled = signal<unit> ()
            let failed = signal<unit> ()
            let start (context: Consumer.MessageContext) : Task =
                task {
                    use registration = context.CancellationTokenSource.Token.Register(fun () -> cancelled.SetResult())
                    do! failed.Task
                }
            use! instance = consumer (Consumer.Options(PassiveNack = true)) start receiver
            receiver.Enqueue 1L |> ignore
            let! _ = get instance
            let nack = instance.Nack "work-item"
            do! cancelled.Task.WaitAsync timeout
            failed.SetException(OperationCanceledException("unrelated cancellation"))
            let! _ = fails<OperationCanceledException> nack
            (instance :?> ConsumerSupervision).Completion.IsFaulted =! true
        }

        run "redelivery never invokes the application deserializer twice" <| fun () -> task {
            let receiver = new Receiver()
            let mutable calls = 0
            let decode (received: ServiceBusReceivedMessage) =
                calls <- calls + 1
                if calls > 1 then failtest "duplicate deserialization"
                received.MessageId
            use! instance = create (Consumer.Options()) Consumer.Renewable.noop decode (fun () -> receiver)
            receiver.Enqueue 1L |> ignore
            let! _ = get instance
            receiver.Enqueue 1L |> ignore
            let! _ = get instance
            do! instance.Ack "work-item"
            receiver.Enqueue 1L |> ignore
            let! _ = get instance
            calls =! 1
        }

        run "dead-letter deserialization errors are surfaced without settling" <| fun () -> task {
            let receiver = new Receiver()
            let error = InvalidOperationException("invalid dead-letter payload")
            use! instance =
                create (Consumer.Options(SubQueue = SubQueue.DeadLetter)) Consumer.Renewable.noop
                    (fun _ -> raise error) (fun () -> receiver)
            receiver.Enqueue 1L |> ignore
            let! caught = fails<InvalidOperationException> (get instance)
            obj.ReferenceEquals(caught, error) =! true
            receiver.DeadLetters =! 0
            receiver.Completed.Length =! 0
        }

        run "a reporting failure cannot leak the replaced delivery" <| fun () -> task {
            let receiver = new Receiver()
            let renewals = Renewals()
            let options = Consumer.Options(Log = Log(fun _ -> invalidOp "reporting failed"))
            use! instance = consumer options renewals.Start receiver
            receiver.Enqueue 1L |> ignore
            let! _ = get instance
            let previous = fst renewals.Contexts[0]
            receiver.Enqueue 1L |> ignore
            let! _ = fails<InvalidOperationException> (get instance)
            Expect.throwsT<ObjectDisposedException>
                (fun () -> previous.CancellationTokenSource.Token |> ignore)
                "replaced delivery has been released"
        }

        run "a reporting failure cannot hide the settlement error" <| fun () -> task {
            let receiver = new Receiver()
            let reportError = InvalidOperationException("reporting failed")
            let settleError = InvalidOperationException("settlement failed")
            let options = Consumer.Options(Log = Log(fun _ -> raise reportError))
            receiver.Complete <- fun _ -> Task.FromException settleError
            use! instance = consumer options Consumer.Renewable.noop receiver
            receiver.Enqueue 1L |> ignore
            let! _ = get instance
            let! aggregate = fails<AggregateException> (instance.Ack "work-item")
            Expect.contains aggregate.InnerExceptions reportError "report error is preserved"
            Expect.contains aggregate.InnerExceptions settleError "settlement error is preserved"
        }

        run "a delayed older delivery cannot replace a newer delivery" <| fun () -> task {
            let receiver = new Receiver()
            let renewals = Renewals()
            use captured = new ManualResetEventSlim()
            use release = new ManualResetEventSlim()
            let older =
                ServiceBusModelFactory.ServiceBusReceivedMessage(
                    messageId = "work-item", sequenceNumber = 1L, deliveryCount = 1,
                    lockTokenGuid = Guid.NewGuid())
            let newer =
                ServiceBusModelFactory.ServiceBusReceivedMessage(
                    messageId = "work-item", sequenceNumber = 1L, deliveryCount = 2,
                    lockTokenGuid = Guid.NewGuid())
            receiver.ReceiveOverride <- Some (fun () ->
                let ready = Task.FromResult older
                captured.Set()
                if not (release.Wait timeout) then failtest "receive barrier timed out"
                ready)
            use! instance = consumer (Consumer.Options()) renewals.Start receiver
            let pending = Task.Run<Envelope<string> option>(fun () -> get instance)
            try
                captured.Wait timeout =! true
                receiver.ReceiveOverride <- Some (fun () -> Task.FromResult newer)
                let! admitted = get instance
                admitted.IsSome =! true
            finally release.Set()
            let! duplicate = pending
            duplicate =! None
            do! instance.Ack "work-item"
            receiver.Completed[0].LockToken =! newer.LockToken
            renewals.Contexts.Length =! 1
        }

        for isAck in [ true; false ] do
            run $"terminal lock loss cannot hide an unexpected renewal error ack={isAck}" <| fun () -> task {
                let receiver = new Receiver()
                let failed = signal<unit> ()
                let stopped = signal<unit> ()
                let renewalError = InvalidOperationException("renewal failed")
                let lockLoss = ServiceBusException("expired", ServiceBusFailureReason.MessageLockLost)
                receiver.Complete <- fun _ -> Task.FromException lockLoss
                receiver.Abandon <- fun _ -> Task.FromException lockLoss
                let start (context: Consumer.MessageContext) : Task =
                    task {
                        use registration = context.CancellationTokenSource.Token.Register(fun () -> stopped.SetResult())
                        do! failed.Task
                    }
                use! instance = consumer (Consumer.Options()) start receiver
                receiver.Enqueue 1L |> ignore
                let! _ = get instance
                let settlement = if isAck then instance.Ack "work-item" else instance.Nack "work-item"
                do! stopped.Task.WaitAsync timeout
                settlement.IsCompleted =! false
                failed.SetException renewalError
                let! aggregate = fails<AggregateException> settlement
                Expect.contains aggregate.InnerExceptions lockLoss "broker lock loss is retained"
                Expect.contains aggregate.InnerExceptions renewalError "unexpected renewal failure is retained"
                let! _ = fails<InvalidOperationException> ((instance :?> ConsumerSupervision).Completion)
                ()
            }
    ]
