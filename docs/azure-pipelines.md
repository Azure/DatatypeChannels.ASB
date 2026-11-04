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
| PR validation | `/azure-pipelines-pr.yml` | `v1/1ES.Unofficial.PipelineTemplate.yml@1esPipelines` | Untrusted, isolated pools; no Azure/release/signing resources |
| Trusted integration and package production | `/azure-pipelines.yml` | `v1/1ES.Official.PipelineTemplate.yml@1esPipelines` | Protected refs, restricted queues and approved resources |

Both use the Azure Repos resource alias `1esPipelines`, repository
`1ESPipelineTemplates/1ESPipelineTemplates`, and `refs/tags/release`. Their stages
are under `extends.parameters.stages`. Only the credential-free build stage in
`eng/pipelines/build.yml` is shared. Do not authorize the PR definition for the
trusted pool, integration connection, release group/environment, or signing.
The unofficial definition never supplies an artifact to the official one.
There is no pipeline-completion release trigger.

## Behavior (after administrator prerequisites)

| Invocation | Build/package | Service Bus tests | NuGet publication |
|---|---|---|---|
| PR targeting master (unofficial definition) | Yes, no application credentials | No | Never |
| Draft PR | Not automatically | No | Never |
| Master push | Yes | Yes | Never |
| Tag push | Yes | Only if commit is already on master | Never |
| Manual master run | Yes | Yes | Never |
| Manual tag run, default parameters | Yes | Only if commit is already on master | Never |
| Manual tag run, `publishPackage: true` | Yes | Required | Only after release approval |
| Manual unofficial run | Yes, candidate only | No | Never |
| Manual feature branch run in official definition | Custom stages skipped; do not authorize/queue | No | Never |

`eng/RunPipeline.fsx` uses only the SDK and .NET libraries, with no FAKE or tool
restore. It restores the existing solution dependencies, generates package
metadata, publishes Release build output, and packs only the library. It retains
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
configured. The release stage consumes the same run's artifact. It validates the package
filename and nuspec ID/version, then pushes that package without rebuilding or
restoring. No duplicate-skip option hides publication errors. Repeating a
successful publish will fail on an immutable feed, so inspect the existing
package before retrying. Failed or skipped integration cannot authorize release.
1ES release jobs cannot check out source. The build therefore includes only
`eng/Pipeline.fsx`, `eng/RunPipeline.fsx`, `RELEASE_NOTES.md`, and `global.json`
alongside the package/tests. Publication runs these tools **from the drop**,
retaining SDK selection and version validation without fetching or rebuilding.
Ancestry validation remains in the mandatory Integration stage before Azure
login (and again in the test helper); it is not repeated in the checkout-free
publisher. Do not run that publisher independently as a release workflow.

No documentation deployment existed in the replaced Actions `ci`/`release`
graph; local documentation targets remain unchanged.

### Versions and metadata

The release-note major/minor and any prerelease suffix are preserved; the patch
is `Build.BuildId + PACKAGE_PATCH_OFFSET`. This replaces `github.run_number`
while preserving the run-number version convention, authorship, release notes,
license, project/repository URLs and package tags. Tags select a commit, not the
package version. A manual release is a new run with a new version.

Before cutover, inspect the destination feed and choose a nonnegative offset
in the trusted `azure-pipelines.yml` if needed to ensure the next patch is above every
previously published version for that major/minor. Do not reuse the old GitHub
counter blindly. The sum must fit an unsigned 32-bit integer. Do not allow
queue-time overrides of version or security variables.

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
   the SDK selected by `global.json`, and the .NET 10 runtime; trusted agents also
   need Azure CLI. `latestPatch` does not accept a later feature band. Missing
   tools fail rather than downloading repository-provided installers. Review
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
6. **Trust controls outside YAML.** Protect master and **all tags** against
   unreviewed changes and restrict tag creation/deletion to maintainers.
   Require maintainer approval on the integration connection and every trusted
   pool, including source analysis. Protect these before enabling the official
   definition: 1ES-injected jobs can run before repository-script trust checks.
   Use branch-control checks for master, rejecting unknown protection state.
   For tag runs, verify the protected tag and its commit against master in the
   resource approval; do not assume ADO branch-control checks establish GitHub
   tag protection. The script also enforces ancestry before login.
   Configure an exclusive lock with sequential behavior for integration access,
   or otherwise serialize runs: existing tests share fixed queue names.
   Never treat a condition in contributor-editable YAML as an authorization
   boundary.
7. **Release resources.** Precreate the environment
   `DatatypeChannels-ASB-Release` and variable group with the same name.
   Set group variable `NUGET_REPO_URL` to the intended HTTPS feed (for the
   existing public package, `https://api.nuget.org/v3/index.json`) and secret
   `NUGET_REPO_KEY` to a package-scoped, expiring API key. The key must exist
   only in ADO secret storage and is mapped only in the release step. Require
   independent maintainer approvals that validate the protected tag and restricted
   trusted-definition permissions on **both** the environment and secret variable group.
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
   All PRs, including same-repository PRs, stay unofficial; trusted tests run after merge or on a
   protected tag. Do not switch to `pull_request_target`.
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
are in place. If the organization's checks cannot safely authorize tag refs,
leave release disabled and resolve that policy with administrators.

## Cutover and release procedure

1. Review the draft PR and administrator setup independently. Keep the removed
   GitHub federation removed. Do not run the legacy `initCI`/`ci`/`release`/
   `publish` targets; they now fail with a migration message. Local `init`
   remains an explicit provisioning operation for developers, not a CI step.
2. Administrators configure the resources and checks above. Validate **both**
   definitions' ADO template expansion in that project with publication disabled.
   Check the no-release and opted-in release expansions without executing
   publication; inspect pool resolution, source-analysis jobs, signing/SBOM
   checks, current-run artifact paths and deployment-job/environment handling.
   Confirm fork PR, same-repo PR, master, tag, manual feature and manual tag
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
5. For a separately authorized release, queue **only the trusted definition** on the full
   protected tag ref with `publishPackage: true`. The tag must contain this
   migration and point to a commit already on master. Inspect the computed version,
   package and successful integration results before approving the release
   resources. Automatic pushes, default manual runs, PRs and failed tests never
   publish. No release is requested or executed by this migration.

## Local checks and limits

With the required SDK preinstalled:

```powershell
dotnet fsi tests/Pipeline.Tests.fsx $env:TEMP
dotnet build --no-restore -c Release
git diff --check
```

The contract checks exercise positive/negative authorization predicates,
versioning, metadata, package identity, result gates, two-template selection,
stage nesting, credential scope and current-run artifact continuity without
Azure access. They also invoke an artifact-hosted publisher with both feed and
key forcibly empty to ensure it fails configuration validation without Git or a
package push.
Fixtures are removed from the supplied scratch directory. They do not execute Azure YAML
or validate configured permissions. `dotnet test` is **not** an offline check:
the existing test assembly contacts Azure during discovery. Do not run it
without explicit permission to create test messaging entities.

The follow-up was checked locally using the installed **10.0.403** SDK/F# FSI.
The required **10.0.201/latestPatch** SDK was unavailable and `global.json` was
not weakened. YAML was parsed with cached YamlDotNet 16.3.0. Local schema/source
inspection is not ADO template expansion, pool authorization, signing readiness
or a successful integration/release run. No Azure tests, pipeline creation or
queueing, permissions changes, provisioning, signing or package publication
were performed for this update.

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
