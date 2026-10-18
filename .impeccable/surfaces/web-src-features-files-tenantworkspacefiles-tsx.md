---
version: 1
slug: "web-src-features-files-tenantworkspacefiles-tsx"
primary_target: "web/src/features/files/TenantWorkspaceFiles.tsx"
related_targets: ["web/src/features/admin/InstanceWorkspaceSection.tsx","web/src/features/files/WorkspaceFileTable.tsx","web/src/features/access/AccessApp.tsx","web/src/features/chat/ChatApp.tsx","web/src/features/filePreview/fileSource.ts"]
---

# Tenant workspace file browser extension

Mode: Operate. Extend the incumbent Instance file list and Session inspector within the current Ant Design world. Audience: people managing retained agent files under independent current permissions.

## Direction contract

THESIS: Make current home and working files manageable without confusing file ownership, read access and mutation authority. Reuse the existing file-browser composition.

OWN-WORLD: Preserve dark Ant Design v6, native controls, shared AdminShell/Session inspector, 8/12/16 spacing, responsive file rows and existing file preview.

STORY: Open an actual workspace, inspect readable current paths, preview/download, or deliberately upload/delete. Write-only users supply a known path without discovering private filenames. Reload is explicit after uncertain or stale effects; supplied drafts survive.

FIRST VIEWPORT: Workspace ownership and current access guidance, wrapping Reload/Upload controls, folder breadcrumb, search and native file table. Mobile stacks path/type/size beside compact actions. The upload editor retains the native bounded Drawer and unsaved guard.

FORM: Existing file browser extension, code-led. Seed key: not applicable; no new visual world or concept tournament. Home lives in Instance navigation; working files use the Session inspector.

FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance


## Finish evidence

Fresh finish review: ship, all 16 required captures valid, no material fixes. The fresh documenter compared shared source and seven captures against the incumbent Design/sidecar and preserved both; historical fingerprint drift is recorded without repair. [Durable verification and both handoffs](../../docs/reports/tenant-workspace-ui-verification-2026-10-19.md) own scoped evidence. No shipping product raster was added. The full proposal remains active.
