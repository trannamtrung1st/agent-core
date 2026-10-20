# Tenant bound Email/Drive human reads — 2026-10-20

This checkpoint implements canonical authenticated human reads through exact tenant Agent/Credential bindings. It provides transport and ownership foundations for the remaining account operations. Agent-use consent/read execution, send/write, autonomous account watchers and the full IAM proposal remain open.

## Implementation

The human facade resolves the exact bound alias through `ITenantCredentialBindingStore`, under the original Account/authentication-session/TenantUser identity. `agents.read`, `credentials.read`, `credentials.use` and the purpose-specific `email.read` or `drive.read` remain independent. Discovery requires metadata/family reads, filters before counting/paging and reports unavailable use honestly. Agent raw permissions cannot substitute for the human's rights. Reads require an active actual executor and current owned source witnesses, even though this facade does not execute an Agent Run.

The Infrastructure reader receives encrypted canonical state and live authority/normalized-maintenance callbacks. It reuses existing Gmail/IMAP/Drive adapters. Google refresh uses the shared tenant OAuth client, exact purpose scopes and bounded responses without refresh retries. Only explicit invalid-grant, missing required scope or repeated authorization rejection can mark reconnect; transient/unexpected broad-scope failures preserve the grant. Protected rotation commits through the existing OAuth transaction before using the bearer, and never overwrites a changed/disconnected source. Bearers stay operation-local inside Infrastructure. Gmail and Drive repeat authority before each read retry. Current original-human/source denial takes precedence over an adapter's normalized transport failure.

The read facade imposes a 60-second provider deadline and closed bounded results, then commits one exact audit through the existing owning store and checks authority again before release. Both stores repeat original authentication, active executor, raw IAM and Agent/binding/Credential/secret witnesses. Audit replay is idempotent only under current authority. SQLite compares the canonical persisted millisecond timestamp. No table or migration was added.

HTTP exposes scoped inventory, mailbox search/exact message, Drive list/metadata and original binary download. JSON data is marked untrusted; responses are no-store. Binary reads require an observed version, byte cap, exact length/checksum and post-transfer metadata check. A suggested filename containing path separators is sanitized without changing original bytes. Google-native exports/folder downloads remain unsupported. No send/write, broad token endpoint or agent-use authority is added.

## Exercised outcomes

Actual Synthetic HTTP tests in Memory and SQLite connect a canonical account, create/bind an owned Agent and exercise metadata/search/message or list/metadata/original-byte paths. They verify exact account-prefixed Email identities and 65,536 original Drive bytes, plus filename disposition, untrusted envelopes, no-store behavior and successful reads without Agent role assignments. Wrong account/purpose/tenant, anonymous access, stale version and a smaller byte cap refuse disclosure. Member tests separately grant/revoke metadata, family-read and use permissions and observe filtering before totals/pages. A wrapper changes original authentication or source after actual reader completion; logout, logout/relogin, unlink and disable withhold the captured body.

Controlled HTTP transports exercise the real Google token/Gmail/Drive adapters under both stores. They assert exact shared-client refresh form and bearer placement, protected rotated refresh grants and one read audit on success. Invalid grant, missing read scope and repeated resource 401 record reconnect without a read result/audit. Broad scope and transient refresh errors retain the prior grant. Logout during token/resource I/O or before a retry refuses current authority and issues no further protected resource request. Drive binary tests return exact bytes, then independently alter checksum or post-transfer version and observe conflict with no read audit.

Direct audit-store tests reopen SQLite against the saved canonical account graph, commit/replay exactly one read, reject changed Agent/binding/Credential/secret witnesses and forged authentication-session identity, then logout/relogin. The old original session remains invalid; a newly initiated proof under the new session can commit a separate audit. This is reopened audit-store evidence, not cold-host external-account/binary recovery evidence.

## Focused checks

Logs below are in `/private/tmp/`. Passing groups were retained after fixture-only corrections; reruns after production changes targeted the affected behavior.

| Check | Result / log |
| --- | --- |
| HTTP representative Email/Drive | 4 passed; `tenant-connected-account-http-representative.log` |
| HTTP filtered inventory / independent use | 2 passed in the initial boundary group; `tenant-connected-account-http-boundaries.log` |
| HTTP original authentication/source race | 6 passed in `tenant-connected-account-http-release.log`; unlink fixture corrected to the canonical 200 response, then 2 passed in `tenant-connected-account-http-unlink.log` |
| HTTP original bytes / sanitized filename | 2 passed, overlapping the representative Drive cases; `tenant-connected-account-download-name.log` |
| Google protocol / original binary | 30 passed in `tenant-connected-account-protocol.log`; the 12 resource/retry cases initially supplied an invalid null refresh-token field, then all 12 passed in `tenant-connected-account-protocol-fixed.log` |
| Exact read audit / reopened SQLite | 2 Memory cases passed in the corrected protocol group; 2 SQLite cases passed after the production timestamp replay fix, `tenant-connected-account-audit-fixed.log` |
| Current-authority error precedence | 12 affected token/resource/retry logout cases passed; `tenant-connected-account-authority-errors.log` |
| Incumbent adapters / OAuth check and provider | 95 passed; `tenant-connected-account-adapter-regression.log` |
| Final solution build | Zero warnings/errors; `tenant-connected-account-final-build.log` |

Selected new case filters are `Tenant_connected_account_HTTP_reads_exact_`, `Tenant_connected_account_inventory_`, `Tenant_connected_account_HTTP_withholds_`, `Connected_account_real_protocol_`, `Connected_Drive_real_protocol_` and `Connected_account_read_audit_owner_`. Incumbent Infrastructure filters are `GoogleDriveTests`, `GmailEmailProviderTests`, `TenantCredentialIntegrationTests.OAuth_check_` and `TenantCredentialIntegrationTests.OAuth_provider_`. Each used `dotnet test <project> --no-restore --nologo --filter <filter>`. Build used `dotnet build AgentCore.sln --no-restore --nologo`.

These qualify **155 distinct focused cases**: 14 HTTP, 42 real-adapter controlled protocol/binary, 4 exact audit-owner and 95 incumbent adapter/OAuth cases. Duplicate reruns do not add to that count. The first compile corrected fixture use of the callback-based Drive constructor, theory data construction, store namespace and result collection names. Initial HTTP wrapper tests were resolved before assigning the callback; unlink expected the documented JSON 200 response. Those setup corrections did not relax production authorization. The SQLite timestamp replay and current-authority error precedence fixes are production changes covered above.

`git diff --check` passed. The changed-document check resolved 1,992 local links/fragments and parsed 29 complete JSON examples across 18 Markdown files; fence and untracked-source whitespace checks passed. All owned test/build processes exited. Earlier failing logs are retained as correction evidence, not acceptance results.

## Evidence limits

No live Google account, live custom IMAP mailbox, inference provider, Real host, frontend presentation or live browser UI was exercised in this checkpoint. The Google peer is controlled; the Synthetic HTTP path is actual application execution. Existing custom IMAP transport is reused with current credential-secret resolution and a final application release fence, but this checkpoint does not establish its live-server behavior. No email send, Drive write, agent account consent/execution or autonomous watcher is claimed. No CI, user-data reset, commit, push, PR or deployment was performed. The original full proposal remains incomplete.
