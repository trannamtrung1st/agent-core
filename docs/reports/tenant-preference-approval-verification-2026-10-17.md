# Durable tenant preference authorization verification

Date: 2026-10-17. Baseline: `f8275bbddb0a70bae9144981a2f15a7803ad0f1e`. The [direct preference checkpoint](tenant-direct-preferences-verification-2026-10-17.md) established Allowed execution. This successor implements reviewed preference requests and exact original-Run consumption. The [full delivery goal](multitenant-iam-delivery-2026-10-16.md) remains active; no milestone acceptance, deployment or installed-data reset is claimed.

## Implemented boundary

The existing `approvals.request_authorization` tool accepts either exact persona or bounded conversation-preference intent. `PreparedTenantOperation` carries exactly one typed payload and shared target/Instance/persona witnesses, canonical operation name and hash. Preference parameters are only response length, language and one-question set/clear values; there is no arbitrary effect payload, caller-selected identity or permission.

The existing request, human decision, exact grant, receipt, worker and Runtime continuation machinery handles both kinds. No parallel approval store, scheduler or Task is introduced. `TenantLocalEffectCommit`, `TenantLocalEffectRules` and `ITenantApprovedOperationStore.TryCommitLocalEffectAsync` replace persona-specific internal names. The service executes only saved parameters; pending model intent must match both kind and normalized fields before any effect. The API projects the corresponding operation and readable summary with no bearer authority.

Management review remains an independent floor: Assisted/non-auto-apply preferences and persona require a qualified Review even when the executor and original requester already have the ordinary action permission. Missing eligible delegatable permission remains an ExceptionalGrant, never a general role or Run permission. An already Allowed, auto-apply preference preparation creates neither a redundant request nor an effect; the direct tool remains its execution path.

Both stores atomically apply the saved local change, consume/revoke the exact grant, save the Run marker and immutable receipt, advance security revision and append audit. Preference effects preserve persona and unrelated overrides. SQLite updates only the affected configuration fields. Execution revalidates current IAM/review, grantor/requester, lease, target witnesses, management and effective model/provider configuration. Freeze after approval yields a stable denial and revokes the unconsumed grant. Freeze before the human decision invalidates the prepared witness and returns conflict without granting authority.

The schema is unchanged: both closed operations share agents.update/agent ownership and existing exact grant/receipt foreign keys. Existing saved persona JSON without the optional preference field retains its canonical identity. Mapping rejects mixed or corrupted payloads. This preserves valid durable requests within the current canonical schema and adds no legacy authority fallback.

## Executed local evidence

All 234 focused tests passed: 150 Infrastructure, 54 Application and 30 API. Offline Synthetic providers and disposable fixtures only; no live provider calls, credentials, user data, existing application processes or hosted CI.

| Check | Command/filter | Result |
| --- | --- | --- |
| Shared direct/approved effects, ownership and recovery | `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~TenantPreferenceOperationTests\|FullyQualifiedName~TenantOperationAuthorizationTests'` | 150 passed |
| Model schema, offering and registry | `dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~TenantApprovalToolContractTests\|FullyQualifiedName~CapabilityProjectionTests\|FullyQualifiedName~ToolRegistryPolicyTests'` | 54 passed |
| Hosted tenant Session, direct and approval journeys | `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --nologo --filter FullyQualifiedName~TenantSessionAccessTests` | 30 passed |

Representative journey in both InMemory and SQLite: authenticate → create owned Instance with Assisted Settings → submit authenticated text → real worker/Runtime offers authorization tool → model requests exact preference values → Core persists request and releases the claim → qualified human approves → worker freshly claims the original Run → exact saved effect commits once → pending tool checkpoint closes → model receives saved outcome and Runtime completes. Observed: expected preference values/revisions, unchanged persona, one effect receipt/audit, no broad grant; Review needs no exceptional grant, and ExceptionalGrant is consumed once.

Hosted boundaries cover Review and ExceptionalGrant approve/reject, cancellation, and stale decisions after freeze. SQLite host recreation covers both persona and preference requests pending before the decision, followed by fresh authentication, approval and completion without reasking. The focused new hosted set passed 14 scenarios before the complete affected class regression.

Store integration verifies exact application and reopened replay for both approval kinds, no RoleAssignment expansion, post-approval freeze denial/revocation, pending parameter/kind mismatches before effect, redundant Allowed preparation, original persona payload identity and mixed-payload rejection. Existing direct preference, persona approval, current authority, cancellation, ownership and receipt schema checks remain part of the final focused regression. The preference-specific store class passed 58 scenarios before the combined regression.

Evidence logs: `/private/tmp/tenant-preference-approval-api-final.log`, `/private/tmp/tenant-preference-approval-api-regressions.log`, `/private/tmp/tenant-preference-approval-infra-new.log`, `/private/tmp/tenant-preference-approval-infrastructure-final.log` and `/private/tmp/tenant-preference-approval-application.log`.

## Remaining boundaries

Only the two closed local operation kinds are supported here. Other resource/effect families, Run-B consumption, complete authorization-decision audit and proactive revocation/storm controls, runtime credential use, full tenant Chat/output/audio and approval UI, and final obsolete-authority/schema removal remain required. Browser behavior was not changed or requalified. CI and live-provider acceptance were not requested or run.
