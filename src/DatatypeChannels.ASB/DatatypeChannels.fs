namespace DatatypeChannels.ASB

open System
open System.Threading.Tasks
open Azure.Messaging.ServiceBus
open Azure.Messaging.ServiceBus.Administration

type ChannelOptions = 
    { Prefetch: uint16 option // optional prefetch limit
      IgnoreDuplicates: bool
      TempIdle: TimeSpan } // temporary queue idle lifetime
    static member Default =
        { Prefetch = None
          TempIdle = TimeSpan.FromMinutes 5.
          IgnoreDuplicates = true }

[<RequireQualifiedAccess>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Channels =
    /// constructs event-stream publishers and consumers.
    /// mkClient: function to construct the client.
    /// mkAdminClient: function to construct the admin client.
    /// log: function for diagnostics logging.
    /// options: channel options.
    let mkNew (mkClient: unit -> ServiceBusClient)
              (mkAdminClient: unit -> ServiceBusAdministrationClient)
              (log: Log)
              (options: ChannelOptions) =
        let client = lazy mkClient()
        let adminClient = lazy mkAdminClient()
        let withClient cont = cont client.Value
        let withAdminClient cont = cont adminClient.Value

        { new Channels with
            member __.GetConsumer<'msg> ofRecevied source : Task<Consumer<'msg>> =
                let withBindings, receiveOptions, renew =
                    match source with
                    | Subscription binding ->
                        let renew = Consumer.Renewable.mkNew binding.Subscription.LockDuration
                        binding.Subscription.LockDuration <- min binding.Subscription.LockDuration Consumer.Renewable.maxLockDuration
                        Subscription.withBinding log withAdminClient binding,
                        Consumer.Options(ReceiveMode = ServiceBusReceiveMode.PeekLock),
                        renew
                    | Persistent (queueOptions, bindings) ->
                        let renew = Consumer.Renewable.mkNew queueOptions.LockDuration
                        queueOptions.LockDuration <- min queueOptions.LockDuration Consumer.Renewable.maxLockDuration
                        Queue.withBindings log withAdminClient queueOptions bindings,
                        Consumer.Options(ReceiveMode = ServiceBusReceiveMode.PeekLock),
                        renew
                    | Temporary bindings ->
                        Queue.withBindings log withAdminClient (CreateQueueOptions(Guid.NewGuid().ToString(), AutoDeleteOnIdle = options.TempIdle)) bindings,
                        Consumer.Options(ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete),
                        Consumer.Renewable.noop
                    | DeadLetter path ->
                        (fun cont -> Task.FromResult path |> cont),
                        Consumer.Options(ReceiveMode = ServiceBusReceiveMode.PeekLock, SubQueue = SubQueue.DeadLetter),
                        Consumer.Renewable.noop
                options.Prefetch |> Option.iter (fun v -> receiveOptions.PrefetchCount <- int v)
                Consumer.mkNew receiveOptions renew ofRecevied withClient withBindings

            member __.GetPublisher<'msg> toSend (Topic topic) : Publisher<'msg> =
                let sender = lazy client.Value.CreateSender topic
                fun send -> send sender.Value
                |> Publisher.mkNew toSend

            member __.UsingPublisher<'msg> toSend (Topic topic) (cont:Publisher<'msg> -> Task<unit>) =
                backgroundTask {
                    let sender = client.Value.CreateSender topic
                    do!
                        fun send -> send sender
                        |> Publisher.mkNew toSend
                        |> cont
                    do! sender.DisposeAsync()
                }

            member __.Dispose() =
                if client.IsValueCreated then client.Value.DisposeAsync().AsTask().Wait()
        }

    /// Build an instance using FQDN of the namespace and Azure TokenCredential
    let fromFqdn (fqNamespace: string) (credential: Azure.Core.TokenCredential) (log: Log) =
        ChannelOptions.Default
        |> mkNew (fun _ -> ServiceBusClient(fqNamespace, credential))
                 (fun _ -> ServiceBusAdministrationClient(fqNamespace, credential))
                 log

    /// Build an instance using connection string
    let fromConnectionString (connString: string) (log: Log) =
        ChannelOptions.Default
        |>  mkNew (fun _ -> ServiceBusClient connString)
                  (fun _ -> ServiceBusAdministrationClient connString)
                  log
