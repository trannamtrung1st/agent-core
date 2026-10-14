Method: dual-agent (A: `/root/design_review` · B: `/root/detector_review`), with parent-owned Synthetic browser verification.

# Recent Git, UI/UX and design documentation review

Date: 2026-10-14. Source: `d279abfe`, including the existing working-tree report correction. Scope: all 22 commits in `8e053018..d279abfe`, the interval since the preceding preview/UI polish baseline. This is a focused review and documentation refresh, not milestone acceptance.

## Open review finding

**[P2] Explain shortened XLSX cell text.** `web/src/features/filePreview/SpreadsheetPreview.tsx:26` receives `projected.truncated` but never displays it. `spreadsheetAdapter.ts:25` shortens cell/window values; `ReadonlySpreadsheet.tsx:17` also uses this shortened projection for the selected-cell readout. CSV displays a shortening notice; XLSX does not. The ordinary “Partial workbook” label explains range coverage, not omitted cell text.

Browser reproduction: intercept the disposable Synthetic host's preview-options response with valid `spreadsheet.maxCellCharacters=4`, reload an uploaded `composer-upload.xlsx`, then open Preview. The readout displays `A1 · Rev…` instead of the source `Revenue`, without a text-clipping notice. Original bytes and Download remain intact. Add a visible notice when `projected.truncated` is true, with smaller-range/original-Download recovery. Review-only scope leaves this implementation finding open; the canonical frontend specification records the gap rather than treating it as verified behavior.

**[P2] Drive approval Escape rejection fails after the preview/reconnect journey.** With the correct disposable OAuth origin, `web/e2e/drive-office.spec.ts` failed twice at line 261 (48.0s and 51.4s total scenario durations). After file previews, disconnect/reconnect and a batch-trash request, pressing Escape with focus inside the approval did not dismiss/reject it; the dialog remained visible and the Run stayed “Waiting for your approval…”. The requested files were not confirmed as trashed. Investigate the dialog/focus/key-event lifecycle and retain this regression before claiming keyboard rejection works through that journey. The root cause and explicit Reject-button recovery were not established in this review; backend Drive integration tests still pass.

These are the two open findings. This is not proof of exhaustive backend correctness or comprehensive canvas accessibility.

## Review coverage and design sync

- Attachments: binary intake, Office context/continuation, readable-state propagation, compact pending tiles, upload errors and original-byte preservation.
- Presentation: shared `ChatFileCard` for attachments/artifacts; available-width user bubbles; bounded filename tooltips; grouped Preview/Download actions; source-owned image/download lifetimes.
- Previews: shared lazy FortuneSheet canvas, source-coordinate navigation, host policy/byte guards, paste/read-only containment, selection and renderer recovery.
- Drive/Credentials: exact-target approval validation, responsive review tables, purpose-based credential creation, removal of generic Save to Drive controls, search reset and agent-owned upload-session continuation.
- Non-UI inspection: guarded Office extraction and output-budget continuation, public binary download flow and Drive recovery changes. Focused API integration tests supplement source review; live provider behavior remains outside this pass.

The design still fits the existing Ant Design v6 dark Operate presentation. Reuse is strongest in `ChatFileCard`, `FilePreview`, existing credential field compositions and the shared approval-table styles. `AppShell` maps AntD tokens; parent layouts own outer insets and gaps, cards own internal padding, and non-shrinking action groups preserve controls. No palette, UI kit or architecture replacement is introduced. Independent source/capture assessment scored 29/40 on Impeccable's ten design heuristics; this provisional score does not certify accessibility or override the runtime findings. Assessment B scanned source only; browser interaction/geometry evidence came from the parent, without live detector-overlay injection.

The refresh updates `.agents/context/DESIGN.md`, its Impeccable preview sidecar, PRODUCT context and relevant surface source lists. It removes stale save-destination/recovery-form guidance, updates the shared credential-purpose choices, aligns sent-file guidance with `ChatFileCard`, and records the XLSX notice gap. Existing tokens and historical acceptance evidence remain intact. `/docs` remains authoritative.

## Focused local verification

