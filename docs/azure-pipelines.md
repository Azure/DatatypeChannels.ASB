# Azure Pipelines build and release

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

## Behavior

| Invocation | Build/package | Service Bus tests | NuGet publication |
|---|---|---|---|
| PR targeting master | Yes, no Azure credentials | No | Never |
| Draft PR | Not automatically | No | Never |
| Master push | Yes | Yes | Never |
| Tag push | Yes | Only if commit is already on master | Never |
| Manual master run | Yes | Yes | Never |
| Manual tag run, default parameters | Yes | Only if commit is already on master | Never |
| Manual tag run, `publishPackage: true` | Yes | Required | Only after release approval |
| Manual feature branch run | Yes | No | Never |

`eng/RunPipeline.fsx` uses only the SDK and .NET libraries, with no FAKE or tool
restore. It restores the existing solution dependencies, generates package
metadata, publishes Release build output, and packs only the library. It retains
both `nuget/` and compiled `tests/` in the same run's `drop` artifact. The
integration stage tests those binaries with VSTest and uploads TRX results.
Missing results, zero tests, failed tests, or skipped tests fail integration.
PR packages are **untested candidates**, not releasable outputs.

The release stage downloads the same run's artifact. It validates the package
filename and nuspec ID/version, then pushes that package without rebuilding or
restoring. No duplicate-skip option hides publication errors. Repeating a
successful publish will fail on an immutable feed, so inspect the existing
package before retrying. Failed or skipped integration cannot authorize release.
No documentation deployment existed in the replaced Actions `ci`/`release`
graph; local documentation targets remain unchanged.

### Versions and metadata

The release-note major/minor and any prerelease suffix are preserved; the patch
is `Build.BuildId + PACKAGE_PATCH_OFFSET`. This replaces `github.run_number`
while preserving the run-number version convention, authorship, release notes,
license, project/repository URLs and package tags. Tags select a commit, not the
package version. A manual release is a new run with a new version.

Before cutover, inspect the destination feed and choose a nonnegative offset
in `azure-pipelines.yml` if needed to ensure the next patch is above every
previously published version for that major/minor. Do not reuse the old GitHub
counter blindly. The sum must fit an unsigned 32-bit integer. Do not allow
queue-time overrides of version or security variables.

## Required administrator configuration

1. **GitHub connection and pipeline.** Use the approved Azure Pipelines GitHub
   App connection for `Azure/DatatypeChannels.ASB`. Create a YAML pipeline using
   `/azure-pipelines.yml`, with default branch `refs/heads/master`. Keep YAML
   triggers enabled without UI overrides. Authorize only this pipeline for each
   protected resource, never all pipelines. Restrict pipeline edit and manual
   queue permissions to maintainers. Do not configure pipeline-completion
   triggers to publish.
2. **Agent pools.** Provide `DatatypeChannels-ASB-Build` and
   `DatatypeChannels-ASB-Trusted`. Agents need preinstalled Git, PowerShell 7,
   the SDK selected by `global.json`, and the .NET 10 runtime; trusted agents also
   need Azure CLI. `latestPatch` does not accept a later feature band. Missing
   tools fail rather than downloading installers. Use clean, ephemeral agents,
   isolated between untrusted builds and trusted tests/releases. Workspace
   cleanup alone is not machine isolation. Do not give PR agents managed
   identities, persistent Azure/CLI sessions, secrets, or access to the trusted
   pool.
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
   Agents must not carry other ambient Azure identities.
6. **Trust controls outside YAML.** Protect master and **all tags** against
   unreviewed changes and restrict tag creation/deletion to maintainers.
   Require maintainer approval on the integration connection and trusted pool.
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
   pipeline permissions on **both** the environment and secret variable group.
   Disallow self-approval and unauthorized check bypass. Naming an environment
   in YAML does not create its approval policy.
8. **PR/fork policy.** Keep "Make secrets available to builds of forks" and
   "Make fork builds have the same permissions as regular builds" disabled.
   Require a maintainer comment before validating fork contributions. Use the
   credential-free pool only. The YAML deliberately skips integration for all
   PRs, including same-repository PRs; trusted tests run after merge or on a
   protected tag. Do not switch to `pull_request_target`.

Resources and branch policies are the security boundary, since PRs can modify
scripts and YAML. Do not enable a connection or secret group until its checks
are in place. If the organization's checks cannot safely authorize tag refs,
leave release disabled and resolve that policy with administrators.

## Cutover and release procedure

1. Review the draft PR and administrator setup independently. Keep the removed
   GitHub federation removed. Do not run the legacy `initCI`/`ci`/`release`/
   `publish` targets; they now fail with a migration message. Local `init`
   remains an explicit provisioning operation for developers, not a CI step.
2. Administrators configure the resources and checks above. Validate ADO YAML
   expansion in that project with publication disabled, including fork PR,
   master, tag and manual-run conditions.
3. After normal PR review/merge, run credential-free validation, then a trusted
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
5. For a separately authorized release, queue the pipeline on the full
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
versioning, metadata, package identity and result gates without Azure access.
Fixtures are removed from the supplied scratch directory. They do not execute Azure YAML
or validate configured permissions. `dotnet test` is **not** an offline check:
the existing test assembly contacts Azure during discovery. Do not run it
without explicit permission to create test messaging entities.

## Authoritative references

- [GitHub triggers and fork security](https://learn.microsoft.com/en-us/azure/devops/pipelines/repos/github)
- [AzureCLI@2 inputs](https://learn.microsoft.com/en-us/azure/devops/pipelines/tasks/reference/azure-cli-v2)
- [Approvals, branch controls and resource checks](https://learn.microsoft.com/en-us/azure/devops/pipelines/process/approvals)
- [Pipeline artifact handoff](https://learn.microsoft.com/en-us/azure/devops/pipelines/artifacts/pipeline-artifacts)
- [PublishTestResults@2 failure controls](https://learn.microsoft.com/en-us/azure/devops/pipelines/tasks/reference/publish-test-results-v2)
- [VSTest command](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-vstest)
