# Azure Service Bus Datatype Channels

[Build and release with Azure Pipelines](docs/azure-pipelines.md)
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
- .NET SDK matching `global.json` (10.0.403 with latest patch in that feature band)
- Access to the shared `avs-oss/Public/OSS-FSharp-Consumption` feed in
  `NuGet.config`. Empty-cache solution, FSI and existing tool-manifest restores
  passed anonymously after authorized upstream caching. New dependency versions
  must be saved by an authorized trusted pipeline or curator before
  credential-free PRs can consume them.
  Release images still require preseeded F# build dependencies. See the
  [setup guide](docs/azure-pipelines.md#required-administrator-configuration).
- Azure CLI only for explicitly requested local provisioning/integration

Build without Azure access:
```
dotnet fsi build.fsx -t build
```

`build.fsx` is the single entrypoint for local and pipeline orchestration, using
FAKE targets with separate dependency graphs. Discover the entire surface without
application credentials, local settings or target execution (the dependency
cache/feed prerequisite still applies):
```
dotnet fsi build.fsx -- --help
dotnet fsi build.fsx --list
dotnet fsi build.fsx -t graph ci official-compile official-pack official-stage integration publish test
```

The test suite is an integration suite. It creates Service Bus messaging entities
and requires an authenticated identity with access to a test namespace. It is
not an offline unit-test suite.

For local integration testing only, the following explicit initialization
**provisions Azure resources**. Do not run it for an offline build or in CI:
```
dotnet fsi build.fsx -t init <YOUR_LOCATION> <YOUR_RG> <YOUR_NAMESPACE>
```
Where 
- <YOUR_LOCATION> is Azure region where Service Bus will be provisioned
- <YOUR_RG> is the name of the resource group to deploy into
- <YOUR_NAMESPACE> Azure Service Bus namespace to create 

```
dotnet fsi build.fsx
```

CI uses two 1ES Azure Pipelines definitions: credential-free unofficial PR
validation (`.pipelines/azure-pipelines-pr.yml`) and official trusted
integration/package production (`.pipelines/azure-pipelines.yml`). They use
preinstalled SDK tools, an existing namespace, and Azure Pipelines
authentication. Trusted Build now wires native `EsrpCodeSigning@6` DLL and NuGet
signing between deliberate FAKE target boundaries. ESRP identity, vault,
certificates, authorized key codes, resource approvals and live 1ES validation
remain external prerequisites; this is not evidence of production readiness.
Trusted Build and Integration authenticate dependency resolution with
`NuGetAuthenticate@1` before FSI. The trusted restore identity requires
**Feed and Upstream Reader (Collaborator)** permission to save packages from the
feed's NuGet.org upstream, even though saved packages are public. PR/fork jobs
must not share that privileged identity or receive the task/connection; their
approved dependency closure must be anonymously readable.
The shared stage defaults to unsigned `build.fsx -t ci` for PRs, without signing
configuration or access. Official builds select `official-compile` → native DLL
sign/verify → `official-pack` (no rebuild) → native NuGet sign/verify →
`official-stage` (final signature checks/hash/staging). Only the two first-party
DLLs are signed; third-party dependencies are not re-signed. Trusted jobs then use
`check-trust`, `integration`, and `publish` on the same final signed artifact.
Release is an explicitly opted-in manual master run, not a Git tag trigger.
The `publish` target validates signatures and the tested artifact without building or packing;
the native `1ES.PublishNuGet@1` task performs the actual push through an approved
NuGet service connection after release approvals.
Neither definition runs local `init`; `initCI` remains retired and fails closed.
The default local target is still `test`, which contacts Azure. See the
[pipeline setup and cutover guide](docs/azure-pipelines.md) for administrator
prerequisites and release authorization.


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
