# Tenant credential approval target prerequisite — 2026-10-17

This is progress toward the full multi-tenant IAM proposal, not runtime credential-use or milestone acceptance. The unfinished draft initially failed SQLite migration validation; the schema and target/review contract now have executed coverage. No draft restoration, user-data reset, installed-database migration, deployment, Real provider call or CI run occurred.

## Delivered boundary

- The same durable request/decision/grant/processed-signal engine accepts a closed typed bound Google connection-check preparation. No second authorization engine or Task entity is added.
- A current Running claim, actual owned binding pinned by admission, pinned/live purpose capability, connected active OAuth credential and current executor credential-read are required. Sensitive `credentials.use` remains mandatory Review when the executor has use, or ExceptionalGrant when the exact operation is delegatable and use is absent. Human authority never supplies the separate executor-read prerequisite.
- The exact hash retains binding, logical/secret revision, auth epoch and target witnesses. Semantic root dedupe excludes mutable witnesses so refresh or configuration changes cannot reopen a refused operation within the root.
- Safe saved evidence and typed read projections distinguish credential checks from persona/preferences. Public output contains binding/logical revision metadata, never secret revision/auth epoch, protected values, provider bodies or authority tokens. Human inventory/read require approval-read and credential read/use; filtering precedes count/offset. Decisions recheck current executor read and witnesses; stale/revoked checks remain cancellable.
- Both store transactions validate actual binding/credential ownership and evidence. SQLite adds the nullable same-tenant credential target and discriminated constraint; downgrade refuses lossy conversion.

## Executed checks

The focused credential cases exercise InMemory and disposable migrated SQLite. They prepare a check with and without executor-use, verify mandatory Review/ExceptionalGrant, read after adapter reopen, reject without a grant, and resume once. Separate cases rotate the protected grant or revoke executor-read before decision; the expected unavailable reason is observed and cancellation still succeeds. Unknown aliases and missing executor-read produce no request. Scoped human credential-use without credential-read yields no inventory/detail; adding exact read reveals the request. A changed internal witness keeps root correlation while changing the exact hash.

The SQLite migration case retains a real saved persona request through downgrade/upgrade, adds a credential request, proves that downgrade then fails without losing it, and rejects a nonexistent credential FK.

The HTTP cases use the actual Synthetic app under cookie/CSRF authentication on both stores: create/connect a Google Drive credential, create an Instance, bind the credential, admit/claim a Run, invoke trusted preparation, inspect the exact action, and reject through the canonical approval API. They assert the existing secret-redaction contract, no exceptional grant and zero provider HTTP attempts. This check caught and removed secret-revision leakage from the initial projection.

All test commands used `dotnet test <project> --no-restore --nologo --filter <filter>`.

| Project / filter | Result |
| --- | --- |
| `tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj`; `FullyQualifiedName~TenantOperationAuthorizationTests\|FullyQualifiedName~TenantPreferenceOperationTests\|FullyQualifiedName~Runtime_credential_check_requires\|FullyQualifiedName~Credential_review_rechecks\|FullyQualifiedName~Credential_preparation_denies\|FullyQualifiedName~Credential_approval_inventory\|FullyQualifiedName~Credential_approval_migration` | 252 passed |
| Same Infrastructure project; `FullyQualifiedName~Credential_approval_for_Gmail` | 4 passed; distinct from the 252 above |
| Same Infrastructure project; `FullyQualifiedName~Credential_approval_migration` | 1 passed after strengthening retained approved-request/grant assertions; overlaps the first row |
| `tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj`; `FullyQualifiedName~TenantApprovalToolContractTests` | 5 passed |
| `tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj`; `FullyQualifiedName~TenantSessionAccessTests\|FullyQualifiedName~TenantCredentialAccessTests` | 50 passed; includes the 2 new credential approval HTTP cases |

**311 distinct focused tests passed:** 256 Infrastructure, 5 Application and 50 API. Earlier minimal scenario runs and the strengthened migration rerun overlap these totals. The final HTTP selection also exercises the existing durable model/worker, requester-context, OAuth and binding journeys.

`git diff --check` and changed-document local-link/fence validation passed. The additive EF migration was generated without applying it to installed data. The configured EF tool reported its 10.0.5/10.0.12 version difference; actual migrated SQLite behavior and pending-model validation passed without suppressing warnings. No frontend source was changed; this checkpoint does not claim browser verification of a new credential-use UI.

## Required next work

The model tool still supports only persona/preferences. No public preparation endpoint creates this kind. The existing local configuration effect executor explicitly fails closed with `tenant_effect_unavailable` for it. There is no provider consumption or external dispatch fence yet. Complete typed off-mailbox dispatch, exact grant consumption, protected rotation/reconnect results, uncertain-outcome recovery, actual model continuation and canonical UI before enabling this operation. Other credential and external operation families, tenant live transport, remaining resource ownership, obsolete authority/schema removal and the full proposal acceptance remain open.
