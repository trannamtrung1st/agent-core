# Tenant Credential lifecycle verification — 2026-10-17

Baseline: `6fcb7545`. The active full proposal continues; this checkpoint completes the owning management boundary for Credentials and adds no UI or runtime secret-use path. Disposable Synthetic/SQLite fixtures only; existing data and Real hosts are untouched.

## Implemented boundary

- `TenantCredentials` is an owning root over the existing Credential/profile rows. Tenant FK, unique credential ID, tenant/ID index and retained deletion marker prevent foreign lookup, global-row adoption and retired-ID reuse. UTC dates use the repository's integer-millisecond mapping. Migration `20261016185556_TenantCredentialOwnership` is intermediate and must join the final schema consolidation; it is not a live-data conversion.
- `ITenantCredentialStore` commits root/value, security revision and redacted audit atomically. Current actor/Account, security/logical/secret revisions and immutable identity/kind/purpose/authentication method are rechecked. Snapshots/inputs are frozen. Deletion refuses bindings and retaining direct grants, removes payload/profile and retains the exclusion marker.
- `TenantCredentialService` and canonical cookie/CSRF HTTP use typed read/manage permissions and real owned scopes. Collections filter before totals/order/paging. Exact item grants do not create siblings. Metadata reads and saved-content responses require read; management alone does not return content. `credentials.use` does not imply read or another effect.
- General, Custom email and registry protected values reuse the credential-ID/version-bound protector. Gmail/Drive creates disconnected; normal CRUD cannot assert OAuth identities/scopes/Synthetic state or replace refresh grants. Public metadata/audit omit payload, input value, secret generation and protection version.
- Legacy get/list/save/delete, OAuth rotation and binding creation hide/reject owned Credential IDs, including retired IDs. The production InMemory identity composition uses the actual shared credential store; no fallback inventory is constructed.

## Runtime evidence

The first actual lifecycle checked create → protected-value read only through the private store/protector fixture → replace → stale denial → metadata/status update → SQLite/key-ring reopen → delete → retired-ID rejection. The initial SQLite update found a timestamp representation mismatch; the new root and migration/snapshot were corrected to integer UTC milliseconds. Fresh migrated fixtures now verify no pending model changes.

Ten new both-store cases cover the lifecycle; exact scoped inventory/paging and sibling denial; replacement and revocation before stale checks; use-only metadata denial; raw retaining-grant deletion rejection and successful grant retirement; Account-suspension fencing; single-winner competing writes; stale secret generation and cancelled deletion; all four profile purposes/disconnected OAuth; forged authorization and legacy binding denial; foreign raw grants; and refusal to adopt existing global rows. Persisted SQLite bytes contain neither test plaintext secret; a reopened protector decrypts the retained value only through the private test path.

Three new full Synthetic HTTP journeys exercise cookie/CSRF, owned/foreign/legacy paths, safe no-store create/replace/update/list/delete and audit, exact grants/revocation, Gmail/Drive/Custom/registry input mapping, rejected OAuth/password/registry input and no value readback. Fixture corrections use the existing invitation-bound registration and `/role-assignments` contract; no alternate endpoints or authority were added.

The affected Infrastructure regression selection passed **100 tests, zero failed/skipped**: `TenantCredentialIntegrationTests`, `IamIdentityIntegrationTests`, `SystemCredentialTests`, `BrowserCredentialSinkTests`, `EmailAccountIntegrationTests`, `GoogleEmailOAuthTests`, `GmailOAuthStorageTests`, `GmailOAuthWorkflowTests`, `GoogleOAuthMigrationJourneyTests`, `DefinitionResourceInfrastructureDiTests`.

The affected API selection passed **30 tests, zero failed/skipped**: `TenantCredentialAccessTests`, `CredentialApiTests`, `EmailAccountApiTests`, `GoogleEmailOAuthApiTests`, `IdentityAndTenantAccessTests`, `IdentitySqliteRestartTests`, `TenantAgentAccessTests`, `TenantDefinitionAccessTests`. Total distinct cases across the final selections: **130**. The real identity SQLite restart and existing Definition/Instance HTTP workflows also pass with the added ownership model and composition.

Commands use `dotnet test tests/<project>/<project>.csproj --filter '<FullyQualifiedName~term|...>' --no-restore --nologo --disable-build-servers -p:UseSharedCompilation=false -m:1 -v minimal`. VSTest runs with approved local execution for its communication socket. Documentation hygiene passed: **10 Markdown files, 662 local links/fragments, 20 complete JSON examples**, balanced fences and `git diff --check`. Repeated focused runs overlap these selections and are not additional distinct cases. No hosted CI/full milestone suite, provider calls, application deployment or browser test was required/run; no frontend changed.

## Remaining full-goal work

Canonical tenant credential binding/use, initiator/Account/session/tenant-bound OAuth callbacks, credential UI and downstream email/Drive/browser/Sandbox effects remain pending. The same applies to other owning resource/effect families, tenant Sandbox/decommission, complete Instance/access/preview UI, Session/Run/Hub/tool/background tenancy, trusted executor/requester/root lineage, durable approvals, obsolete global authority removal and final schema/reset consolidation. This source is not a qualified multi-tenant deployment or milestone acceptance.
