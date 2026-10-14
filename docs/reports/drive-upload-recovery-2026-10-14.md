# Drive upload and effect recovery — 2026-10-14

Local follow-up to the reviewed `d5e7eb86a5b649d7579d31c0649c3bc01cd4350d` baseline. The user explicitly requested fixes with local verification and no wait for external CI. The prior review's seven-job hosted closure is recorded in the [review ledger](file-preview-drive-review-2026-10-14.md); that result is not attributed to these new changes. P10/P11 and historical feature freezes are unchanged.

## Corrections

- Initial writes explicitly rejected by Google, or cancelled before any effect request, report normalized non-execution evidence. The existing operation record can become terminal Failed only with that proof. It releases the pending-effect claim, while the same operation ID remains terminal and cannot coalesce into a later matching pending operation. A fresh save is an explicit owner action with a new ID. Missing metadata, transport loss, elapsed time, reconnect and session expiry never count as non-execution proof.
- Binary uploads retain the generated remote ID and encrypted original upload session. Content requests are bounded to 1 MiB. After transport loss or adapter restart, an empty status PUT determines the next accepted offset before continuation. Invalid/regressing offsets, stalled progress and exhausted request/recovery budgets stop safely. Empty, aligned and full 50 MiB files are covered. Completed upload status still requires exact metadata/size/checksum reconciliation.
- An expired upload session loses its local continuation authority but remains Indeterminate. No replacement session or file is created. Same-host effect/inspection gates are bounded and preserve durable CAS; no worker, execution engine, storage root or schema migration is added. Continuation requires the original authEpoch and immutable source snapshot.
- Save to Drive distinguishes read-only Check operation, exact-session Continue upload, and explicit Start new save after proven non-execution. Network/server/malformed-response uncertainty retains the original ID and locks the destination. The native footer, token gaps, loading names and 40px mobile targets remain. Canonical contracts, design guide/sidecar and affected surface briefs are synchronized.

The implementation follows Google's [resumable upload/status contract](https://developers.google.com/workspace/drive/api/guides/manage-uploads) while retaining Agent Core's stricter uncertain-effect fence instead of automatically creating another session after expiry.

## Executed local verification

Expected recovery outcomes were selected before running the checks: no duplicate create after an unknown response, same-session/offset continuation after partial acceptance, terminal behavior only with proof, and original-epoch/source fencing.

| Check | Result |
| --- | --- |
| Infrastructure filter `GoogleDriveTests`, `DrivePersistenceTests`, `OfficeProcessorTests` | 44 passed; zero failures/skips |
| API filter `DriveJourneyTests`, `OfficeJourneyTests` | 9 passed; zero failures/skips |
| Vitest filePreview directory, DriveFiles and drive service tests, one worker | 23 passed across six files in 23.34s |
| Frontend build | Passed in 7.14s; existing SignalR annotation/large-chunk warnings remain |
| Synthetic Playwright drive-office, file-preview, file-preview-formats, office-preview | 4 passed in 49.8s, fresh isolated database |
| Final drive-office rerun after exact-identity precedence correction | 1 passed in 28.6s, another fresh database |
| Documentation/design validation | 21 canonical documents with zero link/fence issues; 398 changed-document relative targets, sidecar parsing/freshness, surface targets and unchanged primitives/narrative passed |

The integrated checks execute connect/bind, upload/readback, protected exact downloads, unknown failures before/after remote creation, service restart, concurrent same-effect calls, terminal receipt inspection, explicit fresh operation, partial continuation, changed-source rejection and reconnect with a different epoch. Transport fixtures exercise actual adapter HTTP requests with partial accepted bytes, lost final responses, bounded repeated loss/no progress, malformed offsets, expired sessions, cancellation and adapter recreation. SQLite/InMemory checks preserve terminal proof, prevent terminal redispatch and verify protected-session recovery with the key ring.

Playwright MCP exercised the running disposable Synthetic app. At 390×800, a non-executed UI fault remained locked until Start new save; two requests used different IDs and the real Synthetic save returned a verified receipt. At 390×640, a partial-upload UI fault exposed Continue upload; name/folder remained disabled, the 40px action stayed inside the viewport, keyboard Enter continued the identical request, and the actual Synthetic save completed. No page exceptions or document horizontal overflow were observed. These UI envelopes are injected fixtures; they do not claim that the Synthetic provider implements Google's resumable wire protocol. Adapter transport tests cover that protocol separately. Confirmation captures are local `.playwright-mcp/drive-terminal-recovery-390.png` and `drive-upload-continuation-390.png`.

The first sandboxed .NET attempt could not create MSBuild's local named pipe. The permitted native rerun completed all focused checks. Only owned temporary hosts were stopped; user services/data were preserved. Full backend/frontend suites, Compose and new hosted CI were not rerun for this bounded follow-up. Local verification is the requested completion scope, not a new full milestone acceptance claim.

## Conditional enhancements and remaining checks

Google Picker is a suitable optional follow-up for explicit authorization of existing files under `drive.file`. The official [web Picker contract](https://developers.google.com/workspace/drive/picker/guides/overview) requires a specific access token in its client-side API. Integrating that safely needs an explicitly designed owner-scoped, short-lived token handoff and Google project configuration; the current backend-only token boundary remains. No broad scope or ad hoc token endpoint was added. The review makes this demo-dependent, and no current demo requirement was supplied.

Reusable branded Office templates/charts remain optional; the reported v1 generation capabilities are retained because no concrete report/template requirement was provided. Dedicated Google test-account OAuth/transfer UAT, installed Word/Excel fidelity, actual native 200% browser zoom and physical touch/screen-reader acceptance remain unverified. No live credentials or provider calls were used, and production-ready acceptance for those scenarios is not claimed.
