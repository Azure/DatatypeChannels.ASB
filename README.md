# Azure Service Bus Datatype Channels

[![Build](https://github.com/Azure/DatatypeChannels.ASB/actions/workflows/azure.datatypechannels.asb.yml/badge.svg)](https://github.com/Azure/DatatypeChannels.ASB/actions/workflows/azure.datatypechannels.asb.yml)
[![NuGet](https://img.shields.io/nuget/v/DatatypeChannels.ASB.svg?style=flat)](https://www.nuget.org/packages/DatatypeChannels.ASB/)


Small, F#-friendly abstraction layer over Azure Service Bus .NET clients, featuring:
- `Channels` module and interface that provide a way to construct `Consumer` and `Publisher` instances.
- Pull-based `Consumer` interface implementation with deterministic release and background message lock renewal.
- One-off and reusable `Publisher` instances.
- Subscription and queue based bindings, with automatic upkeep (create/update), as well as temporary and deadletter queues.
- Pluggable serialization and conversion between bus primitives and application message representation.

`Consumer` and `Publisher` types facilitate both sides of [Datatype Channel pattern](https://www.enterpriseintegrationpatterns.com/patterns/messaging/DatatypeChannel.html) and pull-based consumer is useful for implementation of [CEP](https://en.wikipedia.org/wiki/Complex_event_processing) systems with backpressure/throttling.

> Loosely based on [FsBunny](https://et1975.github.io/FsBunny) - F# API for event streaming over RMQ.

## Delivery ownership and settlement

For PeekLock consumers, a redelivery of an active broker message replaces its
transport context and renewal task without returning another application envelope.
Renewal and settlement use the receiver that delivered the current lock token, even
after a receiver is recreated. Replacement, ACK, NACK and disposal are serialized.
An older delivery count arriving late cannot replace a newer tracked delivery.
`IgnoreDuplicates = false` rejects active duplicates instead of replacing them.

Broker identity includes the namespace, entity, message ID and sequence number.
Different broker messages sharing a message ID are not silently acknowledged. An
active ID collision is rejected without settlement because the string-based
ACK/NACK API cannot address two active messages with the same ID. After the first
message finishes, a different sequence number can be admitted normally.

Call `Ack` only after application processing and any required durable result
publication have completed. ACK ends local application ownership even when broker
settlement fails; same-message redeliveries are then settled without repeating
application work. Completed identities are retained until broker expiry, with at
least five minutes of grace after ACK for overlapping receives. Dead-letter
identities are retained for the consumer's lifetime because those messages do not
expire. Expired entries are removed on subsequent receives/ACKs. Long message TTLs
and long-lived dead-letter consumers therefore retain more identities in memory.
This tracking is local to one consumer, not durable or distributed exactly-once
processing.

`Nack` releases local ownership and permits a later application attempt.
`PassiveNack = true` skips broker abandonment but still stops and awaits renewal.
ACK/NACK always release their context, including when settlement fails. Settlement
exceptions, including `ServiceBusException` with reason `MessageLockLost`, remain
visible to the caller. A caller may handle that specific reason after terminal
application work; it must not suppress unrelated failures. If settlement and
renewal both fail, cleanup reports both exceptions.

Unexpected background renewal failures also fault the optional
`ConsumerSupervision.Completion : Task` contract on consumers returned by
`Channels`. Observe that task and cancel outstanding application work on failure.
It completes normally on disposal. Existing `Consumer<'T>` and `ChannelOptions`
contracts are unchanged. Definitive renewal lock loss stops only that delivery
generation; transient broker errors retry at the existing renewal interval.
These events and delivery replacement are reported through the supplied `Log`.

Application deadlines remain caller-owned. A replacement delivery does not reset
a deadline or create another invocation. At the original deadline, cancel and
await application work, then NACK or dispose the consumer to stop renewal.
Stopping transport renewal does not itself cancel application work.
Disposal also fences pending receives: a receive that finishes after disposal
cannot register renewal or return new work.

## Building
Pre-requisites:
- .NET SDK 10.0
- Azure CLI

When building for the first time:
```
dotnet tool restore
dotnet fsi build.fsx -t init <YOUR_LOCATION> <YOUR_RG> <YOUR_NAMESPACE>
```
Where 
- <YOUR_LOCATION> is Azure region where Service Bus will be provisioned
- <YOUR_RG> is the name of the resource group to deploy into
- <YOUR_NAMESPACE> Azure Service Bus namespace to create 

```
dotnet fsi build.fsx
```

The consumer unit regressions use fake Service Bus receivers and require no Azure
credentials or resources:

```
dotnet test --filter "FullyQualifiedName~consumer"
```

The existing `integration` tests still require a configured Service Bus namespace.
Their cloud setup is deferred until a selected integration test runs, so discovery
and filtered unit runs do not provision resources.


## Contributing

This project welcomes contributions and suggestions.  Most contributions require you to agree to a
Contributor License Agreement (CLA) declaring that you have the right to, and actually do, grant us
the rights to use your contribution. For details, visit https://cla.opensource.microsoft.com.

When you submit a pull request, a CLA bot will automatically determine whether you need to provide
a CLA and decorate the PR appropriately (e.g., status check, comment). Simply follow the instructions
provided by the bot. You will only need to do this once across all repos using our CLA.

This project has adopted the [Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/).
For more information see the [Code of Conduct FAQ](https://opensource.microsoft.com/codeofconduct/faq/) or
contact [opencode@microsoft.com](mailto:opencode@microsoft.com) with any additional questions or comments.

## Trademarks

This project may contain trademarks or logos for projects, products, or services. Authorized use of Microsoft 
trademarks or logos is subject to and must follow 
[Microsoft's Trademark & Brand Guidelines](https://www.microsoft.com/en-us/legal/intellectualproperty/trademarks/usage/general).
Use of Microsoft trademarks or logos in modified versions of this project must not cause confusion or imply Microsoft sponsorship.
Any use of third-party trademarks or logos are subject to those third-party's policies.
