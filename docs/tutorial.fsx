(*** hide ***)
#I "../tests/DatatypeChannels.ASB.Tests/bin/Release/net5.0/publish"
#r "Azure.Core.dll"
#r "Azure.Identity.dll"
#r "DatatypeChannels.ASB.dll"
#r "Ply.dll"
#r "Azure.Messaging.ServiceBus.dll"

open System
(**
Namespaces
========================

DatatypeChannels.ASB is organized into 3 APIs:

- `Channels` API - connects to Azure Service Bus and provides constructors for publishers and consumers 
- `ToSend`/`OfReceived` types for packing and unpacking messages in and out of Azure Service Bus primitives
- `Publisher` and `Consumer` types - the primary means of interaction


Channels API
========================
`Channels` is the entry point into the API, it can be constructed with a connection string or FQDN of the namespace, for example:
*)

open DatatypeChannels.ASB

use channels = Channels.fromFqdn "mynamespace.servicebus.windows.net"
                                 (Azure.Identity.DefaultAzureCredential false)
                                 (Log ignore) // no logging

(**
For other construction parameters please see `Channels.mkNew`.
Internally two connections might be established - one for consumer bindings management and one for data activities.
The connections are established just in time for the first use and are cached for the lifetime of `Channels` factory.


Converting messages between application and bus representations
========================
`Publisher` and `Consumer` implement the opposite ends of a [Datatype Channel](https://www.enterpriseintegrationpatterns.com/patterns/messaging/DatatypeChannel.html), meaning that the type of message is determinated staticaly and for entire lifetime of the channel.
Implement `OfRecieved` and `ToSend` functions to convert from/to service bus primitives. 

Here's an example of a plain text converters:

*)


module PlainText =
    open Azure.Messaging.ServiceBus
    let ofReceived  =
        fun (sbm: ServiceBusReceivedMessage) -> sbm.Body.ToString()
        |> OfReceived

    let toSend =
        fun (msg:string) -> ServiceBusMessage(Body = BinaryData msg)
        |> ToSend



(**
Any exception during convertion from the bus primitives will result in message being Nacked and sent to the Dead Letter queue (if configured).

Publisher
========================
Publisher is just a function that takes a message and returns when completed and 
there are two ways to obtain it:

- For long-living use:
*)

let publisher = channels.GetPublisher PlainText.toSend (Topic "topic") // long-living publisher
(**
- Or for immediate use and disposal of the publisher:

*)
"test message" |-> channels.UsingPublisher PlainText.toSend (Topic "topic") // one-off publishing, using the infix operator


(** Consumer
========================
Consumer implicitly manages the susbcription every time it's created, updating forwarding and rules as needed. 
A persistent queue can be setup using one or more subscriptions, with forwarding setup automatically. 
`Temporary` queue can be useful for short-lived consumers and is setup with auto-acknowedgments and a guid for a name, it will be deleted once consumer is disposed of.
Implementing guaranteed processing a `Persistent` queue or a `Subscription` with Ack/Nack messages is recommended.

*)
open Azure.Messaging.ServiceBus.Administration

// define the a Subscription binding with no filters
let src = Subscription { Subscription = CreateSubscriptionOptions("mytopic", "mysub", MaxDeliveryCount = 1), Rule = None }

// create a consumer, specifying the convertion function from bus primitives
let consumer = channels.GetConsumer PlainText.ofReceived src

// another example of a source - deadletter queue of the subscription defined above
// this entity must exist, DatatypeChannels.ASB does not manage deadletter sources
let dlq = DeadLetter "mytopic/Subscriptions/mysub" 
let dlqConsumer = channels.GetConsumer PlainText.ofReceived dlq

// define the a Persistent queue binding
let consumer = Persistent (CreateQueueOptions("myqueue", LockDuration = TimeSpan.FromMinutes 6.),
                           [{ Subscription = CreateSubscriptionOptions("mytopic", "mysub", MaxDeliveryCount = 1), Rule = None }])

// define a Temporary queue binding, the messages will be auto-ack'ed
let tmp = Temporary [{ Subscription = CreateSubscriptionOptions("mytopic", "mysub", MaxDeliveryCount = 1), Rule = None }]

(**
Once created, we can start polling them, specifying the timeout:

*)
open FSharp.Control.Tasks.Builders

task {
    let! received = TimeSpan.FromSeconds 3. |> consumer.Get
    // for this example - negative acknowledge the message so that it's routed to the deadletters
    do! consumer.Nack received.Value.Id
    let! received = TimeSpan.FromSeconds 3. |> dlqConsumer.Get // now we can process it from the deadletters
}

(**
Note that:

- when using prefetch: w/ or not you called `Get` the prefetch itself counts as a delivery attempt 
- if expected message processing, specified as `LockDuration`, exceeds the maximum lock time, DatatypeChannels.ASB will override the duration with the valida maximum and setup background lock renewal task.

*)
