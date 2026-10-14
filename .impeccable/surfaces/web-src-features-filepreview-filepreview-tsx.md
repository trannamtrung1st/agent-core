---
version: 1
slug: "web-src-features-filepreview-filepreview-tsx"
primary_target: "web/src/features/filePreview/FilePreview.tsx"
related_targets:
  - web/src/features/filePreview/CsvPreview.tsx
  - web/src/features/filePreview/TextPreview.tsx
  - web/src/features/filePreview/WordPreview.tsx
  - web/src/features/filePreview/SpreadsheetPreview.tsx
  - web/src/features/filePreview/ReadonlySpreadsheet.tsx
  - web/src/features/filePreview/spreadsheetAdapter.ts
  - web/src/features/filePreview/HighlightedCode.tsx
---

# Shared file preview

Mode: Read. Inspect a source-owned file and reach its original download without losing the source context. Account connections, Drive browsing and conversational mutations retain Operate mode in the Chat/Admin briefs.

## Direction contract

THESIS: The document leads; existing Ant Design chrome keeps source identity, reading controls, original Download and Close immediately recognizable. One shared preview serves Attachment/Artifact, Workspace and Resource entry points, including protected Drive binaries.

FORM: Filename uses CSS ellipsis with its complete title. The wrapping metadata group retains source/size and a tight 4px MIME relationship; narrow screens truncate the MIME line with full title/tooltip disclosure. Shell/renderer stacks own 12px gaps; control groups own 8px gaps. Desktop uses a 90vw canvas capped at 1440px with a body bounded to min(80dvh, 900px). Below 768px the Modal fills the viewport with a 12px inset; primary controls, input/checkbox/tab labels and dialog close use 40px minimum height. Grids and code scroll locally. Source collections retain their own action-row spacing and native icon tooltips.

RENDERERS: CSV opens in Grid with local Grid/Text and optional first-row headers; unused single-page pagination is omitted. Text/source provides wrap, copy and clearable find. Images retain fit/actual-size/zoom and local pan. Audio/video use native controls and truthful fallback. Word keeps original document ink on white paper in a sanitized scriptless frame; a slider has a visible tabular percentage. XLSX and CSV Grid share a bounded lazy FortuneSheet canvas. Named worksheet tabs avoid duplicate bottom margin; mobile range fields grow into the wrapping toolbar. Source-coordinate headers/selection, range Copy, zoom and explicit formula/cache labels remain compact. Backend policy owns budgets; partial parsing, shortened long fields and cross-window merges are disclosed. Read-only guards hide editing inputs/handles/shortcuts and preserve Modal focus/Escape. Source identity preserves the selected range/cell across responsive parent updates. These descriptions summarize presentation; format policies, authentication and bounds belong to `/docs`.

STATES: Keep filename/actions outside the bounded canvas. Loading, empty, unsupported, corrupt, permission, stale and codec states reuse native Ant Design feedback and local safe recovery. Source/owner changes close or clear retired content and abort reads. Download remains explicit. Close/Escape returns focus to a surviving trigger; trusted Word-frame Tab/Escape handling preserves the dialog path.

## Evidence and ownership

Current generic file actions are Preview and Download. The [Drive credentials successor ledger](../../docs/reports/drive-credentials-ux-verification-2026-10-14.md) records local compact-card and approval verification. Earlier preview/Drive reports preserve historical evidence and manual/Real gaps.

[Frontend behavior](../../docs/13-frontend-implementation-spec.md#shared-source-owned-file-preview) is authoritative. [DESIGN.md](../../.agents/context/DESIGN.md) owns shared presentation policy; `.impeccable/design.json` contains static excerpts, not operational controls or a replacement UI library.

Upload-session continuation remains an agent capability with exact source/destination/epoch receipts; the generic source-save component and its routes are removed.

The [recent UI review](../../docs/reports/recent-git-ui-design-review-2026-10-14.md#open-review-finding) records an open XLSX notice gap: shortened cell/window text is not explicitly explained in that renderer. Keep the intended disclosure requirement; do not infer that the canvas or selected-cell readout contains complete cell text.
