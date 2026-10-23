# Canonical Drive combinations — 2026-10-23

This follows `230d6f2a` and closes the named canonical combination gaps in the [global Drive retirement report](global-drive-authority-retirement-verification-2026-10-23.md). It advances the [IAM delivery ledger](multitenant-iam-delivery-2026-10-16.md); full proposal-wide security, isolation, functional and rendered UI acceptance remains open.

## Scope and ownership

The current tenant fixture crosses actual IAM roles, bound OAuth/token maintenance, saved consent, human decision, durable successor claims, owned workspace capture, native Google HTTP adapters, effect reservations/settlement and operation inspection. It runs against both InMemory and temporary SQLite, recreating the owning services/store around dispatch and replay for SQLite. Controlled HTTP is offline; no live Google account or installed database is used.

Removed three unreachable global effect-hash lookup branches from `DriveService`. Every effect now explicitly loads only its approved exact operation ID. The existing mandatory tenant binding and inner authority rechecks remain. No approval defaults, schema migration, public endpoint or execution framework changes.

| Combination | Expected observable boundary |
| --- | --- |
| Exact 1/7/20-file manifests and reversed seven-file order | Separately consented file inspection; approved ordered targets only; ordered completion/inspection facts; untouched sentinel; no duplicate PATCH after replay/service recreation. |
| Stale manifest | A target changed after consent denies the compound effect before any PATCH. |
| Seven-file known rejection | First target completes, second is denied, remaining five are unattempted. |
| Seven-file lost acknowledgement before/after remote application | First target completes, second remains uncertain, remaining five are unattempted. Separately approved inspection can verify an applied second target, but cannot replay content or resume later targets. |
| Cancellation/revocation after first PATCH | One remote target is affected; remaining targets and sentinel stay untouched. Factual uncertainty survives; revocation withholds protected results. |
| Hidden source parents, including slash-containing source names | Rename/Trash/move operate on inspected target metadata without reading an unavailable source parent. Move independently inspects its destination. |
| Destination disappears after approval | Folder creation/upload/move send no native write. Once the native effect owner has been entered, the existing conservative aggregate outcome is indeterminate. |
| Invalid manifests | Empty, 21-file, duplicate-ID, unknown nested field and wildcard-ID manifests create no consent and reach no protected HTTP. |
| Seven sequential uploads | Seven distinct approvals and native IDs; DOCX/XLSX/CSV/JS/DOC/XLS/CS original bytes are unchanged; current workspace-derived MIME metadata is verified; replay sends no second content PUT. |
| Uncertain upload now in Trash, then new explicit upload | Separately approved inspection records terminal failed native evidence without changing the original indeterminate tenant receipt. A new human activation/root obtains fresh exact consent and a different native ID; the old target stays in Trash. |
| MIME parameters at native protocol boundary | Four adapter cases normalize metadata MIME to the base media type while retaining the original content header and binary bytes. Canonical workspace uploads derive their MIME from the owned path and do not accept caller-supplied MIME parameters. These protocol cases are separate evidence from tenant IAM. |

The slice adds 38 canonical integration cases (including two cases each exercising five malformed manifests) and four native protocol cases. It extends the incumbent operation-read fixture to assert original manifest order and all unattempted items, rather than a fixed three-item vector.

## Verification

The first 20-file workflow passed on both stores before expanding the grouped checks. The first grouped run exposed a stale fixture count: it expected 19 total approval/receipt triggers, while the current schema has 46. The correction checks the two named account-read/operation-read guards and compares full approval/receipt trigger names and SQL across empty downgrade/upgrade. Historical predecessor counts remain specific to their migration versions. A second recheck exposed a fixture assumption about multi-migration rollback: an eventual populated-data veto does not undo earlier empty migration downgrades. The fixture now migrates back to current, verifies the model and compares exact guard definitions before continuing current consent/recovery; existing mutation/source tampering and lossy downgrade refusals remain exercised. That known-invalid recheck was stopped, without changing an installed database or migration history. The uncertain-upload fixture also now uses a new activation for inspection, respecting the original Run's terminal uncertainty; invalid-manifest assertions use the canonical argument-error envelope.

