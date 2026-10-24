# Credential/account boundary reconciliation — 2026-10-24

Preimage: `52177338`, following the [Sandbox checkpoint](tenant-sandbox-boundary-reconciliation-2026-10-24.md) on the same uncommitted base. All **35 remaining methods** across seven ports have source dispositions. The current [method index](tenant-store-method-source-index-2026-10-24.json) now records **544 reviewed / zero pending slots**, including **all 180 uncalled slots**. This completes store-method source reconciliation; final proposal I/O, worker and rendered-design acceptance remains open.

## Boundaries and corrections

| Port / methods | Disposition |
| --- | --- |
| `IEmailStore` / 8 | Native immutable drafts, factual send records and selection data. Canonical callers independently bind actual tenant, Session, Agent, current credential/binding epoch, sender, saved draft and exact operation. Two selection-write slots are uncalled. Retained legacy selection lookup only serves guarded unowned admission, with no canonical tenant fallback. |
| `IDriveOperationStore` / 5 | Exact native operation ledger, CAS transitions and retained outcomes. Find/delete-owner contracts are uncalled. Ledger possession confers neither source reads nor dispatch authority; canonical typed targets and provider callbacks own both. |
| `ISyntheticDriveStore` / 4 | Credential-keyed local Synthetic file mechanism. Current bound tenant/account/source/target and exact read/mutation consent remain with the canonical caller; it is not a second principal authority. |
| `ITenantAccountOperationStore` / 4 | Exact owned account reservation, dispatch, commit and bounded recovery facts. Current Run/root/Session/Agent/source/action/claim/security/consent witnesses fence new dispatch. Recovery records exact uncertainty or already observed facts without blind replay. |
| `ITenantConnectedAccountReadStore` / 2 | Exact owned read proof and lookup. Human/model readers independently require current credentials.read, credentials.use and family/source rights. A retained proof cannot authorize a fresh private read. |
| `ITenantCredentialCheckStore` / 4 | Owned exact check reservation and factual completion, with current original-human/source/epoch/claim fences. A committed effect survives loss of view access; recovering metadata never repeats the check. |
| `ITenantCredentialOAuthStore` / 8 | Actual owned OAuth attempts, purpose/protected proof, state transitions, expiry and exact connect/disconnect facts. Only current original-human and bound source authority can initiate effects; expiry only retires proofs. |

Source review covered both implementations and bound callers, including account target/rules, scoped mail/Drive adapters and Google retry/chunk callbacks. The scoped Drive adapter already repeats its current-authority callback immediately before native writes; no speculative dispatch framework or permission was added. Remote conditional writes remain best effort against independent clients, rather than an atomic transaction with Google.

The reproduced gap was **final human disclosure** after otherwise authorized source I/O. `TenantConnectedAccountService` now repeats the actual original login/security fence after binding capture and after the complete grant/source comparison, preserving the same Tenant revision. OAuth Setup now requires the server-derived `CredentialOAuthInitiator`, repeats current human/security around descriptor/source I/O, and retains the existing read-only `credentials.read` requirement. The sole HTTP caller supplies that initiator. OAuth check/disconnect pass it into their final metadata read after the once-committed effect. Durable model continuation gains no login requirement. No permission/default, schema/migration, provider protocol, wire or new authority layer changes.

## Focused verification

**604 distinct focused cases pass**, with zero final failures/skips. Actual TRX definitions match exact selection manifests, case IDs are unique per project, corrected IDs replace their initial failures once, and counters are independently validated.

| Selection | Distinct passing cases | Scope |
| --- | --- | --- |
| New direct HTTP matrix | 48 (3m05s) | Six routes × both stores × logout, same-account relogin, password change and security revision change. Inventory, mail/Drive content, OAuth setup/check/disconnect withhold captured private metadata/content; fresh authorized GET recovers it. |
| Application | 62 (145ms) | 12 exact methods in four files: closed action/read contracts, immutable hashes, factual receipts, refusal of caller-selected authority. |
| API | 181 | 20 exact methods in ten files: model account discovery/read/draft/send/Drive, independent manual/scheduled Automation, consent, exact source ownership, OAuth lifecycle and actual SQLite host recreation. Initial group: 20m17s. |
| Infrastructure | 313 (22m40s) | 52 exact methods in 20 files: both-store source/epoch/claim/CAS checks, typed native dispatch, recovery, uncertainty, no replay and retained factual reads. |

Before correction, **all 48 new direct cases failed with actual HTTP 200 instead of the required 403/409**, without setup failures. After correction all pass, including strict reason/no-private-field checks, fresh GET recovery and exactly-once OAuth/read-proof counters. Fresh metadata recovery does not repeat effect POSTs. Baseline and fixed IDs match exactly.

The broader API group initially passed **175** and failed **six existing SQLite fixture cases**. Two expected the historical “scheduled lineage” trigger text after the contract expanded to actual independent lineage; they now check SQLite constraint code 19 and the current exact message. Four attempted a guarded downgrade, then queried current-model receipts after EF had committed removal of empty successor schemas before an older populated migration refused. The fixture now upgrades those successors before current-model reads and checks no pending model changes. It retains the actual downgrade refusal and preserved consent/receipt assertions. **Only those six cases reran**, passing in 1m03s; the 175 passing cases were preserved. These are fixture corrections, with no migration or production-account change beyond the reproduced disclosure fix.

Serial solution builds passed with zero warnings/errors: production fix 50.42s (`/private/tmp/tenant-account-fixed-build-20261024.log`), then fixture correction 12.47s (`/private/tmp/tenant-account-fixture-fixed-build-20261024.log`). This checkpoint's compiled production source SHA-256 is `edcc9229a5b54fc3df5ea9e77508c10f4c8ad91cbd1bb9df00c82edf016783de`: 1,712 C# files and 1,542 bound sites. The r14 read-only analyzer has only nine expected generated-regex diagnostics; the real builds are clean.

Evidence stems: `/private/tmp/tenant-account-{baseline,fixed,fixture-fixed}-20261024`; exact grouped selections, filters, wrappers and logs: `/private/tmp/tenant-account-reconciliation-{api,infra,app}-20261024{-selection.json,-filter.txt,.sh,.log}`. TRX files reside under the corresponding test project's `TestResults`. `/private/tmp/verify-tenant-account-results-20261024.py` verifies exact manifests/IDs/counters and writes `/private/tmp/tenant-account-reconciliation-results-20261024.json`. Counts are scoped to this module; reruns are included once, not added to totals.

## Remaining acceptance

The source census retains 92 interfaces, 544 callable slots, 547 syntax bodies (three inherited defaults), 1,542 sites and 1,712 production files. All method dispositions are now reviewed. This is navigation and named boundary evidence, not proof that every possible private I/O timing is atomic. The [acceptance matrix](tenant-proposal-acceptance-matrix-2026-10-24.md) retains final 20-I/O/ten-worker reconciliation and rendered keyboard/read-only/error/responsive/design confirmation. No live provider/customer effect, Real-host restart, CI, push or deployment was performed. Existing staged commit scope and `.agents` remain untouched.

The final terminal-review presentation successor is qualified separately in [final UI acceptance](tenant-final-ui-acceptance-2026-10-24.md). Current source navigation now uses r15 / `bd9b0dfbfa3ba5c43afd1c51e66849b384051850ea2a667177b91f3efb77e930`; account604 retains the unchanged account owners and its actual edcc build version.
