# Tenant Credential binding verification — 2026-10-17

Baseline: `cc785a1d`. This checkpoint implements association configuration over actual tenant-owned Instances/Credentials. It does not implement runtime secret consumption, OAuth callbacks or credential UI. The active full proposal remains unchanged; all execution uses disposable Synthetic/SQLite data.

## Delivered behavior

- `ITenantCredentialBindingStore` proves both owners and reads a coherent Instance/binding/Credential snapshot. `TenantCredentialBindingService` independently checks parent read/update, exact Credential metadata read/bind and current Connections policy/freeze for agent writers. Metadata filtering precedes counts/pages. Unlink can remove a known association using bind authority without returning unreadable Credential data.
- Binding/unbinding advances actual Instance and tenant security revisions with attributed redacted audit in one transaction. Logical Credential/binding revisions fence the respective changes; Credential values and role assignments are preserved. Reference/credential uniqueness is Instance-local; rebind mints a new binding ID.
- New binding requires an active non-registry Credential and active Instance. Disconnected OAuth association is configuration only. Disabled existing credentials can be unlinked. Archived Instances retain readable associations and must be restored before changes. Bound Credential deletion remains blocked until unlink.
- Canonical rows carry server-derived Tenant and composite foreign keys to the owning SystemIdentity and Credential root. Required agent Tenant/Instance backing and alternate keys support those constraints. Legacy rows remain null-Tenant only in the intermediate development schema; canonical stores never adopt that shape. Final schema consolidation must remove it alongside obsolete authority.
- Legacy get/list/mutation/cleanup and secret resolvers cannot consume owned associations. Binding creates no role assignment or `credentials.use` grant; downstream effects remain separately gated and pending cutover.
- Canonical cookie/CSRF/no-store HTTP uses `/api/v2/tenants/{tenantId}/agents/{instanceId}/credential-bindings`. Create returns safe binding metadata and new revisions; remove returns revisions only. Malformed requests preserve 4xx status through a generic safe `InvalidRequest` without echoing submitted values or producing server diagnostic IDs.

## Runtime evidence and resolved issues

The representative workflow created a protected Credential and two owned Instances, bound distinct aliases, read safe metadata, denied legacy consumption, blocked bound deletion, reopened SQLite, disabled the Credential, denied a new binding, unlinked one association, archived/read/denied mutation/restored the other, then unlinked and deleted. Re-read Instance revisions, retained binding IDs and private protected-value verification matched the expected state; Credential logical/secret generations stayed unchanged by binding and agent `credentials.use` stayed absent.

The first SQLite run exposed EF rebuild ordering: the rebuilt child needed composite uniqueness before its parents were replaced. Migration `20261016195121_TenantCredentialBindings` now creates temporary uniqueness witnesses that disappear with the parent rebuild; direct schema inspection confirms none remain. Generated empty/zero owner defaults were removed. An initial mutation query also used the untracked inventory join; mutation queries now explicitly track the backing Instance, and tests verify persisted revisions. The complete fresh migration graph reports no pending model changes and rejects both cross-tenant FK edges.

Nine new store cases cover both-store lifecycle/reopen, partial reads and paging, independent parent/credential rights, unlink without metadata read, revoke-before-stale denial, Account suspension after preparation, cancellation, single-winner writes, agent Connections policy/freeze, no automatic use grant and actual SQLite FK rejection. Two full Synthetic HTTP journeys exercise owner joins, foreign/registry denial, duplicates, blocked deletion, archive/restore, current scoped permission/revocation, safe audit and malformed bodies. The first HTTP setup exposed ASP.NET's reserved `BindAsync` discovery; explicit service injection now matches the existing credential endpoint pattern. The fixture uses the existing numeric lifecycle contract.

Malformed input previously reached the internal-error path. The shared handler now preserves 400/413/415 without logging/echoing rejected values; three new integrated diagnostic cases and the existing 500/503 cases pass. Test initialization now cleans its temporary root on failure; the two verified empty roots from failed migration setup and the task-owned generated SQL script were removed. No user data or unknown host/process was touched.

## Focused checks

Commands use `dotnet test tests/<project>/<project>.csproj --filter '<FullyQualifiedName~term|...>' --no-restore --nologo --disable-build-servers -p:UseSharedCompilation=false -m:1 -v minimal` with approved local execution for VSTest's communication socket.

| Project | Filter terms | Final result |
| --- | --- | --- |
| Infrastructure | `TenantCredentialIntegrationTests`, `IamIdentityIntegrationTests`, `SystemCredentialTests`, `BrowserCredentialSinkTests`, `EmailAccountIntegrationTests`, `GmailOAuthStorageTests`, `DefinitionResourceInfrastructureDiTests`, `AgentWorkspaceDeletionTests` | 82 passed, zero failed/skipped |
| API | `TenantCredentialAccessTests`, `CredentialApiTests`, `EmailAccountApiTests`, `GoogleEmailOAuthApiTests`, `IdentityAndTenantAccessTests`, `IdentitySqliteRestartTests`, `TenantAgentAccessTests`, `TenantDefinitionAccessTests`, `HttpDiagnosticTests`, `DiagnosticLoggingTests`, `SelfManagementAuthorityTests`, `SelfManagementOperationTests` | 60 passed, zero failed/skipped |

Total distinct cases in the final selections: **142**. Focused runs overlap these selections; the two-store lifecycle was confirmed again after adding an explicit unchanged-secret/generation assertion. Documentation hygiene passed: **10 Markdown files, 669 local links/fragments, 20 complete JSON examples**, balanced fences and `git diff --check`. No full CI/milestone suite, provider calls, deployment or Real restart was requested/run. No frontend changed; browser/visual acceptance remains separate.

## Remaining full-goal work

Runtime credential admission/use, initiator/Account/session/tenant-bound OAuth and credential UI remain open, along with other owning resource/effect families, Sandbox/decommission, full Instance/access/preview panels, Session/Run/Hub/tool/background tenancy, trusted executor/requester/root lineage, durable approvals, obsolete global authority removal and final schema/reset consolidation. This intermediate source does not establish full tenant isolation or milestone acceptance.
