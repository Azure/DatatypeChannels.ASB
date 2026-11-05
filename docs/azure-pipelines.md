# 1ES Azure Pipelines build and release

Migration tracking: [WI 38505857](https://dev.azure.com/msazure/One/_workitems/edit/38505857).
The WI requires removing GitHub federation to the Corp tenant and migrating to
Azure Pipelines. Eugene's June 19, 2026 comment records that the federated
credentials were removed. **Do not recreate the GitHub federated credentials**,
restore `AZURE_CREDENTIALS`, or add a client secret as a workaround.

This repository supplies YAML and scripts, not a configured live pipeline.
No ADO resources, permissions, identities, secrets, or Azure resources are
created by this change. Administrators must complete the setup below before
enabling trusted runs. The WI is in `msazure/One`; the owning team must confirm
the approved pipeline hosting project and policies rather than assuming that a
work-item project authorizes public-repository builds.

## Two definitions, not two release paths

| ADO definition | YAML path | Governed template | Authorization |
|---|---|---|---|
| PR validation | `/.pipelines/azure-pipelines-pr.yml` | `v1/1ES.Unofficial.PipelineTemplate.yml@1esPipelines` | Untrusted, isolated pools; no Azure/release/signing resources |
| Trusted integration and package production | `/.pipelines/azure-pipelines.yml` | `v1/1ES.Official.PipelineTemplate.yml@1esPipelines` | Protected refs, restricted queues and approved resources |

Both use the Azure Repos resource alias `1esPipelines`, repository
`1ESPipelineTemplates/1ESPipelineTemplates`, and `refs/tags/release`. Their stages
are under `extends.parameters.stages`. Only the credential-free build stage in
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
`build.fsx`, `RELEASE_NOTES.md`, `global.json`, and `package.sha256`
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

All targets have descriptions in FAKE's listing. Introspection does not load
release notes or settings, look up secrets, authenticate, provision, publish, or
execute any inspected target bodies. The `graph` target calls FAKE's own graph
printer; it has no build dependencies.

```powershell
dotnet fsi build.fsx --help
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
`PACKAGE_PATCH_OFFSET` and `NUGET_RESTORE_SOURCE`; YAML provides these through
definition variables, built-in variables and step environment mappings.
Integration and publication select the same run's drop with `ARTIFACT_DIRECTORY`.
Privileged target bodies repeat their authorization guards because FAKE permits
`--single-target`; skipping dependency execution must not bypass those guards.
As before, local documentation needs the existing `fsdocs` tool manifest restored
from an approved source; no installer was added to pipeline targets.

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
3. **Restore source.** Set the nonsecret pipeline variable
   `NUGET_RESTORE_SOURCE` to the approved HTTPS NuGet v3 feed that supplies the
   existing versions in `Directory.Packages.props` and implicit SDK packages.
   The build passes that source explicitly; it must work without exposing
   private feed credentials to PR jobs. Configure approved network access.
   No package sources, feeds or tool installers are provisioned by this YAML.

   **F# script runtime on every image, including checkout-free release:** provision
   the `build.fsx` NuGet references and their transitive dependencies into the
   executing account's cache from an approved source before enabling jobs.
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
   `NUGET_RESTORE_SOURCE` controls solution restore, not FSI's resolver; configure
   the image's approved NuGet sources/cache separately. A bare SDK image is not
   sufficient. Do not grant feed credentials to PR jobs or add public fallback
   installers/downloads to make resolution pass. Prefer prewarmed images and
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
9. **Signing and governed outputs — unresolved prerequisite.** Extending the
   official template is **not** a signing implementation. The trusted YAML
   explicitly enables breaking code-sign validation; it does not supply an ESRP
   connection, certificate, tenant/client ID, signing operation or exemption.
   Expect unsigned outputs to fail applicable policy. Keep trusted publication
   disabled until the owning team establishes the required binary/package
   signing policy and the approved 1ES signing integration. Do not disable
   signing checks, reclassify production output as non-production, or bypass SDL
   to make a run green.

   Any required signing must finish **before** the immutable `drop` is published.
   If assembly signing changes bytes, both the packaged library and compiled
   tests must consume that same signed library; sign the package before the
   artifact handoff too. This may require a separately reviewed build/signing
   change, not merely supplying a service-connection name. Establish signed SBOM
   generation/validation and official source-build provenance for the governed
   release input as required by the approved template version. Do not sign,
   repack, rebuild, or replace a package after Integration has tested its drop.

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
3. After normal PR review/merge and signing setup, run unofficial validation, then a trusted
   master integration build. Confirm a nonempty passing TRX and inspect the
   package metadata, version and artifact. Test approval rejection/cancellation
   without publishing. This repository change alone is not evidence of those
   live checks.
4. Replace the old GitHub required status check with the real Azure Pipelines
   check only after it has appeared successfully. Remove obsolete GitHub
   environment secrets and disable any old external pipeline definitions.
   The obsolete Actions workflow, Actions-only Dependabot configuration and
   README Actions badge are removed here. Add a real ADO badge only after the
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
closures. Introspection is tested with release notes/configuration absent.
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
installation was needed. These checks do not prove the full configured CI graph.

YAML was parsed with cached YamlDotNet 16.3.0. Local schema/source
inspection is not ADO template expansion, pool authorization, signing readiness
or a successful integration/release run. No Azure tests, pipeline creation or
queueing, permissions changes, provisioning, signing or package publication
were performed for this update.

The unified script now compiles with its exact pinned build dependencies under
10.0.403. Initial floating-reference resolution failed locally; existing
dependencies were resolved through the configured approved Microsoft feed after
that failure and their observed versions were pinned. Restore reported low
severity NU1901 advisories for transitive NuGet.Packaging/NuGet.Protocol 6.12.4;
these require normal dependency/security review, not disabling pipeline scans.

Local checks against the inspected 1ES editor schema covered template names,
pool/source-analysis objects, governed output, release input/context, SDL
configuration and the selected release-stage shape. The unofficial entrypoint
and shared stage also passed local editor-schema checks. This schema mixes
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
  Structural inspiration only; no Management-specific checks, credentials,
  installers, pools or connections were copied.
- [1ES template documentation](https://aka.ms/1esptdocs) redirected to
  EngineeringHub, but direct and authenticated API reads were network-blocked
  during this update (2026-11-04).
- Read-only access to the
  [1ES template repository](https://dev.azure.com/msazure/1ESPipelineTemplates/_git/1ESPipelineTemplates)
  did work. `refs/tags/release` resolved to
  `77ca3ebb9fe785e4a6d4ab35cf9b1c68970ad1a2` during inspection. Inspected
  `schema/1espt-base-schema.json`, both `v1/1ES.*.PipelineTemplate.yml`
  entrypoints, pool/output/signing guidance, `v1/Steps/Outputs/pipelineArtifact.yml`,
  `v1/Core/Steps/Inputs/pipelineArtifact.yml`, `v1/Jobs/ReleaseJob.yml` and
  `v1/Steps/ReleaseSteps.yml`. In particular, output/input declarations and the
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
