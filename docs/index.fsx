(*** hide ***)
// This block of code is omitted in the generated HTML documentation. Use 
// it to define helpers that you do not want to show in the documentation.
#I "../tests/Azure.ServiceBus.DatatypeChannels.Tests/bin/Release/net5.0/publish"
#r "Azure.Identity.dll"
#r "Azure.Core.dll"
#r "Azure.ServiceBus.DatatypeChannels.dll"
#r "Ply.dll"
#r "Azure.Messaging.ServiceBus.dll"

open System
open FSharp.Control.Tasks.Builders
open Azure.Messaging.ServiceBus.Administration

(**
Data channeling API for Azure Service Bus
======================
Azure.ServiceBus.DatatypeChannels implements [Datatype Channel pattern](https://www.enterpriseintegrationpatterns.com/patterns/messaging/DatatypeChannel.html) on top of official Azure Service Bus clients.

A channel is an abstraction on top of Service Bus entities and it's implemented using topics, subscriptions and queues. 
The core idea is that while there are many channels, all carrying messages, every channel is dedicated to a single type of message. 

It works under assumptions that:

- the bus toplogy is shared among heterogenous players, it's not a private implementation detail  
- we can capture the message type via the topic + subscription rules 
- the consumer is long-lived and handles only one type of message
- the consumer decides when to pull the next message of a subscription or a queue
- the publishers can be long- or short- lived
- we have multiple serialization formats and may want to add new ones easily, this may introduce a new channel
- we never want to loose a message, a message channel gets but is unable to read should be deadlettered
- we control the receiving side of the bus topology and have 'Manage' permissions to create subscriptions and queues as necessary


Installing
======================

<div class="row">
  <div class="span1"></div>
  <div class="span6">
    <div class="well well-small" id="nuget">
      The Azure.ServiceBus.DatatypeChannels library can be <a href="https://nuget.org/packages/Azure.ServiceBus.DatatypeChannels">installed from NuGet</a>:
      <pre>dotnet add YOUR_PROJECT package Azure.ServiceBus.DatatypeChannels</pre>
    </div>
  </div>
  <div class="span1"></div>
</div>

Example
-------

This example demonstrates the complete roundtrip over the channel using Azure.ServiceBus.DatatypeChannels API:

*)
open Azure.ServiceBus.DatatypeChannels
// create the entry point - DatatypeChannels
let channels = Channels.fromFqdn "mynamespace.servicebus.windows.net"
                                 (Azure.Identity.DefaultAzureCredential false)
                                 (Log ignore) // no logging

// define the "source" - subscription and routing rules, together called "binding"
let src = Subscription { Subscription = CreateSubscriptionOptions("mytopic", "mysub", MaxDeliveryCount = 1), Rule = None }

// create a consumer, specifying the convertion function from bus primitives
let consumer = channels.GetConsumer PlainText.ofReceived src // see the tutorial for details

// create a publisher, specifying the target topic and the conversion function to bus primitives
let publisher = channels.GetPublisher PlainText.toSend (Topic "mytopic")  // see the tutorial for details
Threading.Thread.Sleep 5_000 // majic number - this is how long it takes the topic to start routing messages to new subscriptions
task {
    do! publisher |> Publisher.publish "test-payload"
    let! received = TimeSpan.FromSeconds 3. |> consumer.Get
    printfn "Received: %A" received
    do! consumer.Ack received.Value.Id
}

(**
Note that the API is task-based and the bindings are defined using `Azure.Messaging.ServiceBus.Administration` types.
Constructing a consumer establishes the connection and sets up or updates the subscription to the rule specified.
Other types of consumer sources are `DeadLetter`, `Queue` and `Temporary`.


Samples & documentation
-----------------------


 * [Tutorial](tutorial.html) goes into more details.

 
Contributing and copyright
--------------------------
The project is hosted on [GitHub][gh] where you can [report issues][issues], fork 
the project and submit pull requests. 

The library is available under MIT license, which allows modification and 
redistribution for both commercial and non-commercial purposes. For more information see the 
[License file][license] in the GitHub repository. 

  [content]: https://github.com/Azure/Azure.ServiceBus.DatatypeChannels/tree/master/docs/content
  [gh]: https://github.com/Azure/Azure.ServiceBus.DatatypeChannels
  [issues]: https://github.com/Azure/Azure.ServiceBus.DatatypeChannels/issues
  [readme]: https://github.com/Azure/Azure.ServiceBus.DatatypeChannels/blob/master/README.md
  [license]: https://github.com/Azure/Azure.ServiceBus.DatatypeChannels/blob/master/LICENSE.md


Copyright 2021 Microsoft
*)
