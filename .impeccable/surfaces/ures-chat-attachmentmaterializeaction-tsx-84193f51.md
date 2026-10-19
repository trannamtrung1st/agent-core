---
version: 1
slug: "ures-chat-attachmentmaterializeaction-tsx-84193f51"
primary_target: "web/src/features/chat/AttachmentMaterializeAction.tsx"
related_targets: ["web/src/features/chat/TenantSessionFilesDrawer.tsx","web/src/features/chat/SessionArtifacts.tsx","web/src/features/chat/AttachmentPreview.tsx"]
---

# Attachment copy in tenant Chat

Mode: Operate. Extend the incumbent Ant Design Chat file card and Session file inspector. Canonical behavior is docs/19; no visual-world change.

## Direction contract

THESIS: Copy an exact original into current Session files with explicit consent and truthful recovery. Reuse the file inspector rather than creating another file manager.
OWN-WORLD: Existing dark Ant Design, blue primary copy action, semantic success/warning feedback, native Tabs/Modal/Drawer, system type and shared compact/default/section gaps.
STORY: Open a file action, inspect its current source, confirm two copies, inspect current working files and Artifacts. Uncertain results require explicit reload; never repeat an effect automatically.
FIRST VIEWPORT: The bounded Session file Drawer shows source and copy action first, then independently authorized Working files and Artifacts tabs. Long names/outcomes wrap at 390/320; primary confirmation remains keyboard reachable. Closing returns focus to the source action.
FORM: Existing Session file inspector extension, code-led. No seed tournament: pinned incumbent Operate composition. Signature interaction is known success versus locked partial/unknown recovery, followed by a deliberate reload and new confirmation.
FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance
