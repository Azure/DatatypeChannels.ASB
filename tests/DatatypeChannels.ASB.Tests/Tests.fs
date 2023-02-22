module DatatypeChannels.ASB.Tests

open System
open System.Threading.Tasks
open Azure.Messaging.ServiceBus
open Azure.Messaging.ServiceBus.Administration
open Expecto
open Swensen.Unquote

type TestId = TestId of string

module PlainText =
    let ofReceived  =
        fun (sbm: ServiceBusReceivedMessage) -> sbm.Body.ToString()
        |> OfReceived

    let toSend (TestId testId) =
        fun (msg:string) -> 
            let sbm = ServiceBusMessage(Body = BinaryData msg)
            sbm.ApplicationProperties.["TestId"] <- testId
            sbm
        |> ToSend


module Logging =
    open Microsoft.Extensions.Logging
    open OpenTelemetry
    open OpenTelemetry.Trace
    open OpenTelemetry.Resources

    let info =
        let log = LoggerFactory.Create(fun builder -> builder.AddConsole() |> ignore).CreateLogger<Channels>()
        fun (msg: string, args: obj[]) -> log.LogInformation(msg, args)
        |> Log
    let tracing = 
        Sdk.CreateTracerProviderBuilder()
            // .AddSource("Azure.*")
            .AddSource("DatatypeChannels.*")
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("Tests"))
            .AddConsoleExporter()
            .Build()

module Settings =
    open Microsoft.Extensions.Configuration

    [<CLIMutable>]
    type TestSettings = 
        { ServiceBus: string }

    let settings = 
        let config = ConfigurationBuilder().AddJsonFile("local.settings.json",true).Build()
        let settings = { ServiceBus = "" }
        config.Bind settings
        settings


let it name (mkTest: TestId -> _) =
    (fun () -> TestId name |> mkTest) |> testCase name

let itt name (mkTask: TestId -> Task<_>) =
    (fun () -> mkTask(TestId name).Result) |> testCase name

module TempTopic =
    let create fqNamespace (credential: Azure.Core.TokenCredential) (Log info) =
        let client = ServiceBusAdministrationClient(fqNamespace, credential)
        let options = CreateTopicOptions(Guid.NewGuid().ToString(), AutoDeleteOnIdle = TimeSpan.FromMinutes 5.)
        let t = task {
            let! _ = client.CreateTopicAsync options
            info("Created temp topic: {topic}", [|options.Name|])
            return Topic options.Name
        }
        t.Result

module Binding =
    let onTest (TestId testId) name (Topic topic) =
        { Subscription = CreateSubscriptionOptions(topic, name, MaxDeliveryCount = 1)
          Rule = CreateRuleOptions("testId", CorrelationRuleFilter() |> CorrelationRuleFilter.withProp "TestId" testId) |> Some }