| Focused check | Observed result |
| --- | --- |
| Final corrected Infrastructure group | **142 passed**, zero failed/skipped, **10 m 19 s**. Includes all new combinations except the already passing four upload-sequence/recovery cases, SQLite operation-read variants, native protocols/storage/binding/readiness, transfers and affected schema/recovery checks. |
| Unchanged InMemory operation-read variants | **24 passed** in the initial 161-case discovery run. Its 35 failed cases were fixture issues subsequently corrected; they are not counted as passing from that run. |
| Sequential upload/fresh recovery subset | **4 passed** on both stores in the six-case recovery recheck, **29 s** overall. Its two malformed-manifest cases initially asserted the wrong exception type; their corrected five-vector loops both pass in the final 142-case group. |
| Closed action/read contracts | **39 passed**, zero failed/skipped, **125 ms**. |
| Actual model-driven Drive actions | **14 passed**, zero failed/skipped, **1 m 32 s**, both stores. |
| First 20-file representative | **2 passed**, **47 s**; overlaps the final group. |
| Schema restoration/reopen smoke | **5 passed**, zero failed/skipped, **40 s**; overlaps the final group. |
| Final serial solution build | **Passed**, zero warnings/errors, **11.63 s**. |

**223 distinct scoped cases have passing evidence** (170 Infrastructure, 39 Application, 14 API); representative/smoke overlap is not counted again. No acceptance is inferred from the failed or aborted discovery runs. Changed-document local links/fences and whitespace checks pass.

All tests use `dotnet test <project> --no-build --no-restore --nologo --filter '<filter>'`. Projects are the existing `tests/AgentCore.Infrastructure.Tests`, `tests/AgentCore.Application.Tests` and `tests/AgentCore.Api.Tests` project files. Exact final filters:

```text
Infrastructure: (FullyQualifiedName~Canonical_Drive_&FullyQualifiedName!~Canonical_Drive_sequential_)|(FullyQualifiedName~Canonical_operation_reads_&DisplayName~True)|FullyQualifiedName~Account_transfers_|FullyQualifiedName~GoogleDriveTests|FullyQualifiedName~DrivePersistenceTests|FullyQualifiedName~TenantDriveLedgerBoundaryTests|FullyQualifiedName~DriveHostGateTests|FullyQualifiedName~Account_read_SQLite_|FullyQualifiedName~Account_action_empty_SQLite|FullyQualifiedName~Account_abandoned_claim_|FullyQualifiedName~Account_password_read_consent_
Recovery recheck: FullyQualifiedName~Canonical_Drive_sequential_uploads_|FullyQualifiedName~Canonical_Drive_invalid_manifests_
Application: FullyQualifiedName~TenantAccountActionContractTests|FullyQualifiedName~TenantAccountReadContractTests
API: FullyQualifiedName~Model_drive_actions_use_approved_metadata_and_exact_original_bytes
```

Build: `dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`. Logs use `/private/tmp/tenant-drive-combinations-` with `build-r5-20261023.log`, `infrastructure-r3-20261023.log`, `recovery-20261023.log`, `contracts-20261023.log`, `model-final-20261023.log`, `first-20261023.log` and `schema-recheck-20261023.log`. The initial discovery is `infrastructure-20261023.log`; the deliberately aborted stale-schema recheck is `infrastructure-final-20261023.log`.

No CI, live Google acceptance or rendered UI run was requested for this backend slice. These checks do not establish complete proposal acceptance.

At this Drive checkpoint the global `SessionManager`/`LocalUserProfileService` composition roots remained outside the slice. The [session/profile successor](global-session-profile-authority-retirement-verification-2026-10-23.md) records their retirement. Broader internal/schema reconciliation and final proposal-wide security/isolation/rendered UI acceptance remain active work.
