(*** hide ***)
// This block of code is omitted in the generated HTML documentation. Use 
// it to define helpers that you do not want to show in the documentation.
#I "../../src/ASB.fs/bin/Debug/netstandard2.1/publish"
#r "ASB.fs.dll"
#r "Ply.dll"
#r "Azure.Messaging.ServiceBus.dll"

open System
open FSharp.Control.Tasks.Builders
open Azure.Messaging.ServiceBus.Administration

(**
Data streaming API for Azure Service Bus
======================
ASB.fs implements a streaming API on top of official Azure Service Bus client for implementation of event-driven systems.

The core idea is that while there are many streams carrying many messages using many serializers, each stream is dedicated to a single type of message serialized using certain serializer. 

It works under assumptions that:

- we never want to loose a message
- the topic + subscription rules tell us the message type (as well as serialization format)
- the consumer is long-lived and handles only one type of message
- the consumer decides when to pull the next message of a subscription or a queue
- the publishers can be long- or short- lived
- we have multiple serialization formats and may want to add new ones easily


Installing
======================

<div class="row">
  <div class="span1"></div>
  <div class="span6">
    <div class="well well-small" id="nuget">
      The ASB.fs library can be <a href="https://nuget.org/packages/ASB.fs">installed from NuGet</a>:
      <pre>dotnet add YOUR_PROJECT package ASB.fs</pre>
    </div>
  </div>
  <div class="span1"></div>
</div>

Example
-------

This example demonstrates the API defined by ASB.fs:

*)
open ASB
// create the entry point - EventStreams
use streams = EventStreams.fromFqdn "mynamespace.servicebus.windows.net"
                                    (Azure.Identity.DefaultAzureCredential false)
                                    (Log ignore) // no logging
// define the "source" - subscription and routing rules, together called "binding"
let src = Subscription { Subscription = CreateSubscriptionOptions("mytopic", "mysub", MaxDeliveryCount = 1), Rule = None }
// create a consumer, specifying the convertion function from bus primitives
let consumer = streams.GetConsumer src PlainText.ofReceived // see the tutorial for the converter function explanation
// create a publisher, specifying the target topic and the conversion function to bus primitives
let publisher = streams.GetPublisher (Topic "mytopic") PlainText.toSend  // see the tutorial for the converter function explanation
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

 * [API Reference](reference/index.html) contains automatically generated documentation for all types, modules
   and functions in the library. This includes additional brief samples on using most of the
   functions.
 
Contributing and copyright
--------------------------
The project is hosted on [GitHub][gh] where you can [report issues][issues], fork 
the project and submit pull requests. 

The library is available under MIT license, which allows modification and 
redistribution for both commercial and non-commercial purposes. For more information see the 
[License file][license] in the GitHub repository. 

  [content]: https://github.com/Azure/ASB.fs/tree/master/docs/content
  [gh]: https://github.com/Azure/ASB.fs
  [issues]: https://github.com/Azure/ASB.fs/issues
  [readme]: https://github.com/Azure/ASB.fs/blob/master/README.md
  [license]: https://github.com/Azure/ASB.fs/blob/master/LICENSE.md


Copyright 2021 Microsoft
*)
