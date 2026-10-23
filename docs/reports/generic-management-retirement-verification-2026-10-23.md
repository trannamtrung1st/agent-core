# Generic management executor retirement — 2026-10-23

This follows [standalone Harness retirement](harness-authority-retirement-verification-2026-10-23.md). It removes the remaining unowned configuration/remedy and Skill execution branches while preserving canonical tenant command and protected transaction owners. The [original delivery goal](multitenant-iam-delivery-2026-10-16.md) remains active.

## Completed slice

- Removed `SessionToolExecutor.Configuration.cs`, `SessionToolExecutor.ConfigurationDiagnosis.cs` and the executable partial class in `InstanceSkillTools.cs`.
- Removed generic management auto-apply decisions, standalone policy reload and configuration/Skill dispatch. Unowned offering and policy/execution deny these operations even with caller-built Managed delegation and all auto-apply choices.
- Removed five direct `SessionToolExecutor` dependencies and their DI arguments: `SelfManagementService`, `AgentInstanceSettingsService`, `AgentInstanceResourceService`, `AgentInstanceSkillService` and `AdminAgentInstanceService`.
- Retained canonical descriptors, typed schema/input helpers, delegated eligibility helpers and shared semantic planners. The actual tenant dispatcher resolves current Run ownership/claim and invokes its existing management read or mutation owner before the generic refusal. No new executor, fallback, approval authority, permission or schema is introduced.
- Converted standalone management success fixtures into actual refusal/privacy checks. The typed settings schema still rejects non-text preferences and duplicate fields. Actual stored Instances with Managed/all-auto delegation cannot change settings/resources through the generic executor; failure preserves revision and stored state. Unowned inspection exposes no provider/credential diagnosis, selection blockers, private Skills/resources or remedy proposals.

## Focused verification

All tests use Synthetic/disposable fixtures. Backend invocations use `dotnet test <project> --no-build --no-restore --nologo --filter '<selection>'`.

| Check | Result |
| --- | --- |
| Serial solution build, `--no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false` | Pass: 0 warnings/errors, 59.41 s. |
| Application registry/policy, management projection, capability projection/discovery | 94 pass, including actual denial for all 26 configuration/child management names plus the six Harness names retained from the preceding slice. |
| API standalone refusal/schema/quiet-context checks plus actual canonical fresh-task continuation | 21 pass. Both-store saved Instance tests preserve original settings, resources and revisions despite all-auto Managed delegation. Canonical fresh-task continuation still finishes with new exact write consent. |
| API canonical configuration commands, inspected remedies and existing credential bindings | 48 pass: both stores retain current authority, exact consent, stale/forged refusal, primary-only grants, independently qualified bindings and factual receipts. |
| API canonical Skill/Resource authoring and child reads | 118 pass: both-store direct/Assisted changes, exact approvals, independent raw reads, scoped grants, source retention, no-op/stale/forged refusal and original Run pins. |

All 281 selected backend cases pass.

Exact filters:

```text
Application: FullyQualifiedName~ToolRegistryPolicyTests|FullyQualifiedName~ManagementProjectionTests|FullyQualifiedName~CapabilityProjectionTests|FullyQualifiedName~CapabilityDiscoveryRuntimeTests
Boundary: FullyQualifiedName~SelfManagementOperationTests|FullyQualifiedName~SelfManagementRemediationTests|FullyQualifiedName~SelfManagementCompletionTests|FullyQualifiedName~SelfManagementContinuationTests|FullyQualifiedName~Canonical_configuration_fresh_task_retains_boundary
Configuration: FullyQualifiedName~Canonical_management_commands_reuse_exact_consent|FullyQualifiedName~Canonical_configuration_remedies_use_exact|FullyQualifiedName~Canonical_existing_credential_bindings_use_independent
Children: FullyQualifiedName~Canonical_Skill_authoring_uses_actual_child|FullyQualifiedName~Canonical_Resource_authoring_records_exact|FullyQualifiedName~Canonical_management_child_reads_use_exact
```

Logs are `/private/tmp/generic-management-retirement-{build,application,boundary,canonical-configuration,canonical-children}-20261023.log` (separate files). Source review confirms the deleted handlers/auto-apply methods have no remaining call site and no direct constructor/factory injection remains for those five services. Canonical service registrations and shared planners remain.

## Limits and remaining work

This backend-only slice changes no UI or wire contract. Its integrated checks exercise actual tenant operations and their stored effects; no new browser/UI acceptance is claimed. The preceding slice's actual Synthetic browser/MCP evidence remains separately scoped. No CI, live provider call, push, user-data reset or deployment was performed.

Historical Harness preparation serialization still requires a coordinated hash/consent/schema cutover. Other global/profile service and store assumptions, obsolete occurrence fixtures (the four failures disclosed in the preceding report), the final tool-transport inventory and proposal-wide security/isolation/functional/UI acceptance remain open. Passing this focused slice does not establish original proposal acceptance.