[<Tests>]
let tests =
    let topic = TempTopic.create Settings.settings.ServiceBus
                                 (Azure.Identity.DefaultAzureCredential false)
                                 Logging.info
    use channels = Channels.fromFqdn Settings.settings.ServiceBus
                                     (Azure.Identity.DefaultAzureCredential false)
                                     Logging.info
    testList "integration" [
        itt "Creates src bindings" <| fun testId ->
            task {
                let src = [Binding.onTest (TestId "zzz") "creates-sub" topic] |> Temporary
                use! consumer = channels.GetConsumer PlainText.ofReceived src
                ()
            }

        itt "Creates subscription bindings" <| fun testId ->
            let src = Binding.onTest (TestId "zzz") "creates-sub" topic |> Subscription
            task {
                use! consumer = channels.GetConsumer PlainText.ofReceived src
                ()
            }

        itt "Updates bindings" <| fun testId ->
            let src =
                (CreateQueueOptions("test-src"),
                 [Binding.onTest testId "updates-sub" topic]) |> Persistent
            task {
                use! consumer = channels.GetConsumer PlainText.ofReceived src
                let src =
                    (CreateQueueOptions("test-src", AutoDeleteOnIdle = TimeSpan.FromMinutes 5.),
                    [Binding.onTest testId "updates-sub" topic]) |> Persistent
                use! consumer = channels.GetConsumer PlainText.ofReceived src
                ()
            }

        itt "Roundtrips" <| fun testId ->
            task {
                let src = [Binding.onTest testId "roundtrips-sub" topic] |> Temporary
                let! consumer = channels.GetConsumer PlainText.ofReceived src
                let publisher = channels.GetPublisher (PlainText.toSend testId) topic 
                do! Task.Delay 5_000 // majic number - this is how long it takes the backend to start routing messages to this subscription!
                do! publisher |> Publisher.publish "test-payload"
                let! received = TimeSpan.FromSeconds 1. |> consumer.Get
                received.Value.Msg =! "test-payload"
                let sw = System.Diagnostics.Stopwatch.StartNew()
                let! received = TimeSpan.FromSeconds 1. |> consumer.Get
                sw.Stop()
                received =! None
                printfn "\nslept for: %4.2fms" sw.Elapsed.TotalMilliseconds
            }

        itt "Reads deadletters" <| fun testId ->
            task {
                let src = Binding.onTest testId "dlq-sub" topic |> Subscription
                let! consumer = channels.GetConsumer PlainText.ofReceived src
                let dlq = sprintf "%s/Subscriptions/dlq-sub" (Topic.toString topic) |> DeadLetter
                let! dlqConsumer = channels.GetConsumer PlainText.ofReceived dlq
                let publisher = channels.GetPublisher (PlainText.toSend testId) topic
                do! Task.Delay 5_000 // majic number - this is how long it takes the backend to start routing messages to this subscription!
                do! publisher |> Publisher.publish "test-payload"
                let! received = TimeSpan.FromSeconds 3. |> consumer.Get
                received.Value.Msg =! "test-payload"
                do! consumer.Nack received.Value.Id
                let! received = TimeSpan.FromSeconds 3. |> dlqConsumer.Get
                received.Value.Msg =! "test-payload"
            }

        itt "Exceeding the lock duration renews messages" <| fun testId ->
            task {
                let src = // 5min is the maximum LockDuration, we'll adjust it and setup the lock renewal
                    (CreateQueueOptions("renews-queue", LockDuration = TimeSpan.FromMinutes 6., MaxDeliveryCount = 1, AutoDeleteOnIdle = TimeSpan.FromMinutes 10.),
                    [Binding.onTest testId "renews-sub" topic]) |> Persistent
                let! consumer = channels.GetConsumer PlainText.ofReceived src
                let! dlc = DeadLetter "renews-queue" |> channels.GetConsumer PlainText.ofReceived
                let publisher = channels.GetPublisher (PlainText.toSend testId) topic
                do! Task.Delay 5_000 // majic number - this is how long it takes the backend to start routing messages to this subscription!
                let n = 200
                for i in 1..n do
                    do! publisher |> Publisher.publish $"test-payload-{i}"
                let! xs = // also test parallel consumption, needs longer timeout to fetch each message
                    seq { for _ in 1..10 -> seq { for _ in 1..n/10 -> TimeSpan.FromSeconds 3. |> consumer.Get }}
                    |> Seq.map Task.WhenAll
                    |> Task.WhenAll
                    |> Task.map (Array.collect id)
                    |> Task.map (Array.choose id)
                (xs.Length, xs.Length) =! (xs |> Seq.distinct |> Seq.length, n)
                do! Task.Delay (TimeSpan.FromMinutes 7.)
                for received in xs do
                    do! consumer.Ack received.Id
            }

        itt "Roundtrips lots" <| fun testId ->
            task {
                let src = [Binding.onTest testId "roundtrip-lots-sub" topic] |> Temporary
                let! consumer = channels.GetConsumer PlainText.ofReceived src
                let publisher = channels.GetPublisher (PlainText.toSend testId) topic
                do! Task.Delay 5_000 // majic number - this is how long it takes the backend to start routing messages to this subscription!
                let n = 1_000
                for i in 1..n do
                    do! publisher |> Publisher.publish $"test-payload-{i}"
                Threading.Thread.Sleep 5_000
                let xs = ResizeArray()
                for i in 1..n do
                    let! received = TimeSpan.FromSeconds 1. |> consumer.Get
                    xs.Add received
                xs |> Seq.choose id |> Seq.distinct |> Seq.length =! n
            }
    ]
