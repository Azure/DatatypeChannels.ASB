# 1ES Azure Pipelines build and release

Migration tracking: [WI 38505857](https://dev.azure.com/msazure/One/_workitems/edit/38505857).
The WI requires removing GitHub federation to the Corp tenant and migrating to
Azure Pipelines. Eugene's June 19, 2026 comment records that the federated
credentials were removed. **Do not recreate the GitHub federated credentials**,
restore `AZURE_CREDENTIALS`, or add a client secret as a workaround.

This repository supplies YAML and scripts, not a configured live pipeline.
The explicitly authorized **OSS-FSharp-Consumption dependency feed** was created
in the existing `avs-oss/Public` project. No other resources, explicit permission
changes, identities or service connections were created. Administrators must
complete the setup below before enabling trusted runs. The WI is in `msazure/One`;
the owning team must confirm
the approved pipeline hosting project and policies rather than assuming that a
work-item project authorizes public-repository builds.

## Two definitions, not two release paths

| ADO definition | YAML path | Governed template | Authorization |
|---|---|---|---|
| PR validation | `/.pipelines/azure-pipelines-pr.yml` | `v1/1ES.Unofficial.PipelineTemplate.yml@1esPipelines` | Untrusted, isolated pools; no Azure/release/signing resources |
| Trusted integration and package production | `/.pipelines/azure-pipelines.yml` | `v1/1ES.Official.PipelineTemplate.yml@1esPipelines` | Protected refs, restricted queues and approved resources |

Both use the Azure Repos resource alias `1esPipelines`, repository
`1ESPipelineTemplates/1ESPipelineTemplates`, and `refs/tags/release`. Their stages
are under `extends.parameters.stages`. Only the build stage in
`.pipelines/build.yml` is shared. Select the entrypoint paths above when creating
or updating the ADO definitions; all pipeline YAML lives under `.pipelines`.
Do not authorize the PR definition for the trusted pool, integration connection,
release group/environment, or signing.
The unofficial definition never supplies an artifact to the official one.
There is no pipeline-completion release trigger.

## Behavior (after administrator prerequisites)

| Invocation | Build/package | Service Bus tests | NuGet publication |
|---|---|---|---|
| PR targeting master (unofficial definition) | Yes, no application credentials | No | Never |
| Draft PR | Not automatically | No | Never |
| Master push | Yes | Yes | Never |
| Tag push | Not triggered | No | Never |
| Manual master run, default parameters | Yes | Yes | Never |
| Manual master run, `publishPackage: true` | Yes | Required | Only after release approvals |
| Manual tag run (any parameters) | Custom stages skipped | No | Never |
| Manual unofficial run | Yes, candidate only | No | Never |
| Manual feature branch run in official definition | Custom stages skipped; do not authorize/queue | No | Never |

`build.fsx` is the single entrypoint for local and pipeline orchestration.
It uses the existing FAKE target engine, not a wrapper or a separate dispatcher.
The two YAML entrypoints remain separate trust boundaries, but both invoke
`dotnet fsi build.fsx -t ci` through the shared stage. This graph checks the
pipeline contracts, generates metadata, restores existing solution dependencies,
publishes Release build output, and packs only the already built library. It retains
both `nuget/` and compiled `tests/` in the same run's `drop` pipeline artifact.
The build declares `templateContext.outputs` with `output: pipelineArtifact`
instead of publishing directly, so 1ES controls output scanning and metadata.
The official signing-validation gate can block this output until signing is
established (see below). The integration stage tests those binaries with VSTest
and uploads TRX results.
Missing results, zero tests, failed tests, or skipped tests fail integration.
PR packages are **untested candidates**, not releasable outputs.

The release deployment is a production `releaseJob`, with a governed
`pipelineArtifact` input explicitly selecting `buildType: current`. 1ES owns
the download and source-build/SBOM/signature checks; no validation bypass is
configured. The release stage consumes the same run's artifact. `build.fsx -t publish`
validates the package
filename and nuspec ID/version, the package SHA256 recorded at build time, and
equality of the packaged library and integration-test library. Then the supported
**`1ES.PublishNuGet@1` task**, not a raw push hidden inside an F# script, publishes
that package using an approved external NuGet service connection and preinstalled
dotnet. No duplicate-skip option hides publication errors. Repeating a
successful publish will fail on an immutable feed, so inspect the existing
package before retrying. Failed or skipped integration cannot authorize release.
1ES release jobs cannot check out source. The build therefore includes only
`build.fsx`, `RELEASE_NOTES.md`, `global.json`, `NuGet.config`, and `package.sha256`
alongside the package/tests. Publication runs the same entrypoint **from the drop**,
retaining SDK selection and version validation without fetching or rebuilding.
The image must already contain the script's pinned FAKE/Farmer/configuration
dependencies and their transitive closure; see setup below. The hash file is a
consistency check, not a signature or a substitute for governed provenance.
Ancestry validation remains in the mandatory Integration stage before Azure
login (and again in the test helper); it is not repeated in the checkout-free
release preparation graph. `publish-check` also requires `INTEGRATION_SUCCEEDED=true`, mapped only
by the already-gated release job. That assertion is not an approval mechanism:
the official stage dependency and external checks remain authoritative.
Running the F# target alone does not publish or obtain credentials.

### Release guidance: requirements versus choices

The inspected 1ES `release` source was
`77ca3ebb9fe785e4a6d4ab35cf9b1c68970ad1a2` on 2026-11-04. EngineeringHub's
[NuGet guide](https://aka.ms/1espt/outputs/nuget) and overview returned HTTP 403
after redirect, but the template implementation and repository feature guidance
were readable. These findings are based on source, not an assumed exemption:

- **Enforced shape:** [ReleaseJob.yml][release-job] requires production
  classification/official templates and rejects job `outputs`.
  [ReleaseSteps.yml][release-steps] enforces no source checkout in this custom
  workflow and processes governed inputs before user steps. We use an ordinary
  Release stage containing a production deployment `releaseJob`, not the
  separate EV2-oriented `releaseStage` workflow.
- **Allowed input:** [pipelineArtifact.yml][artifact-input] explicitly supports
  `buildType: current` (also its default). The current official run's `drop`
  is therefore used; no arbitrary run selection, unofficial artifact, cache or
  direct download task is introduced. Input processing applies signed-SBOM,
  source-build and configured signature checks. [ValidateSourceBuild.yml][source-build]
  and its implementation check governed origin/artifact type; exact enforcement
  modes and organization overrides must be confirmed during ADO expansion.
- **Allowed actions:** [UserProvidedReleaseSteps.yml][release-user-steps] rejects
  build/output/direct-download/caching task patterns and direct `NuGetCommand`
  push; it explicitly recognizes **`1ES.PublishNuGet@1`**. It also permits
  ordinary custom script steps; this is not permission to hide builds or new
  dependency downloads in them. Our F# step only checks already-built inputs
  using preinstalled dependencies. The NuGet hook runs
  [Outputs/nuget.yml][nuget-output], including path validation and applicable
  SBOM handling; its [SBOM preparation][nuget-sbom] copies packages to temporary
  storage rather than repacking the input nupkg. No signing, packing, application
  restore or build is requested in Release.
- **Minimal script/task boundary:** `build.fsx` remains the only repository
  orchestration script and owns the release preflight/identity/hash graph.
  Actual publication is intentionally a native governed YAML task. Making
  `build.fsx` perform a raw `dotnet nuget push` would not establish NuGet
  compliance merely because a production job contains it. No second release
  script or dispatcher is introduced.
- **Trigger is our explicit choice, not a claimed blanket tag ban:** release is
  now an opted-in **manual master run** with Build → Integration → Release,
  independent external approvals, and the same run's tested drop. The inspected
  [branch validator][branch-validate] actually skips tag validation, and its
  [provider support][branch-support] does not cover public github.com. Requesting
  `branchValidation: enforceInReleaseJobs` is not proof of protection for this
  repository. Protected master, restricted queues and resource approval checks
  are mandatory external setup here. Tags no longer select builds/releases.
- **Credentials:** `useDotNetTask: true` and `nuGetFeedType: external` select the
  supported 1ES DotNet push implementation without a NuGet.exe installer.
  `publishFeedCredentials` identifies an administrator-selected NuGet service
  connection, which owns the destination URL and API key. The
  [Microsoft DotNet task source][dotnet-push] supports external API-key
  connections. No API key is mapped to F#, echoed by repository commands or
  stored in the repository. The native task's secret handling and service
  connection authorization still require live policy review.

The public [Management preview stages][management-preview] at
`f8642c4a1c97ca68f4f23032b7b25cee6975e864` were checked again. They publish
PowerShell dev/preview modules from a build job; they are **not** a production
`releaseJob` example or evidence that direct scripting is approved for this
NuGet release. Their tool installers, token export and dependency replication
were not copied. No other production release YAML was present in that public
tree; private Management release policy was not assumed.

No documentation deployment existed in the replaced Actions `ci`/`release`
graph; local documentation targets remain unchanged.

### One target surface, separate dependency graphs

All targets have descriptions in FAKE's listing. Introspection target bodies do
not load release notes or settings, look up secrets, authenticate, provision, publish, or
execute any inspected target bodies. The `graph` target calls FAKE's own graph
printer; it has no build dependencies.

```powershell
dotnet fsi build.fsx -- --help
dotnet fsi build.fsx --list
dotnet fsi build.fsx -t graph ci integration publish test
dotnet fsi build.fsx --write-info "$env:TEMP/asb-targets.json"
```

| Target | Hard dependency graph / behavior |
|---|---|
| `check` | Offline contract checks only; requires external scratch `CHECK_DIRECTORY` |
| `ci` | `ci-preflight` and `check` → `ci-meta` → `ci-restore` → `ci-build` → `ci-package` → `ci-stage` → `ci` |
| `check-trust` | Trusted repository/master/reason and Git ancestry; executed before AzureCLI login |
| `integration` | `check-trust` → `integration-artifact` → `integration`; no build/pack |
| `publish` | `publish-check` → `publish-artifact` → `publish`; checks readiness for the native NuGet task; **no checkout, Git, clean, restore, build, pack, live tests or push** |
| `release` | Alias depending only on `publish` |
| Local `test` (default) | Existing `clean` → `restore` → `meta` → `build` → `test`; **contacts Azure** |
| Local `package` | Existing local build graph → `package`; development output, not official release input |
| Local `init` | Explicit developer provisioning only; never a dependency of CI or introspection |

For `ci`, set `ARTIFACT_DIRECTORY`, `CHECK_DIRECTORY`, `BUILD_BUILDID`,
`PACKAGE_PATCH_OFFSET`; YAML provides these through
definition variables, built-in variables and step environment mappings.
Integration and publication select the same run's drop with `ARTIFACT_DIRECTORY`.
Privileged target bodies repeat their authorization guards because FAKE permits
`--single-target`; skipping dependency execution must not bypass those guards.
As before, local documentation needs the existing `fsdocs` tool manifest restored
using `dotnet tool restore --configfile NuGet.config` after feed access is
established. No tool restore or installer was added to pipeline targets.

### Versions and metadata

The release-note major/minor and any prerelease suffix are preserved; the patch
is `Build.BuildId + PACKAGE_PATCH_OFFSET`. This replaces `github.run_number`
while preserving the run-number version convention, authorship, release notes,
license, project/repository URLs and package tags. Git tags are not consulted.
A manually opted-in master run creates a new version once during Build;
Integration and Release use that exact version and artifact, not a new counter
or package at release time.

Before cutover, inspect the destination feed and choose a nonnegative offset
in the trusted `.pipelines/azure-pipelines.yml` if needed to ensure the next patch
is above every previously published version for that major/minor. Do not reuse
the old GitHub counter blindly. The sum must fit an unsigned 32-bit integer.
Do not allow queue-time overrides of version or security variables.

## Required administrator configuration

1. **GitHub connection and definitions.** Use the approved Azure Pipelines
   GitHub App connection for `Azure/DatatypeChannels.ASB`. Create **two** YAML
   definitions using the paths in the table, both defaulting to
   `refs/heads/master`. Retarget any existing generic root-YAML definition as
   the trusted one; do not leave a third legacy definition enabled. Keep YAML
   triggers enabled without UI overrides: PR definition has `trigger: none`;
   trusted definition has `pr: none`. Restrict edits, queue permissions and
   queue-time variable overrides. Authorize only the trusted definition for
   each protected resource, never all pipelines.
2. **1ES repository access and agent pools.** Confirm the owning ADO
   organization provides the Azure Repos project/repository
   `1ESPipelineTemplates/1ESPipelineTemplates` and the `release` tag; the name
   is relative to that organization, not a GitHub repository or an assumed
   cross-organization connection. Arrange minimum read/template authorization
   for both definitions without granting PR builds broader project access.
   Do not copy Management's pool names or add an arbitrary endpoint.

   Set the following **nonsecret definition variables**, with queue-time
   overrides disabled, to approved existing 1ES-compatible resources:

   | Definition | Build/job pool and image | Source-analysis pool and image |
   |---|---|---|
   | PR | `PR_POOL_NAME`, `PR_POOL_IMAGE` | `PR_SOURCE_ANALYSIS_POOL_NAME`, `PR_SOURCE_ANALYSIS_POOL_IMAGE` |
   | Trusted | `TRUSTED_POOL_NAME`, `TRUSTED_POOL_IMAGE` | `TRUSTED_SOURCE_ANALYSIS_POOL_NAME`, `TRUSTED_SOURCE_ANALYSIS_POOL_IMAGE` |

   All pool objects explicitly use `os: windows`, `name`, and `image`; source
   analysis is under `sdl.sourceAnalysisPool`. An image is an image identifier
   available **in that pool**, not an arbitrary `vmImage` label. Unset variables
   are deliberately unresolved resource names, not working defaults. Pool/image
   authorization and resolution must pass ADO validation before any run.
   The trusted pool serves build, integration and release; source-analysis
   pools must meet the same trust boundary as their owning definition.
   Agents need preinstalled Git, PowerShell 7,
   the SDK selected by `global.json` (**10.0.403/latestPatch**), and the .NET 10
   runtime; trusted agents also need Azure CLI. `latestPatch` does not accept a
   later feature band. Missing tools fail rather than downloading
   repository-provided installers. Review
   1ES's injected compliance tasks/tool acquisition separately with the template
   owners and approve their feeds/network access. Use clean, ephemeral agents,
   isolated between untrusted builds and trusted tests/releases. Workspace
   cleanup alone is not machine isolation. Do not give PR agents managed
   identities, persistent Azure/CLI sessions, secrets, or access to the trusted
   pool, including through automatic 1ES source-analysis jobs.
3. **Internal-only restore source and verified cold-consumption flow.** Repository
   `NuGet.config` selects only
   `https://pkgs.dev.azure.com/avs-oss/Public/_packaging/OSS-FSharp-Consumption/nuget/v3/index.json`.
   Public Management's
   [NuGet configuration][management-nuget] and [team variables][management-feed-vars]
   at commit `f8642c4a1c97ca68f4f23032b7b25cee6975e864` established the existing
   **avs-oss/Public** ownership context. PS-Consumption was tried first, but its
   [metadata][consumption-metadata] lists only PowerShell Gallery and
   AdminTools@Local upstreams; it cannot supply this .NET closure.
   Authenticated inventory found no suitable shared .NET feed.

   Following explicit authorization, the documented [Create Feed API][create-feed]
   returned **201** for **OSS-FSharp-Consumption**
   (`1c9221b0-b7ed-49b3-8520-8d0bfc5618e5`) in existing project **Public**
   (`c12681cb-56b3-4846-8768-fb04c70b5507`, visibility Public).
   [Live metadata][oss-consumption-metadata] and its HTTP-200 v3 index confirm the
   actual endpoint. Its **sole upstream is public NuGet.org**
   (`https://api.nuget.org/v3/index.json`, status `ok`), not an internal/private
   feed. Public packages are consumed through Azure Artifacts, never through a
   client-side NuGet.org fallback. PS-Consumption was not modified; no project
   visibility or explicit role assignments were changed. The new feed uses the
   existing project's ownership/visibility defaults. No built ASB package was
   uploaded to it.

   `<clear />` removes inherited package sources. Inherited audit sources are
   also cleared; NuGet then uses the package sources for audit. There is no
   public fallback, credential material or `defaultPushSource`.

   Normal `dotnet restore/build/test/pack`, local tool restore and FSI source
   discovery use this configuration. Local FAKE `restore` and `ci-restore`
   explicitly pass its path. The old arbitrary `NUGET_RESTORE_SOURCE` override
   and YAML mapping are removed. Do not add `--source`, `--add-source` or FSI
   `#i "nuget: ..."` overrides to bypass it. The SDK's on-disk FSharp
   `library-packs` source may also appear in restore metadata; it is not a
   network feed. Dependency restore does **not** select the publication
   destination: the native publishing task still uses its separate approved
   external service connection.

   **Cold restore now passes, not just cached builds.** Initial empty-cache
   solution/FSI/tool restores used the already installed Microsoft credential
   provider and existing authentication to save the current public dependency
   closure through the approved upstream. No provider installer or manual token
   export was used. Subsequent independent empty package/HTTP/resolution-cache
   restores passed with authentication plugins disabled and credential environment
   variables removed. Solution restore fetched **72 packages**; isolated FSI
   resolution fetched **53 packages**, with no resolved assembly outside its new
   cache. The existing `fsharp.formatting.commandtool` **9.0.4** manifest also
   restored anonymously into a separate empty tool/package cache.
   Unsaved package lookups returned **401 before curation**, then **200 after**:
   this is a public saved-package feed, not anonymous upstream-saving permission.

   **Trusted authentication is wired, not exercised in ADO.** The official
   Build call enables `authenticateRestore`; the shared stage defaults it to
   false for the unofficial PR call. `NuGetAuthenticate@1` runs after checkout
   and **before the first FSI invocation** in trusted Build and Integration,
   covering `#r "nuget: ..."` as well as later solution restore. This uses the
   [supported platform task][restore-auth-task], not raw token extraction or a homemade installer.
   Its default preserves an already installed credential provider; approved
   images/task acquisition policy must cover first-time provider setup.

   Set nonsecret `NUGET_RESTORE_SERVICE_CONNECTION` on the **official definition
   only** to an existing authorized external NuGet connection targeting the
   exact configured feed when the pipeline is outside `avs-oss`. For a pipeline
   in `avs-oss`, explicitly set it empty to use the task's same-organization job
   identity support. Do not leave an unresolved macro or reuse the release
   publication connection. Grant build consumers only feed read access;
   upstream package saving must be done separately by an authorized feed curator.
   Local cold consumption with existing authentication was exercised; the ADO
   task, connection/identity authorization and agent image were not. Upstream
   saving requires [Collaborator or higher][upstream-sources]; do not broaden
   PR permissions or give every consumer that role.

   **PR/fork flow stays unauthenticated.** No authentication task/restore
   connection is requested by the PR entrypoint. Resource permissions must deny
   it the trusted connection even if contributor YAML tries to enable the
   shared parameter. Never export trusted job tokens, PATs, Azure identities or
   credential-provider sessions to forks. Public saved packages can be read
   anonymously; unavailable/unsaved dependencies fail closed. Dependency changes
   need trusted curation before a cold fork build. Microsoft's current
   [public-feed lifecycle guidance][public-feeds] says existing public projects
   convert to private starting in **2027**; anonymous feed access then ends.
   Owners must provide an approved anonymous-readable consumption path before
   that transition, or leave fork builds blocked. Never solve it by sending
   credentials to forks or falling back to NuGet.org. Release does not run
   dependency authentication or restore: it still requires the preseeded closure
   and restricted egress, with publication credentials owned by the native task.

   This feed does not advertise a `VulnerabilityInfo` resource. Absence of
   advisory warnings after changing sources is not evidence that the earlier
   advisories are resolved or that vulnerability scanning is covered. Owners
   must establish approved internal audit/CG coverage; no public audit fallback
   or audit-disable switch is added.

   **F# script runtime, especially checkout-free release:** build/PR images can
   restore the curated closure from the configured feed. Release images must
   preseed the `build.fsx` NuGet references and their transitive dependencies into
   the executing account's cache from that source before enabling release jobs.
   These are the existing FAKE, Farmer, Microsoft.Extensions.Configuration and
   StructuredLogger dependencies, not new application dependencies. Their current
   resolved versions are now explicit: FAKE **6.2.0**, Farmer **1.9.36**,
   configuration JSON/binder **10.0.14**, StructuredLogger **2.2.291**.
   The former explicit logger version 2.2.243 was unavailable and caused
   NU1603; 2.2.291 was the resolver's existing fallback. Pinning the formerly
   floating references also avoids selecting different tools at release time.
   `Directory.Packages.props` and application dependencies are unchanged. The SDK
   is now pinned to **10.0.403** with the existing `latestPatch` policy.

   FSI resolves `#r "nuget: ..."` **before** target execution, including help.
   Its source discovery uses the script directory's `NuGet.config`; the exact
   config is included with `build.fsx`, `global.json` and release notes in the
   checkout-free drop. FSI's resolution cache can reuse earlier absolute package
   paths, so successful cached introspection is not proof of feed access.
   A bare SDK image without feed access or caches is not sufficient. Do not grant
   feed credentials to PR jobs or add public fallback installers/downloads to
   make resolution pass.
   Prefer prewarmed images and
   restrict release network access to approved publishing/compliance endpoints,
   with dependency acquisition disabled during release (prewarm the complete
   closure rather than enabling a restore fallback). Missing script
   dependencies fail in FSI before targets; missing CLI tools fail explicitly.
4. **Existing test namespace and identity.** Set nonsecret
   `SERVICE_BUS_NAMESPACE` to an existing public-Azure Service Bus FQDN such as
   `your-test-namespace.servicebus.windows.net`. Standard or Premium is required
   for topics/subscriptions. Provide network access to the namespace and Entra.
   Administrators must supply the namespace and assign the test identity
   `Azure Service Bus Data Owner` at namespace scope. Tests create and modify
   messaging entities, but the pipeline does not deploy namespaces, resource
   groups or role assignments. Use a disposable test namespace, never production.
5. **ADO Azure connection.** Create the Azure Resource Manager service
   connection `DatatypeChannels-ASB-Integration` using approved **Azure DevOps
   workload identity federation**, not GitHub federation or a stored client
   secret. Grant only the test permissions above plus any minimal read access
   required by the connection setup. `AzureCLI@2` signs in with isolated CLI
   configuration. The existing `DefaultAzureCredential` in tests can use that
   CLI session. No service principal key or token is exported to scripts.
   Agents must not carry other ambient Azure identities. This connection name
   is a configuration contract, not evidence that a connection exists. Authorize
   it for the trusted definition only.
6. **Trust controls outside YAML.** Protect master against unreviewed changes.
   Require maintainer approval on the integration connection and every trusted
   pool, including source analysis. Protect these before enabling the official
   definition: 1ES-injected jobs can run before repository-script trust checks.
   Use branch-control checks for master, rejecting unknown protection state.
   Validate the protected master commit in resource approvals; do not assume
   1ES's provider-specific classification check covers public GitHub.
   The script also enforces master ancestry before login.
   Configure an exclusive lock with sequential behavior for integration access,
   or otherwise serialize runs: existing tests share fixed queue names.
   Never treat a condition in contributor-editable YAML as an authorization
   boundary.
7. **Release resources.** Precreate the environment
   `DatatypeChannels-ASB-Release` and variable group with the same name.
   Set nonsecret group variable `NUGET_PUBLISH_SERVICE_CONNECTION` to an
   administrator-approved **NuGet external-feed service connection**. That
   connection must specify the intended HTTPS destination and a package-scoped,
   expiring API key in ADO secret storage. The YAML deliberately chooses no
   feed URL, connection name, identity or key. For the existing public package,
   confirm whether the destination remains NuGet.org before configuring it.
   Retire the previous proposed raw `NUGET_REPO_URL`/`NUGET_REPO_KEY` mappings;
   do not put those secrets into the script environment.
   Require independent maintainer approvals that validate protected master and
   restricted trusted-definition permissions on the environment, release
   configuration group and publishing connection. Authorize the publishing
   connection for the official definition only; do not grant all pipelines access.
   Disallow self-approval and unauthorized check bypass. Naming an environment
   in YAML does not create its approval policy. Use an environment with no VM or
   Kubernetes resource attached; it is an approval boundary, not a deployment
   machine. These names are setup contracts, not provisioned resources.
8. **PR/fork policy.** Keep "Make secrets available to builds of forks" and
   "Make fork builds have the same permissions as regular builds" disabled.
   Require a maintainer comment before validating fork contributions. Use the
   unofficial definition and its isolated build/source-analysis pools only.
   Deny the PR definition permission to queue/edit the official definition and
   to access any protected resource. Limit job token scope and repository access;
   "credential-free" means no application, Azure, signing or publishing secrets,
   not that ADO can build without its own restricted job token.
   All PRs, including same-repository PRs, stay unofficial; trusted tests run
   only on master. Do not switch to `pull_request_target`.
9. **Signing — exact missing policy and repository integration.** The WI requires
   FIC removal/ADO migration; it has no signing requirement or acceptance policy.
   The [1ES code-sign guidance][code-sign-guidance] requires signed shipped
   binaries/scripts, while the [NuGet output implementation][nuget-output] does
   not choose this library's signer or require a particular NuGet author signature.
   The trusted YAML retains breaking code-sign validation. Validation is not a
   signer: local `Get-AuthenticodeSignature` reports **NotSigned** for both
   first-party DLLs, and `dotnet nuget verify --all` reports **NU3004** for the
   locally built unsigned nupkg.

   **Owner decision required:** supply the approved code-sign validation policy
   and ESRP signing operation/profile for this OSS library, including the treatment
   of first-party test binaries and copied third-party DLLs, and state whether
   the **nupkg itself needs an author/repository signature in addition to binary
   Authenticode signatures**. These are different signing contracts, not merely
   missing resource names:

   | Surface | Required integration boundary / missing policy |
   |---|---|
   | `DatatypeChannels.ASB.dll` in the package and `tests/` | Approved binary-signing operation/key/certificate policy; sign after `ci-build`, before `ci-package`, and copy the same signed DLL to the test output. |
   | `DatatypeChannels.ASB.Tests.dll` and dependency DLLs in the governed test drop | Approved validation scope/third-party treatment (`policyFile`/targets if needed); do not blindly re-sign dependencies or invent exclusions. |
   | `DatatypeChannels.ASB.<version>.nupkg` | Explicit decision whether package signing is required and its approved operation/profile; if required, sign after packing and before `ci-stage` seals its hash. |
   | SBOM/source-build provenance | Platform onboarding and signed-SBOM validation for the production input; independent of DLL/package signatures. |

   The supported current [1ES example][code-sign-sample] uses
   **`EsrpCodeSigning@6` with WIF**, not a raw signer or installer.
   [1ES.Signing.yml][deprecated-signing] explicitly deprecates its old task in
   favor of v6. The v6 contract includes `ConnectedServiceName`,
   `AppRegistrationClientId`, `AppRegistrationTenantId`, `AuthAKVName`,
   `AuthCertName`, `AuthSignCertName`, `ServiceEndpointUrl`, `FolderPath`,
   `Pattern` and `inlineOperation` with `signConfigType: inlineSignParams`.
   The example's identities, key codes, certificate names and operations are not
   this repository's authorization; none were copied. A connection alone cannot
   select the missing policy. Once approved, repository work must expose those
   native signing boundaries through the existing FAKE targets; the current
   single `ci` invocation does **not** implement them.

   Signer-independent support is implemented now: `sbom.enabled: true`,
   **`validate: true`** and **`continueOnError: false`**. The inspected
   [SBOM sample][sbom-sample] supports validation; the
   [generation implementation][sbom-generation] propagates `continueOnError`
   to task execution, so false fails closed despite a misleading editor-schema
   description. No SBOM was generated/signed locally and ADO enforcement remains
   untested. Keep production disabled pending signing policy, implementation and
   live platform checks. Never sign, repack, rebuild or replace anything after
   Integration, disable validation, or relabel production output to bypass it.

   **Production input inventory must also fail closed.** Generation's
   `continueOnError: false` does not enforce manifest completeness at release.
   The official `extends.parameters.featureFlags` therefore explicitly sets
   **`sbomValidatorIgnoreMissing: false`**. At the inspected release revision,
   [ReleaseSteps.yml][release-steps] passes `sbomValidationStrict: false`;
   [Inputs.yml][inputs] otherwise enables an advisory post-validation step.
   The explicit false flag selects `SbomValidationFailureEventAction: fail`
   in both toolkit and legacy paths of [SBOM.Validation.Steps.yml][sbom-validation],
   and excludes that advisory step. The production pipeline-artifact input still
   sets `continueOnError: false` and `validateSignature: true`; neither is weakened.
   Omitting the flag or setting it true instead selects ignore-missing behavior.
   The [post-validation helper][post-sbom-validation] explicitly recommends false;
   its unchanged one-error handler otherwise emits a warning and succeeds.
   This inventory policy does not supply or replace the missing signing policy.

Resources and branch policies are the security boundary, since PRs can modify
scripts and YAML. Do not enable a connection or secret group until its checks
are in place. If the organization's checks cannot safely authorize protected
master on this public GitHub repository, leave release disabled.

## Cutover and release procedure

1. Review the draft PR and administrator setup independently. Keep the removed
   GitHub federation removed. `initCI` remains retired and fails with a migration
   message. `ci`, `release` and `publish` now use the graphs above; do not treat
   a standalone local release-preflight invocation as release authorization. Local `init`
   remains an explicit provisioning operation for developers, not a CI step.
2. Administrators configure the resources and checks above. Validate **both**
   definitions' ADO template expansion in that project with publication disabled.
   Check the no-release and opted-in release expansions without executing
   publication; inspect pool resolution, source-analysis jobs, signing/SBOM
   checks, current-run artifact paths and deployment-job/environment handling.
   Confirm fork PR, same-repo PR, master, manual opted-in master, rejected tag and manual feature
   cases. Schema/editor checks are not equivalent to this expansion.
   **Authorized negative SBOM inventory check:** in a controlled nonpublishing
   validation run, use a test artifact whose signed SBOM is valid but omits an
   extra file. Require production-input validation to fail the job before the
   publisher can execute (not merely emit a warning or succeed with issues).
   Confirm strict settings for the validator implementation actually selected
   by the organization, signature validation remains enabled, and a complete
   signed control artifact passes. Do not mutate a tested production drop.
   This requires separate authorization and has not been run locally or in ADO.
3. After normal PR review/merge and signing setup, run unofficial validation, then a trusted
   master integration build. Confirm a nonempty passing TRX and inspect the
   package metadata, version and artifact. Test approval rejection/cancellation
   without publishing. This repository change alone is not evidence of those
   live checks.
4. Replace the old GitHub required status check with the real Azure Pipelines
   check only after it has appeared successfully. Remove obsolete GitHub
   environment secrets and disable any old external pipeline definitions.
   The obsolete Actions workflow and README Actions badge are removed here.
   `.github/dependabot.yml` is retained unchanged from master, per review.
   Add a real ADO badge only after the
   pipeline identity is known.
5. For a separately authorized release, queue **only the trusted definition**
   on protected `refs/heads/master` with `publishPackage: true`. Build packages
   once; Integration tests that drop; Release consumes the same governed input.
   Inspect the computed version, package/hash, successful integration results,
   1ES provenance/signature checks and chosen external NuGet connection before
   approving release resources. Automatic pushes, tag runs, default manual runs,
   PRs and failed tests never
   publish. No release is requested or executed by this migration.

## Local checks and limits

With the required SDK preinstalled:

```powershell
$env:CHECK_DIRECTORY = "$env:TEMP/asb-contracts"
dotnet fsi build.fsx -t check
dotnet fsi build.fsx --list
dotnet fsi build.fsx -t graph ci integration publish
git diff --check
```

The contract checks exercise positive/negative authorization predicates,
versioning, metadata, package identity, result gates, two-template selection,
stage nesting, credential scope and current-run artifact continuity without
Azure access. They invoke artifact-hosted release preparation with no publishing
connection, with the old raw feed/key forcibly empty and CLI tools absent from
PATH. Missing-connection and other denial paths cannot publish. A fake connection
name exercises successful **preflight only**, never the native publishing task.
Tests load the **actual** build script to inspect its helpers, then
execute the exact artifact-hosted entrypoint (not a stripped copy) for help,
listing, graph export and denial paths. FAKE's machine-readable dependency
export is checked for exact credential-free, integration and no-rebuild publish
closures. Introspection is tested with release notes/application settings absent;
the dependency configuration remains present.
Package/hash/library mismatch and `--single-target` guard regressions are included.
Fixtures are removed from the supplied scratch directory. They do not execute Azure YAML
or validate configured permissions. `dotnet test` is **not** an offline check:
the existing test assembly contacts Azure during discovery. Do not run it
without explicit permission to create test messaging entities.

Earlier migration checks used the installed **10.0.403** FSI directly because
the then-required **10.0.201/latestPatch** SDK was unavailable. The requested SDK
is now **10.0.403/latestPatch** in `global.json`; the roll-forward and prerelease
policies are unchanged.

For the SDK update, `dotnet --version` from the workspace selected **10.0.403**.
Using normal SDK selection, `dotnet fsi build.fsx -t check` passed **191 checks**,
and `dotnet fsi build.fsx -t ci-build --single-target` compiled the Release
library and integration binaries without restoring or running Azure tests.
The build still reported NU1902 for OpenTelemetry.Api 1.15.2; this SDK change
does not update application dependencies. No dependency restore or tool
installation was needed at that SDK-only checkpoint.

The subsequent source-configuration/completeness pass ran the full safe
`dotnet fsi build.fsx -t ci` graph with the normally selected SDK and existing
caches: **205 contract checks**, preflight, metadata, restore, Release build,
pack and artifact staging passed. No Azure integration or native publishing
task ran. Packaging still warns about the deprecated icon URL and missing readme.
The config regression first failed because `NuGet.config` was absent. Effective
source probes now cover the repository, test-script directory, checkout-free
artifact and an inherited-source fixture, not just XML/string matching.

Separate empty-package-cache solution restore and isolated uncached FSI
dependency resolution failed with **NU1101** against PS-Consumption (plus the
SDK's local library packs), with no public fallback. The FSI resolver generated
only the PS-Consumption network source. Tool restore was not exercised at that
checkpoint. These were the PS-Consumption checkpoint's limits, superseded by the
new-feed cold-restore evidence below.
The earlier NU1902/OpenTelemetry.Api and NU1901/NuGet transitive advisory
observations remain unresolved; the SDK/source change is not a CVE audit.
After the authentication clarification, the ordering regression failed before
wiring `NuGetAuthenticate@1`; that checkpoint's contract suite had **209 checks**.
The full safe FAKE CI graph was rerun with all 209 checks and passed with existing
caches. The authentication task itself has not run locally or in a live pipeline.

The authorized shared-feed follow-up replaced PS-Consumption with the actual
new OSS-FSharp-Consumption endpoint. Empty-cache normal SDK solution restore,
isolated FSI dependency resolution and existing manifest-tool restore passed
after upstream saving using existing native-provider authentication. Each was
then repeated with independent empty caches, credential environment variables
removed and `NUGET_NETCORE_PLUGIN_PATHS` set to an empty path list; the installed
SDK's own `PluginDiscoverer` returned **0 authentication plugins**.
All three anonymous restores passed. The FSI diagnostic used the installed SDK's
dependency resolver with a fresh resolution cache, not the global FSI cache;
all 52 resolved assembly paths were in its isolated package directory.

The full normal `dotnet fsi build.fsx -t ci` graph then passed **211 checks**,
restore, Release compilation, pack and staging using those populated solution
caches with credential plugins disabled. This last build was not itself cold;
the separate cold-restore probes are the feed-access evidence. Feed-selection
and SBOM regressions failed before their respective changes. Real package SHA256
and packaged/test DLL equality checks passed; the four staged tooling/config
files exactly match the workspace. Neither live Azure tests nor publication ran.
The tool restored was the existing `fsharp.formatting.commandtool` 9.0.4;
its manifest/version was not changed and its command was not executed.

The focused SBOM review follow-up reproduced the permissive production-input
selection before the fix. Executing only the inspected helper's unchanged
error-handling functions in memory with a failed, one-error result produced
**WARNING → Succeeded**. Selected upstream expressions for both validator
implementations gave: unset/true → ignore-missing plus advisory post-step;
false → failure action plus no advisory post-step. The repository selection
failed before the new flag and passed afterward; normal
`dotnet fsi build.fsx -t check` passed **215 checks**, including unset/false/true
policy guards. These are source-expression/error-handler tests, **not an actual
DropValidator/toolkit execution or ADO template expansion**.

The inspected editor schema's `featureFlags` whitelist omits
`sbomValidatorIgnoreMissing` and rejects it as an additional property.
This is an explicit schema limitation, not a passing whole-document check:
the runtime templates accept/pass the object, consume this boolean and the
upstream helper recommends this exact setting. Keep the strict flag and
confirm current schema/template compatibility during authorized ADO expansion;
do not remove it merely to satisfy the editor.

YAML was parsed with cached YamlDotNet 16.3.0. Local schema/source
inspection is not ADO template expansion, pool authorization, signing readiness
or a successful integration/release run. Apart from the explicitly authorized
consumption-feed creation and normal upstream caching, no resource provisioning,
explicit permission changes, Azure tests, pipeline creation/queueing, signing or
library package publication were performed.

The unified script now compiles with its exact pinned build dependencies under
10.0.403. Initial floating-reference resolution failed locally; existing
dependencies were resolved through the configured approved Microsoft feed after
that failure and their observed versions were pinned. Restore reported low
severity NU1901 advisories for transitive NuGet.Packaging/NuGet.Protocol 6.12.4;
these require normal dependency/security review, not disabling pipeline scans.

Local checks against the inspected 1ES editor schema covered template names,
pool/source-analysis objects, governed output, release input/context, SDL
configuration and the selected release-stage shape. The unofficial entrypoint
also passed local editor-schema checks. Both selected shared Build shapes
(authentication enabled/disabled) are checked separately: the editor's step
schema has no compile-time conditional arm, so validation of the whole new
conditional shared template is not claimed. This schema mixes
scalar-string ADO fields with JSON-boolean 1ES/task fields, so a plain YAML-to-JSON
`Test-Json` call is not a full pipeline validator. In addition, its AzureCLI@2
input list omits the existing `visibleAzLogin` option, and its PublishTestResults@2
list omits `failTaskOnFailureToPublishResults` and `failTaskOnMissingResultsFile`.
The real YAML retains all three controls; the integration-stage shape probe
excludes them **in memory for diagnosis only**. The options are present in
[Microsoft's task source](https://github.com/microsoft/azure-pipelines-tasks/blob/bf4b17a2d2eeb6526f064deb03117943da3946bf/Tasks/AzureCLIV2/task.json).
See also the
[test-results task source](https://github.com/microsoft/azure-pipelines-tasks/blob/bf4b17a2d2eeb6526f064deb03117943da3946bf/Tasks/PublishTestResultsV2/task.json).
Whole trusted-document/schema compatibility and conditional template expansion
remain unproven; administrators must resolve them with the current template/task
versions in ADO rather than weakening checks to satisfy a stale editor schema.

## Authoritative references

- Public Management prior art at commit
  `f8642c4a1c97ca68f4f23032b7b25cee6975e864`:
  [validationBuild.yml](https://github.com/Azure/Microsoft.AVS.Management/blob/f8642c4a1c97ca68f4f23032b7b25cee6975e864/.ci/validationBuild.yml),
  [previewBuild.yml](https://github.com/Azure/Microsoft.AVS.Management/blob/f8642c4a1c97ca68f4f23032b7b25cee6975e864/.ci/previewBuild.yml),
  [shared preview stages](https://github.com/Azure/Microsoft.AVS.Management/blob/f8642c4a1c97ca68f4f23032b7b25cee6975e864/.ci/templates/common-preview-build-stages.yml).
  The existing feed ownership context is grounded in its
  [NuGet config][management-nuget] and [feed variables][management-feed-vars];
  the authorized OSS F# feed was created separately in that same public project.
  Pipeline structure is inspiration only; no Management-specific credentials,
  installers, pools or connections were copied.
- [1ES template documentation](https://aka.ms/1esptdocs) redirected to
  EngineeringHub, but direct and authenticated API reads were network-blocked
  during this update (2026-11-04).
- Read-only access to the
  [1ES template repository](https://dev.azure.com/msazure/1ESPipelineTemplates/_git/1ESPipelineTemplates)
  did work. `refs/tags/release` resolved to
  `77ca3ebb9fe785e4a6d4ab35cf9b1c68970ad1a2` during inspection and the
  2026-11-05 completeness recheck. Inspected
  `schema/1espt-base-schema.json`, both `v1/1ES.*.PipelineTemplate.yml`
  entrypoints, pool/output/signing guidance, `v1/Steps/Outputs/pipelineArtifact.yml`,
  `v1/Core/Steps/Inputs/pipelineArtifact.yml`, `v1/Jobs/ReleaseJob.yml` and
  `v1/Steps/ReleaseSteps.yml`, `v1/Steps/UserProvidedReleaseSteps.yml`,
  `v1/Steps/1ES.Signing.yml`, `Samples/CodeSignValidation.yml`,
  `Samples/SBOMPipeline.yml`, `v1/Steps/SDLBinaryAnalysis.yml` and the SBOM
  generation/validation step implementations.
  In particular, output/input declarations and the
  no-checkout release constraint were checked against implementation, not just
  Management examples. The YAML intentionally tracks the approved `release`
  channel, not that inspection commit; revalidate when the channel advances.
- [GitHub triggers and fork security](https://learn.microsoft.com/en-us/azure/devops/pipelines/repos/github)
- [AzureCLI@2 inputs](https://learn.microsoft.com/en-us/azure/devops/pipelines/tasks/reference/azure-cli-v2)
- [Approvals, branch controls and resource checks](https://learn.microsoft.com/en-us/azure/devops/pipelines/process/approvals)
- [Pipeline artifact handoff](https://learn.microsoft.com/en-us/azure/devops/pipelines/artifacts/pipeline-artifacts)
- [PublishTestResults@2 failure controls](https://learn.microsoft.com/en-us/azure/devops/pipelines/tasks/reference/publish-test-results-v2)
- [VSTest command](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-vstest)

[release-job]: https://dev.azure.com/msazure/1ESPipelineTemplates/_git/1ESPipelineTemplates?path=/v1/Jobs/ReleaseJob.yml&version=GC77ca3ebb9fe785e4a6d4ab35cf9b1c68970ad1a2
[release-steps]: https://dev.azure.com/msazure/1ESPipelineTemplates/_git/1ESPipelineTemplates?path=/v1/Steps/ReleaseSteps.yml&version=GC77ca3ebb9fe785e4a6d4ab35cf9b1c68970ad1a2
[release-user-steps]: https://dev.azure.com/msazure/1ESPipelineTemplates/_git/1ESPipelineTemplates?path=/v1/Steps/UserProvidedReleaseSteps.yml&version=GC77ca3ebb9fe785e4a6d4ab35cf9b1c68970ad1a2
[artifact-input]: https://dev.azure.com/msazure/1ESPipelineTemplates/_git/1ESPipelineTemplates?path=/v1/Core/Steps/Inputs/pipelineArtifact.yml&version=GC77ca3ebb9fe785e4a6d4ab35cf9b1c68970ad1a2
[source-build]: https://dev.azure.com/msazure/1ESPipelineTemplates/_git/1ESPipelineTemplates?path=/v1/Core/Steps/ValidateSourceBuild.yml&version=GC77ca3ebb9fe785e4a6d4ab35cf9b1c68970ad1a2
[nuget-output]: https://dev.azure.com/msazure/1ESPipelineTemplates/_git/1ESPipelineTemplates?path=/v1/Steps/Outputs/nuget.yml&version=GC77ca3ebb9fe785e4a6d4ab35cf9b1c68970ad1a2
[nuget-sbom]: https://dev.azure.com/msazure/1ESPipelineTemplates/_git/1ESPipelineTemplates?path=/scripts/sbom_nuget_generation.py&version=GC77ca3ebb9fe785e4a6d4ab35cf9b1c68970ad1a2
[branch-validate]: https://dev.azure.com/msazure/1ESPipelineTemplates/_git/1ESPipelineTemplates?path=/scripts/branch_validation/validate.py&version=GC77ca3ebb9fe785e4a6d4ab35cf9b1c68970ad1a2
[branch-support]: https://dev.azure.com/msazure/1ESPipelineTemplates/_git/1ESPipelineTemplates?path=/scripts/branch_validation/shared.py&version=GC77ca3ebb9fe785e4a6d4ab35cf9b1c68970ad1a2
[dotnet-push]: https://github.com/microsoft/azure-pipelines-tasks/blob/bf4b17a2d2eeb6526f064deb03117943da3946bf/Tasks/DotNetCoreCLIV2/pushcommand.ts
[management-preview]: https://github.com/Azure/Microsoft.AVS.Management/blob/f8642c4a1c97ca68f4f23032b7b25cee6975e864/.ci/templates/common-preview-build-stages.yml
[management-nuget]: https://github.com/Azure/Microsoft.AVS.Management/blob/f8642c4a1c97ca68f4f23032b7b25cee6975e864/nuget.config
[management-feed-vars]: https://github.com/Azure/Microsoft.AVS.Management/blob/f8642c4a1c97ca68f4f23032b7b25cee6975e864/.ci/vars.yml
[restore-auth-task]: https://github.com/microsoft/azure-pipelines-tasks/blob/3cdc037ed3702e99641e536ed7512b3f873b9d1d/Tasks/NuGetAuthenticateV1/task.json
[consumption-metadata]: https://feeds.dev.azure.com/avs-oss/Public/_apis/packaging/feeds/PS-Consumption?api-version=7.1
[oss-consumption-metadata]: https://feeds.dev.azure.com/avs-oss/Public/_apis/packaging/feeds/OSS-FSharp-Consumption?api-version=7.1
[create-feed]: https://learn.microsoft.com/en-us/rest/api/azure/devops/artifacts/feed-management/create-feed?view=azure-devops-rest-7.1
[upstream-sources]: https://learn.microsoft.com/en-us/azure/devops/artifacts/concepts/upstream-sources?view=azure-devops
[public-feeds]: https://learn.microsoft.com/en-us/azure/devops/artifacts/concepts/feeds?view=azure-devops
[code-sign-guidance]: https://dev.azure.com/msazure/1ESPipelineTemplates/_git/1ESPipelineTemplates?path=/.agents/features/sdl-code-sign-validation.md&version=GC77ca3ebb9fe785e4a6d4ab35cf9b1c68970ad1a2
[code-sign-sample]: https://dev.azure.com/msazure/1ESPipelineTemplates/_git/1ESPipelineTemplates?path=/Samples/CodeSignValidation.yml&version=GC77ca3ebb9fe785e4a6d4ab35cf9b1c68970ad1a2
[deprecated-signing]: https://dev.azure.com/msazure/1ESPipelineTemplates/_git/1ESPipelineTemplates?path=/v1/Steps/1ES.Signing.yml&version=GC77ca3ebb9fe785e4a6d4ab35cf9b1c68970ad1a2
[sbom-sample]: https://dev.azure.com/msazure/1ESPipelineTemplates/_git/1ESPipelineTemplates?path=/Samples/SBOMPipeline.yml&version=GC77ca3ebb9fe785e4a6d4ab35cf9b1c68970ad1a2
[sbom-generation]: https://dev.azure.com/msazure/1ESPipelineTemplates/_git/1ESPipelineTemplates?path=/v1/Core/Steps/SBOM.Generation.Steps.yml&version=GC77ca3ebb9fe785e4a6d4ab35cf9b1c68970ad1a2
[inputs]: https://dev.azure.com/msazure/1ESPipelineTemplates/_git/1ESPipelineTemplates?path=/v1/Steps/Inputs.yml&version=GC77ca3ebb9fe785e4a6d4ab35cf9b1c68970ad1a2
[sbom-validation]: https://dev.azure.com/msazure/1ESPipelineTemplates/_git/1ESPipelineTemplates?path=/v1/Core/Steps/SBOM.Validation.Steps.yml&version=GC77ca3ebb9fe785e4a6d4ab35cf9b1c68970ad1a2
[post-sbom-validation]: https://dev.azure.com/msazure/1ESPipelineTemplates/_git/1ESPipelineTemplates?path=/scripts/post_sbom_validation.py&version=GC77ca3ebb9fe785e4a6d4ab35cf9b1c68970ad1a2
