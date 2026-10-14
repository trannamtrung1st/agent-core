# File preview and Drive UI polish — 2026-10-14

This is a frontend refinement of the implemented Office/file-preview/Drive successor. Ant Design v6, the shared dark theme, existing 8/12/16px spacing roles, protected file reads, upload receipts and capability boundaries remain the baseline. It does not renew historical hosted milestone acceptance.

## Changes

- The shared preview metadata group retains source/size and a truncating MIME line with full title/tooltip disclosure. Renderer toolbars share compact gaps, mobile controls and dialog close use 40px targets, worksheet range input grows into available mobile width, and worksheet tabs no longer add a second bottom gap.
- Spreadsheet cells align their text to the start. Logical source identity keeps range reads and selected-cell details stable when a responsive collection reconstructs the FileRef. Word zoom has a visible percentage. Single-page CSV previews omit unused pagination; source search can be cleared.
- Icon-only Preview/Save to Drive use native Ant Design tooltips. Drive browsing shares the search/control row, marks folders with the established icon family, and explains local horizontal scrolling on mobile.
- Save to Drive uses the native Modal footer outside a bounded scrolling form. Destination reads offer Retry destinations; folder reads expose loading and Retry folders. Account changes clear folder options/search and abort retired reads; late results are ignored, and more-folder results are deduplicated. Success leaves one Close action; uncertainty still offers read-only Check operation.

The shared product compositions own these relationships. There are no new dependencies, UI wrappers, parallel tokens or protocol changes. Canonical frontend behavior and the shared DESIGN.md presentation context were synchronized.

## Runtime evidence

Playwright MCP used a local Synthetic .NET/Vite host with the previous task's disposable SQLite/fixture storage; no operator database or real Google connection was used. Inspection comprised one batched initial desktop/mobile round and one confirmation round. Functional verification and regression fixes remained separate from discretionary visual iteration.

| Journey | Expected and observed result |
| --- | --- |
| Drive search → binary workbook preview | Searching `q3-sales` settled to one matching file. The shared protected workbook showed original formatted values and truthful unavailable formula-cache state. |
| Invalid worksheet range → recovery | `bad` produced the supported safe error/Retry; `A1:C4` restored the grid. |
| Responsive workbook | At 1440/1024/768/390/320px, no document-level horizontal overflow; mobile input/close measured 40px, cells used `flex-start`, and resizing triggered no additional Office-preview read. The final E2E also retained selected B4 formula details at all five widths. |
| Failed destinations → Retry → save | An injected 503 exposed Retry destinations. Retry restored the bound account. Saving `polish-review.docx` showed Verified in Synthetic Drive and a new remote file receipt. Closing returned focus to the source trigger. |
| Short-screen save | At 320×450, the body scrolls while Save/Close stay within the viewport and measure 40px. The folder field remains reachable through the scrolling form. |
| Word preview → Escape | The sanitized scriptless frame displayed original black ink on white pages; zoom percentage remained visible. Escape from the document closed the dialog and restored focus to Preview. |
| Account/folder authority | Focused component regression proves that switching accounts aborts the old folder read and ignores its late result. Destination retry restores the same dialog without enabling an accountless upload. |

Final MCP actions recorded no page exceptions or failed requests. Console HTTP 400 and 503 entries were intentional error/recovery probes. An Input prefix/suffix warning occurred during development HMR while markup was being changed; repeatable final browser scenarios asserted no page errors. Initial inspection tooling had locator-role/strictness failures; those did not establish product failures and were corrected before asserting outcomes.

## Checks

- Focused Vitest: **15 passed** across five files (preview policy/source/lifecycle, Drive connection, new destination recovery tests).
- Final full frontend suite: **1,004 passed across 125 files in 696.26s**. Existing Ant Design deprecation and test `act` warnings did not fail assertions.
- `pnpm --dir web build`: passed after the final runtime change. Existing SignalR annotation and large-chunk warnings remain.
- Final Synthetic Playwright: **4 passed in 42.2s**, using a fresh database and dedicated ports. Cases cover attachment CSV source/exact downloads/focus/403 retry/five widths; images/TSV/escaped code/sanitized Markdown/native audio/video/unsupported fallback; actual Office generation/formulas/range recovery/scriptless pages; Drive connect/bind/save receipts/remote previews/reconnect plus the new destination-retry and responsive-selection assertions.
- Markdown targets/fences and definition/design JSON checks: **21 files, zero issues**. `git diff --check`: passed.

Full-suite command uses the bundled Node runtime with `NODE_OPTIONS=--no-experimental-webstorage`, preserving the repository's existing test environment expectations. Browser command:

```sh
CI=1 PLAYWRIGHT_API_PORT=5088 PLAYWRIGHT_WEB_PORT=5188 \
PLAYWRIGHT_FAITHFUL_MANUAL=1 PLAYWRIGHT_FIXTURE_PORT=5101 \
PLAYWRIGHT_SQLITE_PATH=/tmp/agent-core-ui-polish/final-e2e/synthetic.db \
pnpm exec playwright test file-preview.spec.ts file-preview-formats.spec.ts \
  office-preview.spec.ts drive-office.spec.ts --project=synthetic \
  --output=/tmp/agent-core-ui-polish/final-e2e-results
```

The prior seven-job hosted acceptance belongs to `a451600a`, not this refinement. Backend/Compose were not rerun for this frontend-only change. Real Google UAT, native browser 200% zoom, physical touch/screen-reader assessment and installed Office-client fidelity are not established by these checks. The existing successor verification ledger retains those boundaries.

## Confirmation captures

| Surface | Desktop | Mobile |
| --- | --- | --- |
| Workbook | [1440px](assets/file-preview-polish/excel-desktop.png) | [320px](assets/file-preview-polish/excel-mobile.png) |
| Drive files | [1440px](assets/file-preview-polish/drive-desktop.png) | [320px](assets/file-preview-polish/drive-mobile.png) |
| Save to Drive | [1440px](assets/file-preview-polish/save-desktop.png) | [320×450](assets/file-preview-polish/save-mobile-short.png) |
| Word | [1440px](assets/file-preview-polish/word-desktop.png) | [320px](assets/file-preview-polish/word-mobile.png) |
