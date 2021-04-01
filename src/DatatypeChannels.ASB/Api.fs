namespace DatatypeChannels.ASB

open System
open System.Threading.Tasks
open Azure.Messaging.ServiceBus
open Azure.Messaging.ServiceBus.Administration

/// Envelope contains message and the session-bound unique id used for acknowlegements.
type Envelope<'msg> =
    { Msg : 'msg
      Id : string }

/// Binding combines subscription and rule definitions
type Binding =
    { Subscription: CreateSubscriptionOptions
      Rule: CreateRuleOptions option }

/// Defines the source of messages for the consumer.
/// Temporary queues get a GUID name and autoack received messages
type Source =
    | Subscription of Binding // directly off a subscription
    | DeadLetter of string // entity path: https://docs.microsoft.com/en-us/azure/service-bus-messaging/service-bus-dead-letter-queues#path-to-the-dead-letter-queue
    | Persistent of CreateQueueOptions * Binding list
    | Temporary of Binding list // for short-living consumers that only need to receive while they are up

/// Topic name
[<Struct>]
type Topic = Topic of string

[<RequireQualifiedAccessAttribute>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Topic =
    let toString (Topic topic) = topic

/// EventPublisher abstraction for publishing events to a topic.
[<Struct>]
type Publisher<'msg> = Publisher of ('msg -> Task<unit>)

/// Diagnostics logging interface: format with {placeholders} * corresponding values array
[<Struct>]
type Log = Log of (string * obj[] -> unit)

/// Consumer interface.
type Consumer<'msg> =
    inherit IDisposable
    abstract member Get : TimeSpan -> Task<Envelope<'msg> option>
    abstract member Ack : string -> Task<unit>
    abstract member Nack : string -> Task<unit>

/// Assembles user message from AMQP properties
type OfReceived<'msg> = OfReceived of (ServiceBusReceivedMessage -> 'msg)

/// Disassembles user message into AMQP properties
type ToSend<'msg> = ToSend of ('msg -> ServiceBusMessage)

/// Channels is a factory for constructing channel consumers and publishers.
type Channels =
    inherit IDisposable

    /// Construct a consumer, using specified message type, the source to bind to and the assember.
    abstract GetConsumer<'msg> : OfReceived<'msg> -> Source -> Consumer<'msg>

    /// Construct a publisher for the specified message type and disassembler.
    abstract GetPublisher<'msg> : ToSend<'msg> -> Topic -> Publisher<'msg>

    /// Use a publisher with a continuation for the specified message type and disassembler.
    abstract UsingPublisher<'msg> : ToSend<'msg> -> Topic -> (Publisher<'msg> -> Task<unit>) -> Task<unit>

/// Infix operators.
[<AutoOpen>]
module Operators =
    let inline (|->) (msg:^msg) (withPublisher:(Publisher<'msg> -> Task<unit>) -> Task<unit>) : Task<unit> =
        withPublisher (fun (Publisher send) -> send msg)
