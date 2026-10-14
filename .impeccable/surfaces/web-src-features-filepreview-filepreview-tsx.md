---
version: 1
slug: "web-src-features-filepreview-filepreview-tsx"
primary_target: "web/src/features/filePreview/FilePreview.tsx"
related_targets:
  - web/src/features/filePreview/CsvPreview.tsx
  - web/src/features/filePreview/TextPreview.tsx
  - web/src/features/filePreview/WordPreview.tsx
  - web/src/features/filePreview/SpreadsheetPreview.tsx
  - web/src/features/filePreview/HighlightedCode.tsx
  - web/src/features/filePreview/SaveToDriveButton.tsx
---

# Shared file preview

Mode: Read. Inspect a source-owned file and reach its original download without losing the source context. Save to Drive, account connections and Drive browsing retain Operate mode in the Chat/Admin briefs.

## Direction contract

THESIS: The document leads; existing Ant Design chrome keeps source identity, reading controls, original Download and Close immediately recognizable. One shared preview serves Attachment/Artifact, Workspace and Resource entry points, including protected Drive binaries.

FORM: Filename uses CSS ellipsis with its complete title. The wrapping metadata group retains source/size and a tight 4px MIME relationship; narrow screens truncate the MIME line with full title/tooltip disclosure. Shell/renderer stacks own 12px gaps; control groups own 8px gaps. Desktop uses a 90vw canvas capped at 1440px with a body bounded to min(80dvh, 900px). Below 768px the Modal fills the viewport with a 12px inset; primary controls, input/checkbox/tab labels and dialog close use 40px minimum height. Grids and code scroll locally. Source collections retain their own action-row spacing and native icon tooltips.

RENDERERS: CSV opens in Grid with local Grid/Text and optional first-row headers; unused single-page pagination is omitted. Text/source provides wrap, copy and clearable find. Images retain fit/actual-size/zoom and local pan. Audio/video use native controls and truthful fallback. Word keeps original document ink on white paper in a sanitized scriptless frame; a slider has a visible tabular percentage. XLSX uses named worksheet tabs without duplicate bottom margin, an expanding mobile range field, start-aligned cells and explicit formula/cache labels. Source identity preserves the selected range/cell across responsive parent updates. These descriptions summarize presentation; format policies, authentication and bounds belong to `/docs`.

STATES: Keep filename/actions outside the bounded canvas. Loading, empty, unsupported, corrupt, permission, stale and codec states reuse native Ant Design feedback and local safe recovery. Source/owner changes close or clear retired content and abort reads. Download remains explicit. Close/Escape returns focus to a surviving trigger; trusted Word-frame Tab/Escape handling preserves the dialog path.

## Evidence and ownership

Current source is `0763058d` (2026-10-14); presentation geometry is captured against `8e053018`. The [polish report and confirmation captures](../../docs/reports/file-preview-drive-ui-polish-2026-10-14.md) record 1440/1024/768/390/320 widths, short-screen save, retry/receipt and keyboard flows, 1,004 unit passes and four Synthetic browser journeys. The [successor ledger](../../docs/reports/office-file-preview-drive-verification-2026-10-13.md) preserves earlier hosted acceptance and the separate real Google, native browser zoom, physical input/screen-reader and installed Office-client gaps. This brief adds no new execution or acceptance claim.

[Frontend behavior](../../docs/13-frontend-implementation-spec.md#shared-source-owned-file-preview) is authoritative. [DESIGN.md](../../.agents/context/DESIGN.md) owns shared presentation policy; `.impeccable/design.json` contains static excerpts, not operational controls or a replacement UI library.

The bounded Drive recovery follow-up retains the same footer, token gaps and mobile targets. It distinguishes observational Check operation, exact-session Continue upload and explicit Start new save after proven non-execution. Local runtime evidence and remaining verification limits are in the [recovery report](../../docs/reports/drive-upload-recovery-2026-10-14.md).
