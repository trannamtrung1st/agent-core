---
version: 1
slug: "web-src-features-chat-artifactview-tsx"
primary_target: "web/src/features/chat/ArtifactView.tsx"
related_targets:
  - web/src/features/chat/ChatApp.tsx
  - web/src/features/chat/Conversation.tsx
  - web/src/features/chat/ChatMessage.tsx
  - web/src/features/chat/ChatFileCard.tsx
  - web/src/features/filePreview/FilePreview.tsx
  - web/src/features/filePreview/fileSource.ts
  - web/src/features/filePreview/previewOptions.ts
  - web/src/features/filePreview/officeSource.ts
  - web/src/services/artifacts.ts
  - web/src/app.css
---

# Tenant Artifact delivery in Chat

Mode: Operate in Chat; Read inside the incumbent shared preview. This is a bounded extension of the existing Ant Design v6 dark system. It introduces no visual world, route, generic control wrapper, token scale or separate file renderer.

## Direction and authority

The [Tenant Artifact delivery contract](../../docs/19-multi-tenant-iam-ux.md#tenant-artifact-delivery-in-chat) owns behavior. [DESIGN.md](../../.agents/context/DESIGN.md) owns incumbent presentation; [PRODUCT.md](../../.agents/context/PRODUCT.md) is tooling context. The [Chat brief](web-src-features-chat-chatapp-tsx.md) and [shared preview brief](web-src-features-filepreview-filepreview-tsx.md) retain their wider surface ownership.

The document remains the focus of Preview. Conversation identity and surrounding response text remain the focus of Chat. This code-led extension preserves the existing compact file row and bounded native preview. No separate five-block direction/quality-bar card was supplied for this pass; this brief records the finished implementation and its evidence, without inventing a retrospective direction selection.

## Shared presentation owners

`ChatApp` passes the current tenant through `Conversation` and `ChatMessage`. `ChatMessage.RichBlock` mounts `ArtifactView` with a tenant/Session/Artifact key. `ArtifactView` owns current metadata, request cancellation, explicit download and recovery state; it does not own file styling. `ChatFileCard` remains the shared sent-attachment/generated-file composition of direct Ant Design Typography, Tooltip and Button controls.

The existing `app.css` file-chip rules own the row: elevated surface, quiet border, chip radius, 8px/12px padding and 8px icon/content gap. The Artifact row caps at 32rem within its parent, centers its items and keeps actions from shrinking. The metadata column shrinks; the single-line filename ellipsizes with a complete title, focusable tooltip and full accessible action names. Secondary type/size uses the existing 12px label role. The action group retains its 4px gap. These are incumbent shared rules, not new tokens or per-tenant overrides.

`FilePreviewButton`/`FilePreview` own the native Modal, source/size/MIME metadata, explicit original Download, Close, loading/recovery feedback and renderer selection. Ant Design token spacing supplies the 12px shell and 8px control-group rhythm, with 4px metadata separation. The title reserves Close space using `controlHeightLG + paddingXS` and a `controlHeightLG` minimum height. The established desktop preview is 90vw capped at 1440px, with a body bounded to `min(80dvh, 900px)`; content scrolls inside it. Below 768px the existing full-viewport Modal uses a 12px inset and 40px minimum control/Close targets. Card actions also retain 40px minimum mobile targets. Source with Wrap lines off scrolls horizontally inside the preview.

Palette, body/type hierarchy, shape, native focus treatment and depth continue to come from `antdTheme`, resolved Ant Design tokens and existing product CSS. No custom font, accent, shadow or animation was added for tenant Artifacts.

## Current reads and recovery

Before metadata resolves, the card is busy and says Preparing file…. It exposes Preview/Download only after authorized metadata resolves. Missing or failed metadata says File unavailable and offers explicit Retry when a Session is available. A recoverable download failure retains the readable filename and persistent Download failed. Try again. status; the download control becomes Retry download. Concurrent download is disabled while busy.

Definitive tenant download read loss (401/403/404) clears card metadata and file actions. Shared preview read loss calls the same card owner after clearing protected preview metadata/content; retiring the file reference unmounts its preview. Recovery is an explicit current read, never an automatic download replay. Scope/identity changes and unmount cancel obsolete requests. Normal native Close/Escape restores the surviving Preview opener; after revoked access removes that opener, this brief makes no exact-opener restoration claim.

Tenant metadata reads bypass the legacy browser-wide Artifact metadata cache. The explicit tenant/Session/Artifact reference flows through metadata, content, preview-options and Office requests using the identity transport; it never falls back to the legacy owner transport. `fileSource` owns source addressing, metadata/byte/hash validation and original downloads. `previewOptions` reads tenant policy per preview owner. `officeSource` uses that same source transport and existing Office projection. Existing source-specific consumers retain their own identity and transport bounds.

Core authorization and delivery filtering remain server-owned: actual owned source/current executor policy precede reference mapping, and current recipient Session/Artifact access independently gates live/history delivery. Publication authority or an exceptional publication grant supplies no read authority. Authorized metadata supplies the visible filename; model-provided labels do not. Unsupported references retain the established fallback, while unreadable recipient cards are omitted. Exact durable block credit remains the existing receipt owner's responsibility; it does not imply heard credit.

## Evidence and limits

The [focused verification report](../../docs/reports/tenant-artifact-delivery-verification-2026-10-19.md) separates C# live publication/receipt evidence from the browser's already-acknowledged history journey. The browser exercised authorized card → tenant Preview → Rendered/Source → Escape with exact-opener restoration → original download matching all 1,784 UTF-8 bytes. Rendered Markdown removed executable HTML; Source retained literal script text without script elements. After exact read revocation, Download returned 403, removed private filename/actions and left File unavailable. Explicit Retry remained denied; a Ready history reload retained surrounding conversation text and omitted the private card.

The implementation owner and fresh finish reviewer inspected all thirteen current captures in `.impeccable/review/artifact-delivery/`: card and preview at 1440/1024/768/390/320px, plus Source, read loss and revoked Ready history at 320px. They reported no document overflow or preview-title/Close overlap; at 320px the title ended at x260 and the 40px Close control began at x268. The finish verdict was SHIP with no material fixes across persistence, fidelity, ceiling, material fixes and keep. The final shared preview detector scan was clean. This documentation pass checked source, authority, incumbent briefs and the verification record; it did not rerun the app or detector.

Loading and recoverable download-error presentation are source-recorded states without dedicated captures in this set. The report's Office checks establish scoped DOCX/XLSX projections, not every renderer feature. Initial live JavaScript publication, rich-receipt application-host restart, real providers and native-device accessibility are not established by this browser journey. CI was not requested or run; no milestone or full-proposal acceptance is claimed.

Pre-existing DESIGN/sidecar fingerprint drift remains reported and unrepaired. This narrow brief does not regenerate or change DESIGN.md, PRODUCT.md or `.impeccable/design.json`, and does not canonize that tooling drift as a visual rule.
