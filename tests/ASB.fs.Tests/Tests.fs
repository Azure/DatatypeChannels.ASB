module ASB.Tests

open System
open System.Threading.Tasks
open Azure.Messaging.ServiceBus
open Azure.Messaging.ServiceBus.Administration
open FSharp.Control.Tasks.Builders
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
    let info =
        let log = LoggerFactory.Create(fun builder -> builder.AddConsole() |> ignore).CreateLogger<EventStreams>()
        fun (msg: string, args: obj[]) -> log.LogInformation(msg, args)
        |> Log

module Settings =

    [<CLIMutable>]
    type TestSettings = 
        { ServiceBus: string }

    let settings = 
        let config = ConfigurationBuilder()
                        .AddJsonFile("local.settings.json",false)
                        .AddEnvironmentVariables()
                        .Build()
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
    let topic = TempTopic.create "yetolmac1sb.servicebus.windows.net"
                                 (Azure.Identity.DefaultAzureCredential false)
                                 Logging.info
    use streams = EventStreams.fromFqdn "yetolmac1sb.servicebus.windows.net"
                                        (Azure.Identity.DefaultAzureCredential false)
                                        Logging.info
    testList "integration" [
        it "Creates src bindings" <| fun testId ->
            let src = [Binding.onTest (TestId "zzz") "creates-sub" topic] |> Temporary
            use consumer = streams.GetConsumer src PlainText.ofReceived
            ()

        it "Creates subscription bindings" <| fun testId ->
            let src = Binding.onTest (TestId "zzz") "creates-sub" topic |> Subscription
            use consumer = streams.GetConsumer src PlainText.ofReceived
            ()

        it "Updates bindings" <| fun testId ->
            let src =
                (CreateQueueOptions("test-src"),
                 [Binding.onTest testId "updates-sub" topic]) |> Persistent
            use consumer = streams.GetConsumer src PlainText.ofReceived
            let src =
                (CreateQueueOptions("test-src", AutoDeleteOnIdle = TimeSpan.FromMinutes 5.),
                 [Binding.onTest testId "updates-sub" topic]) |> Persistent
            use consumer = streams.GetConsumer src PlainText.ofReceived
            ()

        itt "Roundtrips" <| fun testId ->
            let src = [Binding.onTest testId "roundtrips-sub" topic] |> Temporary
            let consumer = streams.GetConsumer src PlainText.ofReceived
            let publisher = streams.GetPublisher topic (PlainText.toSend testId)
            Threading.Thread.Sleep 5_000 // majic number - this is how long it takes the backend to start routing messages to this subscription!
            task {
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
            let src = Binding.onTest testId "dlq-sub" topic |> Subscription
            let consumer = streams.GetConsumer src PlainText.ofReceived
            let dlq = sprintf "%s/Subscriptions/dlq-sub" (Topic.toString topic) |> DeadLetter
            let dlqConsumer = streams.GetConsumer dlq PlainText.ofReceived
            let publisher = streams.GetPublisher topic (PlainText.toSend testId)
            Threading.Thread.Sleep 5_000 // majic number - this is how long it takes the backend to start routing messages to this subscription!
            task {
                do! publisher |> Publisher.publish "test-payload"
                let! received = TimeSpan.FromSeconds 3. |> consumer.Get
                received.Value.Msg =! "test-payload"
                do! consumer.Nack received.Value.Id
                let! received = TimeSpan.FromSeconds 3. |> dlqConsumer.Get
                received.Value.Msg =! "test-payload"
            }

        itt "Exceeding the lock duration renews message lock" <| fun testId ->
            let src = 
                (CreateQueueOptions("renews-src", LockDuration = TimeSpan.FromMinutes 6.), // 5min is maximum, hence the renewal will kick in
                 [Binding.onTest testId "renews-sub" topic]) |> Persistent
            let consumer = streams.GetConsumer src PlainText.ofReceived
            let dlq = "renews-src" |> DeadLetter
            let dlc = streams.GetConsumer src PlainText.ofReceived
            let publisher = streams.GetPublisher topic (PlainText.toSend testId)
            Threading.Thread.Sleep 5_000 // majic number - this is how long it takes the backend to start routing messages to this subscription!
            task {
                do! publisher |> Publisher.publish "test-payload"
                let! received = TimeSpan.FromSeconds 1. |> consumer.Get
                do! Task.Delay (TimeSpan.FromMinutes 10.)
                do! consumer.Ack received.Value.Id
                let! received = TimeSpan.FromSeconds 1. |> dlc.Get
                received =! None
            }

        itt "Roundtrips lots" <| fun testId ->
            let src = [Binding.onTest testId "roundtrip-lots-sub" topic] |> Temporary
            let consumer = streams.GetConsumer src PlainText.ofReceived
            let publisher = streams.GetPublisher topic (PlainText.toSend testId)
            Threading.Thread.Sleep 5_000 // majic number - this is how long it takes the backend to start routing messages to this subscription!
            task {
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
