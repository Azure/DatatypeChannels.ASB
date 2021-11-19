# Azure Service Bus Datatype Channels

[![Build Status](https://dev.azure.com/azure-fsharp-libs/public/_apis/build/status/Azure.DatatypeChannels.ASB?branchName=master)](https://dev.azure.com/azure-fsharp-libs/public/_build/latest?definitionId=5&branchName=master)
[![NuGet](https://img.shields.io/nuget/v/DatatypeChannels.ASB.svg?style=flat)](https://www.nuget.org/packages/DatatypeChannels.ASB/)


Small, F#-friendly abstraction layer over Azure Service Bus .NET clients, featuring:
- `Channels` module and interface that provide a way to construct `Consumer` and `Publisher` instances.
- Pull-based `Consumer` interface implementation with deterministic release and background message lock renewal.
- One-off and reusable `Publisher` instances.
- Subscription and queue based bindings, with automatic upkeep (create/update), as well as temporary and deadletter queues.
- Pluggable serialization and conversion between bus primitives and application message representation.

`Consumer` and `Publisher` types facilitate both sides of [Datatype Channel pattern](https://www.enterpriseintegrationpatterns.com/patterns/messaging/DatatypeChannel.html) and pull-based consumer is useful for implementation of [CEP](https://en.wikipedia.org/wiki/Complex_event_processing) systems with backpressure/throttling.

> Loosely based on [FsBunny](https://et1975.github.io/FsBunny) - F# API for event streaming over RMQ.

## Building
Pre-requisites:
- .NET SDK 6.0
- Azure CLI

When building for the first time:
```
dotnet tool restore
dotnet fake build -t init <YOUR_LOCATION> <YOUR_RG> <YOUR_NAMESPACE>
```
Where 
- <YOUR_LOCATION> is Azure region where Service Bus will be provisioned
- <YOUR_RG> is the name of the resource group to deploy into
- <YOUR_NAMESPACE> Azure Service Bus namespace to create 

```
dotnet fake build
```


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
