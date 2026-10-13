# Office, file preview and Drive verification

Started 13 October 2026; updated 14 October 2026. Implementation began from clean `d6472746`; the proposal inspected `5b2d42d4`, followed by a TODO-only commit. Concurrent approval reconnect correction `3f46ce1e` is preserved. Historical freezes remain intact; P10/P11 remain unopened.

**Implementation complete; required exact-commit hosted verification pending.** Initial implementation [`26e20b82`](https://github.com/trannamtrung1st/agent-core/commit/26e20b82b91bd8f5c8110a7294346117cc3c5501) is **not accepted**: its [workflow](https://github.com/trannamtrung1st/agent-core/actions/runs/38457840262) completed six jobs successfully, but core browser shard 1 exceeded the 30-minute job budget and reported three preview failures. A corrected final candidate remains to be verified. This successor does not retroactively expand historical acceptance.

## Implemented scope

One protected, bounded FileRef/source composition covers Attachments, Artifacts, Agent Instance `/home`, Instance/inherited Resources, Definition draft Resources and explicitly bound Drive files. Separate guarded Download returns original bytes; preview never creates another persisted copy. Common viewers cover raster fit/zoom/pan, inert text/code with lazy restricted Shiki, sanitized Markdown/Source, Papa Parse CSV/TSV Grid/Text, authenticated Blob audio/video, isolated DOCX pages and bounded XLSX worksheet/range projections. Unsupported/oversized/corrupt/unauthorized files have distinct recovery; extensionless Resource keys use allowlisted stored MIME.

Six Office tools use the existing executor/workspace authority: create/inspect/exact edit for DOCX/XLSX. SHA guards, content-anchored selectors, bounded ranges, package guards and create-only writes protect original bytes. `/home` survives Sessions; `/working` stays scratch; both publish through existing Artifacts with Office MIME. v26 opts into Office, v27 adds ten Drive tools; immutable v25 is unchanged.

Optional Drive uses a distinct credential purpose/profile, protected OAuth PKCE/state/nonce/refresh CAS, explicit bindings, default-off host gate, `drive.file`, official REST and deterministic Synthetic transport. Durable operation IDs, source hashes, auth epochs and pre-generated remote IDs prevent duplicate creates. Unknown effects remain indeterminate. Archived Instances cannot read/execute remote files. Admin Credentials owns connection lifecycle/bindings; Instance Drive Files uses shared preview; Artifact/home Save to Drive creates a new file with verifiable receipt. No Gmail token reuse, polling worker, second UI kit, persistence root or execution engine was introduced.

## Local verification

| Check | Observed evidence |
| --- | --- |
| Full backend candidate | **3,936 passed, 21 opt-in live skips**, zero failures across Domain/Application/Infrastructure/API and OrderEvents. This preceded the last guard/archive/scratch corrections; focused checks below verify them and hosted gates must cover the complete commit. |
| Final Office/Drive API journeys | **3 passed**: ordinary model/tool loop creates workbook/report, real valid downloads, exact edits and stale rejection, protected projections/publication, scratch MIME, empty document, fresh-Session home durability/scratch absence; Drive binding/owner denial, receipt recovery without duplicates, archive denial, epoch/reconnect and exact trash approval. |
| Office Infrastructure | **12 passed**: OpenXML schema validation, exact table edit/ambiguous duplicate rejection, formula cache labeling, rejected external formula/range, cancellation, external relationships, DTD, traversal, macro, expansion bomb, Word paragraph/cell/page limits and spreadsheet connections. |
| Drive transport/persistence | **17 passed**: offline PKCE/scopes/identity/cookies, refresh/restart/reconnect, 401/403/404/409/429/503, bounded read retry/no write retry, strict resumable URL, cancellation, SQLite/InMemory claims/CAS/reopen, protected grants/upload state, migration compatibility and owner isolation. |
| Existing protected sources | **11 API passes** across Attachment, Artifact, Agent Workspace and Instance Resource protection. Bodies stay owner-authorized/lazy. |
| Full frontend candidate | **124 files / 1,001 passed** using bundled Node 24 with experimental WebStorage disabled. Native Node 26 initially exposed existing global localStorage incompatibility; no product workaround added. New extensionless-MIME and concurrent approval tests are included in final hosted Node 22 gates. |
| Final focused frontend | **16 passed** covering format/BOM/CSV/TSV/source bounds, stale fetch abort/URL cleanup, shared dialog lifecycle, incumbent image/lazy protected downloads and Drive stale connection. Credentials dialog regressions separately **9 passed**. |
| Browser regression | Broad local run **238/244 passed**. Six failures: Resource selector/extensionless MIME, three legacy attachment selectors, one trace-file collision from overlapping CLI processes, one loaded-run timing issue. All six affected flows plus file/Drive journeys passed in a fresh isolated **25-test** rerun. No claim that the original broad run was green; exact-commit hosted shards are the final broad gate. |
| Final new browser journeys | **3 passed in 34.4s** on fresh storage: TSV Grid/Text/Grid, image/XML/Markdown/native MP3/MP4/DOCX header/footer/raster image/PDF fallback; Office creation, tabs/formulas/scriptless pages/downloads; Drive connect/bind/upload/preview/reconnect. |
| Production build | TypeScript/Vite pass, lazy CSV/worker/Shiki/DOCX chunks. Existing SignalR annotation and large-chunk warnings remain. Earlier cold production-server CSV/Office journeys passed. |
| Compose | Disposable `agent-core-office-verification` smoke passed: owner capability, durable home, Runs/configuration/skills, Gmail OAuth and Drive grant/binding/receipt. After recreation the same operation/file ID and exact remote bytes were recovered. Existing operator data was not reset. |

Playwright MCP exercised actual affected UI on isolated Synthetic API `5086` / Vite `5186`: Chat Markdown and multiline CSV Grid → Text → Grid; workspace provenance; Office generation/download/preview and sheet/formula navigation; Drive connect/check/bind, folders/uploads, Save as new file, preview, disconnect/reconnect and retained remote bytes. Native MP3/MP4 played from authenticated Blob URLs. No Google credentials or real Google calls were used.

The integrated `synthetic-office: q3-sales-drive` demo ran through the ordinary executor: create Sales/Assumptions workbook → inspect totals/formula → create three-page report → inspect/publish both → create app-authorized Q3 Sales folder → upload both → verified IDs/receipts. Emailing links was optional and was not performed or claimed.

Downloaded DOCX/XLSX were independently opened/parsed with python-docx/openpyxl plus OpenXML validation: report paragraphs/table, Sales/Assumptions sheets, `SUM(B2:B3)` and `0001` matched. No desktop Office-compatible GUI client is installed. That fallback **does not establish desktop layout/printing fidelity**; manual opening in a normal Office client remains unverified.

## Security and package review

DOCX renders in a sanitized scriptless iframe with restrictive CSP, no external image/font/document requests, raster data images only and retained safe layout CSS. Browser fixtures verified headings/table/image/header/footer, white paper/black text, no hostile Markdown fetch/script, inert XML, actual native media and unsupported PDF fallback. XLSX uses the same bounded backend interpreter as tools; preview never recalculates formulas.

Guards cover compressed/expanded ZIP sizes, entry/path/relationship/XML bounds, Word elements/images, workbook cells/sheets and cooperative cancellation/deadlines. Vendor calls cannot be hard-preempted mid-call; package bounds/checks around phases limit practical work. Arbitrary Office fidelity, pivots/charts/embedded objects, macros, external-data connections and dangerous formulas are not claimed. Drive version checks are best effort against independent Google clients; remote overwrite, native Google editing, broad search and sync are excluded.

Exact direct/transitive and selected grammar/theme licenses are recorded in [Technology Decisions](../10-technology-decisions.md). Redistribution notices ship in [frontend notices](../../web/public/notices/office-preview-frontend.txt), [selected Shiki grammar/theme notices](../../web/public/notices/shiki-grammars-themes.txt) and [backend Office notices](../../src/AgentCore.Infrastructure/Notices/office-dependencies.txt). SixLabors.Fonts is explicitly pinned to Apache-2.0 **1.0.0**. Production pnpm and transitive NuGet audits reported **zero vulnerabilities** at verification time; this is a dated result.

## Bounded Impeccable evidence and design synchronization

One batched inspection and one confirmation round corrected shared-owner defects: modal unmount lost focus, mobile max-width left a 304px canvas, unstable table identities, long-name action compression, dropped DOCX styles/black-on-dark paper, Drive first-click readiness and stale folder/account responses. Paper uses white/black on the token canvas; actions wrap; modal remains mounted through close. Escape inside the document iframe closes/restores the source trigger; Tab remains in the modal.

Document scroll width equals viewport at **1440, 1024, 768, 390 and 320**; grid/code overflow stays local. At 320px preview fills the viewport and long Artifact filename retains 248px beside wrapping actions. A 720×450 reduced viewport checked equivalent 200% layout pressure; **actual browser UI zoom was not independently verified**. Native codec support and assistive technology beyond accessible-label/keyboard checks remain browser dependent.

Initial: [CSV desktop](assets/file-preview/file-preview-csv-1440.png), [CSV mobile](assets/file-preview/file-preview-csv-320.png). Final: [Word desktop](assets/file-preview/word-preview-1440.png), [Word mobile](assets/file-preview/word-preview-320.png), [Excel desktop](assets/file-preview/excel-preview-1440.png), [Excel mobile](assets/file-preview/excel-preview-320.png), [Drive desktop](assets/file-preview/drive-files-1440.png), [Drive mobile](assets/file-preview/drive-files-320.png). Canonical behavior owners, Admin composition guidance, DESIGN.md, relevant Impeccable briefs and schema-2 design JSON match implemented source.

Relative Markdown paths/fences checked across 21 documents and 214 fragments across changed Markdown files: zero issues. New definitions/design JSON parse and `git diff --check` pass.

## Remaining acceptance evidence

- Seven required exact-behavior-SHA jobs: backend; two frontend shards; two core browser shards; acceptance browser; Compose. Pending publication/verification, so final acceptance is not claimed yet.
- Optional dedicated real-Google consent/refresh/upload/query/download/revocation remains unverified; no account/client credentials supplied. Synthetic receipts do not establish live Google connectivity.
- Desktop Office GUI fidelity and actual browser UI 200% zoom remain manual verification gaps, with practical fallbacks explicitly recorded above.

## Initial exact-source hosted gates (`26e20b82`)

| Required job | Result |
| --- | --- |
| Synthetic backend | Passed: Domain 195; Infrastructure 1,350; Application 1,807; API 578 = **3,930 passed**, 21 opt-in live + 6 opt-in Docker sandbox skips. Event-storage Python checks: 3 passed. |
| Synthetic frontend 1/2 | Passed: 62 files / 579 tests; production build. |
| Synthetic frontend 2/2 | Passed: 62 files / 423 tests; production build. Total **124 files / 1,002 tests**. |
| Synthetic core browser 1/2 | Cancelled by the 30-minute budget at 109/115 scenarios. Three earlier failures: Drive flow recorded React maximum update depth; TSV grid failed to show content; attachment preview lost its dialog during viewport changes. No overall acceptance. |
| Synthetic core browser 2/2 | Passed: **113** scenarios, no flaky retries. |
| Synthetic acceptance browser | Passed: **16** scenarios across all seven acceptance projects, no flaky retries. |
| Synthetic Compose smoke | Passed: owner-capability/SQLite volume and Email watcher cursor/generation restart checks, including the new Drive receipt/bytes survival assertions. |

These results apply only to the initial SHA. The previous successful baseline shard took 29m46s; adding the preview scenarios exhausted that allocation. Core job budget increases to 40 minutes with all projects, one worker and individual test timeouts preserved. CSV/XLSX now use bounded ordinary table pages (CSV at most 4,000 visible cells, XLSX 50 rows/page), avoiding measured virtual-row layout. All four affected Office/Drive/attachment/common-format browser scenarios passed locally in **38.4s**, focused unit checks **16 passed** and production build passed after correction. The attachment fixture additionally waits for upload readiness before sending: an earlier local rerun sent while uploading, correctly retaining the file as pending for the next turn. Browser failures now retain stack details. Final hosted verification must confirm the complete corrected source.
