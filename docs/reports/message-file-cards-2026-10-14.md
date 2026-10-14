# Message file card polish — 2026-10-14

Sent attachments and generated artifacts reuse `ChatFileCard`, preserving Ant Design controls and the existing file-chip tokens. Each row contains a shrinking filename/type column and a non-shrinking Preview/Download icon group. Full filenames have titles and hover/keyboard tooltips. The collection owns aligned stacked rows; file-bearing user bubbles reserve available width. Image thumbnails retain their authenticated source lifecycle above the named file card. Pending composer tiles retain their separate upload/remove behavior.

Verified locally with a disposable SQLite Synthetic API on 5086 and Vite on 5186; no live provider account or hosted CI was used.

- Playwright MCP: upload/send multiple long Vietnamese XLSX filenames, reload history, preview Revenue, close with Escape and restore opener focus, download the original filename, inject a 503 download failure and retry successfully. The generated Markdown artifact also previews and downloads successfully. Inspected both shared consumers at 1440/768/767/390/320px, with 8px internal gap, 8px × 12px padding, aligned rows, contained actions and 40px mobile hits. Console errors: zero; the injected 503 was expected and recovered.
- Local Chromium: `message-files.spec.ts` plus `artifact-delivery.spec.ts` — 3 passing scenarios. The two-file scenario verifies exact workbook bytes, keyboard filename disclosure, preview focus restoration, local retry, reload and viewport containment; artifact scenarios cover exact bytes through reconnect/ended history and metadata/download failure recovery. Final focused two-file confirmation also passed.
- Vitest: `ArtifactView.test.tsx` (7), `AttachmentPreview.test.tsx` (5), `SessionArtifacts.test.tsx` (4) — 16 passing tests. SessionArtifacts emits existing React `act` warnings; no failed tests.
- `pnpm --dir web build` — passed, retaining the existing large-bundle warning.
- `dotnet build src/AgentCore.Api/AgentCore.Api.csproj --nologo` — passed, zero warnings/errors, to run the matching local Synthetic host.

The running workspace includes other pending Office preview and binary intake changes; this patch preserves their source behavior and does not include them in the focused UI commit. This is a local UI fix, not a new milestone or hosted-CI acceptance claim.

[Desktop two-file evidence](assets/message-file-cards-2026-10-14/1440.png) · [390px two-file evidence](assets/message-file-cards-2026-10-14/390.png)