| Check | Result |
| --- | --- |
| `pnpm exec vitest run src/features/chat/AttachmentPreview.test.tsx src/features/filePreview src/features/chat/ApprovalModal.test.tsx src/features/admin/SystemCredentialsPage.test.tsx` | 50 tests passed across 8 matched files. No file matched the requested SystemCredentialsPage path; credential journey coverage is separate. |
| `pnpm run build` | Passed; existing SignalR annotation and large-chunk warnings remain. |
| `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --filter 'FullyQualifiedName~AttachmentApiTests\|FullyQualifiedName~FilePreviewPolicyTests\|FullyQualifiedName~DriveJourneyTests' --nologo` | 62 tests passed, none failed/skipped. The actual filter uses literal pipe alternatives without Markdown escapes. |
| Impeccable `detect --json` across 24 existing changed TSX product files | Exit 0, empty findings/advisories. A first attempt included a deleted SaveToDriveButton path; retry excluded it. |
| Playwright MCP: upload XLSX, send an order-91 message, inspect response/file cards | Observed completed “Order 91 is delayed.” response, uploaded file card and generated `case-note.md`. |
| Playwright MCP: widths 1440/768/767/390/320 | No document/card horizontal overflow; actions stayed within both attachment/artifact cards. Desktop cards capped at 512px. At 320px attachment/artifact cards measured 272/296px. |
| Playwright MCP: Preview, selected cell, Escape | Observed `A1 · Revenue`; Escape closed the preview and restored its trigger's focus. |
| Playwright MCP: fail content download with HTTP 503, then retry by keyboard | Filename/error retained; Enter on Retry downloaded `composer-upload.xlsx` and cleared the local error. |
| Playwright MCP: valid smaller cell-text policy | Reproduced the open XLSX notice finding above. |
| CLI: six affected Synthetic browser specs | 9/10 passed: approval layout/short-screen review, compact Office/binary uploads, upload retry/removal, protected preview formats, CSV source/copy/download, shared file cards/download recovery, Office originals and spreadsheet read-only guards. Drive required the follow-up below. |

CLI command: `PLAYWRIGHT_API_PORT=5280 PLAYWRIGHT_WEB_PORT=5373 PLAYWRIGHT_FAITHFUL_MANUAL=1 pnpm exec playwright test e2e/message-files.spec.ts e2e/composer-attachments.spec.ts e2e/approval-layout.spec.ts e2e/drive-office.spec.ts e2e/file-preview-formats.spec.ts e2e/office-preview.spec.ts --project synthetic`.

The first Drive attempt timed out at offline consent navigation because the disposable host inherited a different OAuth return origin. Restarting only this review host with `GoogleOAuth__PublicAppUrl=http://127.0.0.1:5373` resolved authorization. The focused rerun exercised create/connect/check/bind, agent-created DOCX/XLSX and Drive uploads, Drive search/Clear, range/cell persistence through five widths, Word preview/download, disconnect/reconnect and return to Chat. It then failed at `web/e2e/drive-office.spec.ts:261`: Escape on the batch-trash approval did not produce the expected rejection result within 30 seconds; the approval remained visible and the Run remained waiting for approval. This is an unresolved browser recovery check, not a passing Drive journey. The backend Drive integration filter passed separately. Trace/error context are under `web/test-results/drive-office-connect-expli-46841-previews-and-epoch-recovery-synthetic/` (generated, ignored output).

The MCP-retried XLSX download also matched the fixture bytes exactly. Six changed Markdown owners/references passed local link/anchor and fenced-block checks; the sidecar parsed as schemaVersion 2 with synchronized narrative. `git diff --check` passed.

Disposable Synthetic native backend/Vite ran at `127.0.0.1:5280/5373` with InMemory data and separate `/tmp/agent-core-ui-review` file roots. Existing user hosts/databases were not reset or stopped. Initial sandbox runs could not bind Vite/.NET sockets; approved local execution resolved that setup limitation. Browser setup required separate file-chooser handling; only observed final interactions above count as evidence. Console/network inspection showed no unexpected product errors in the successful flow; the intentionally injected download failure produced the expected HTTP 503 console entry.

## Limits

Hosted CI was neither dispatched nor awaited. Full suites, milestone acceptance, real Google/live model execution, physical audio/touch, screen-reader canvas navigation, installed Office-client fidelity and actual browser 200% zoom were not verified. Historical captures/reports retain their original dates/source; no earlier hosted freeze is renewed.
