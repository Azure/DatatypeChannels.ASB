namespace ASB

open System
open System.Threading.Tasks
open FSharp.Control.Tasks.Builders
open Azure.Messaging.ServiceBus
open Azure.Messaging.ServiceBus.Administration

[<RequireQualifiedAccess>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module EventStreams =
    /// constructs event-stream publishers and consumers.
    /// retries: number of send/receieve attempts.
    /// prefetch: prefetch limit.
    /// tempIdle: temporary queue idle lifetime.
    let mkNew (mkClient: unit -> ServiceBusClient)
              (mkAdminClient: unit -> ServiceBusAdministrationClient)
              (log: Log)
              (retries: uint16)
              (prefetch: uint16 option)
              (tempIdle: TimeSpan) =
        let client = lazy mkClient()
        let adminClient = lazy mkAdminClient()
        let withClient cont = cont client.Value
        let withAdminClient cont = cont adminClient.Value

        { new EventStreams with
            member __.GetPublisher<'msg> (Topic topic) toSend : Publisher<'msg> =
                let sender = lazy client.Value.CreateSender topic
                fun send -> send sender.Value
                |> Publisher.mkNew retries toSend

            member __.GetConsumer<'msg> source ofRecevied: Consumer<'msg> =
                let withBindings, receiveOptions, renew =
                    match source with
                    | Subscription binding ->
                        let renew = Consumer.LockDuration.makeRenewable retries binding.Subscription.LockDuration
                        binding.Subscription.LockDuration <- min binding.Subscription.LockDuration Consumer.LockDuration.fiveMinutes
                        Subscription.withBinding log withAdminClient binding,
                        ServiceBusReceiverOptions(ReceiveMode = ServiceBusReceiveMode.PeekLock),
                        renew
                    | Persistent (queueOptions, bindings) ->
                        let renew = Consumer.LockDuration.makeRenewable retries queueOptions.LockDuration
                        queueOptions.LockDuration <- min queueOptions.LockDuration Consumer.LockDuration.fiveMinutes
                        Queue.withBindings log withAdminClient queueOptions bindings,
                        ServiceBusReceiverOptions(ReceiveMode = ServiceBusReceiveMode.PeekLock),
                        renew
                    | Temporary bindings ->
                        Queue.withBindings log withAdminClient (CreateQueueOptions(Guid.NewGuid().ToString(), AutoDeleteOnIdle = tempIdle)) bindings,
                        ServiceBusReceiverOptions(ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete),
                        Consumer.LockDuration.noRenew
                    | DeadLetter path ->
                        (fun cont -> Task.FromResult path |> cont),
                        ServiceBusReceiverOptions(ReceiveMode = ServiceBusReceiveMode.PeekLock, SubQueue = SubQueue.DeadLetter),
                        Consumer.LockDuration.noRenew
                prefetch |> Option.iter (fun v -> receiveOptions.PrefetchCount <- int v)
                Consumer.mkNew receiveOptions retries renew ofRecevied withClient withBindings

            member __.UsingPublisher<'msg> (Topic topic) toSend (cont:Publisher<'msg> -> Task<unit>) =
                task {
                    let sender = client.Value.CreateSender topic
                    do!
                        fun send -> send sender
                        |> Publisher.mkNew retries toSend
                        |> cont
                    do! sender.DisposeAsync()
                }

            member __.Dispose() =
                if client.IsValueCreated then client.Value.DisposeAsync().AsTask().Wait()
        }

    let fromFqdn (fqNamespace: string) (credential: Azure.Core.TokenCredential) (log: Log) =
        mkNew (fun _ -> ServiceBusClient(fqNamespace, credential))
              (fun _ -> ServiceBusAdministrationClient(fqNamespace, credential))
              log 3us None (TimeSpan.FromMinutes 5.)

    let fromConnectionString (connString: string) (log: Log) =
        mkNew (fun _ -> ServiceBusClient connString)
              (fun _ -> ServiceBusAdministrationClient connString)
              log 3us None (TimeSpan.FromMinutes 5.)
