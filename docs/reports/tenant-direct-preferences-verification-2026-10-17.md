# Direct Allowed tenant preference verification

Date: 2026-10-17. Baseline: `4d64558c0ee2d49c98271b66165fcd11be01f072`. This follows the [model approval checkpoint](tenant-model-approval-verification-2026-10-17.md). The [full delivery goal](multitenant-iam-delivery-2026-10-16.md) remains active; no milestone acceptance, deployment or installed-data migration is claimed.

## Implemented boundary

`settings.update_preferences` is a context-only typed tool for the actual tenant executor's own response length, language and one-question preference. Its strict set/clear schema rejects authority fields, unrelated settings, duplicate/overlapping fields and invalid values. Core derives target, current Run generation/revisions and operation identity.

New effects require the executor's own `agents.read` and `agents.update`, an Allowed current requester-aware IAM/review decision, and both pinned/current Managed Settings with ConversationPreferences auto-apply selected. Assisted, frozen, unselected or review-required operations do not apply a change or create an approval/grant. Persona retains its separate reviewed path. Approval support for preference changes remains future work.

The shared tenant gate and normal Runtime loop execute this path without a Profile or legacy configuration fallback. The review found legacy discovery filtering the context-only settings descriptor; projection now retains eligible tenant authority tools in both projection modes. Normal Runtime admissions now include the server-held claim generation.

Both stores atomically save settings, the succeeded Run marker, a direct receipt, security revision and audit. The existing receipt table distinguishes approved and direct sources without weakening approved-operation foreign keys. Exact per-tenant/Run/call replay returns the saved outcome, even after SQLite reopen; changed arguments conflict. Clear removes only the named override and resolves the immutable Definition default. Global Account changes, current revisions, live lease and cancellation are fenced at commit. Tenant writes emit no legacy Profile configuration event.

Migration review found a generated zero-GUID default on downgrade. The source migration now refuses an unrepresentable downgrade with direct receipts; an empty-source downgrade/upgrade round trip succeeds. Only disposable fixture databases were migrated.

## Executed local evidence

The final focused checks passed 205 tests: 135 Infrastructure, 53 Application and 17 API. All execution used offline Synthetic providers and disposable data. No provider credentials, user database, existing application process or hosted CI was used.

| Check | Command/filter | Result |
| --- | --- | --- |
| Direct effects plus existing operation authorization | `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~TenantPreferenceOperationTests\|FullyQualifiedName~TenantOperationAuthorizationTests'` | 135 passed |
| Context offering and existing registry/projection | `dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~TenantApprovalToolContractTests\|FullyQualifiedName~CapabilityProjectionTests\|FullyQualifiedName~ToolRegistryPolicyTests'` | 53 passed |
| Hosted tenant Session, approvals and direct tools | `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --nologo --filter FullyQualifiedName~TenantSessionAccessTests` | 17 passed |

Representative hosted journey, in both InMemory and SQLite: authenticate → create an owned Instance → select Managed Settings and ConversationPreferences auto-apply → assign the actual executor its own scoped read/update role → create a Session and submit authenticated text → real worker claims the Run → model emits the typed preference call → shared executor applies once → model receives applied=true → Runtime completes. Observed: expected settings and revision, unchanged persona, one safe receipt/audit, no approval and no global Profile. The paired read-only executor journey returns RequiresApproval/applied=false, completes without a write, and creates neither receipt nor approval.

Store integration covers missing own read/update, original requester lacking update, stricter tenant review, pinned/current management and live freeze; malformed authority/unrelated fields; reordered-argument replay and changed-argument conflict; clear-to-default preserving unrelated settings; account/role/freeze/lease/caller cancellation/Run cancellation races and one competing winner. Receipt schema tests reject duplicate source calls, missing/mixed sources and forged tenant/executor/root; existing approved receipt tests still reject altered hash/executor/root. Migration parity, SQLite reopen and safe downgrade behavior pass in disposable fixtures.

Evidence logs: `/private/tmp/tenant-direct-preferences-model.log`, `/private/tmp/tenant-direct-preferences-boundaries-final.log`, `/private/tmp/tenant-direct-preferences-application.log`, `/private/tmp/tenant-direct-preferences-api-regressions.log` and `/private/tmp/tenant-direct-preferences-infrastructure-final.log`.

## Remaining boundaries

This verifies one existing eligible auto-apply category. Other direct and approved operation families, preference approval creation/consumption, Run-B consumption, complete authorization-decision audit coverage, proactive revocation/storm controls, tenant Chat/output/audio and approval UI, runtime credential use and final obsolete-authority/schema removal remain required. Browser behavior was not changed or requalified. CI and live-provider acceptance were not requested or run.
