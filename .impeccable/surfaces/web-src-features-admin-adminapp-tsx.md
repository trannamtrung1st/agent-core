---
version: 1
slug: "web-src-features-admin-adminapp-tsx"
primary_target: "web/src/features/admin/AdminApp.tsx"
related_targets:
  - web/src/features/admin/HarnessManagementSection.tsx
  - web/src/features/admin/DefinitionVersionsTable.tsx
  - web/src/features/admin/InstanceContinuitySection.tsx
  - web/src/features/admin/InstanceAutomationsSection.tsx
  - web/src/features/admin/InstanceSkillsSection.tsx
  - web/src/features/admin/DefinitionSkillsSection.tsx
  - web/src/features/admin/InstanceWorkspaceSection.tsx
  - web/src/features/admin/CredentialsSection.tsx
  - web/src/features/admin/instanceMemoryAutomation.tsx
  - web/src/features/admin/definitionDraftPublishGatePanel.tsx
  - web/src/features/admin/AdminSessionPicker.tsx
  - web/src/features/admin/EventSourcesSection.tsx
  - web/src/features/admin/AdminCollectionToolbar.tsx
  - web/src/features/admin/useAdminDetailLayout.ts
  - web/src/app/confirmAction.ts
  - web/src/app.css
---

# Admin configuration and continuity

Mode: Operate. Preserve shipped dark Ant Design v6 Admin panels.

THESIS: Make configuration, authority and continuity easy to inspect with compact collections and aligned details. Teaching happens in Chat; harness policy and freeze stay clear in Admin.

OWN-WORLD: Existing Admin panel insets, direct Ant Design controls, token spacing and shared confirmation; `/docs` owns behavior.

STORY: Browse Definitions, Instances, Event sources and Credentials. Instance tabs are Identity & version, Skills, Continuity, Automation, Runs, Connections and Effective configuration. Skills separates Definition and Instance ownership; Continuity contains Memory and Experience; Automation contains Automations and Policies & models; Connections contains Credentials and Event sources. Teaching happens in Chat; Admin retains governance, inspection and explicit owner changes.

FIRST VIEWPORT: Shared inventory/search/pagination and instance panel insets. Automations summarizes Name, When, Status, Next run, Last run, Model and Origin. Runs shows Automation, admitted Instructions, Run ID, Status/attention and Updated. Historical Instructions remain pinned for the admitted execution.

FORM: Prefer compact single-line summaries with full-text titles. Skills, versions, Event sources, Credentials and Workspace reserve sufficient action-column width and use the shared admin-table-actions group with paddingXS gap, no wrapping and no button shrink. Narrow tables scroll locally; ordinary long cell text wraps within its column. Skill summaries ellipsize on desktop; mobile keeps metadata in the Skill cell and Skill ID separate. Forms, expanded details and drawer actions wrap. Shared detail labels align at 12rem at 768px+ and stack below. Admin owns the 40px mobile button minimum. Panels and toolbars each own their insets once.

DETAILS: New/Edit automation uses the 640px maximum drawer, full viewport below 768px, with scrolling form and persistent footer. Background Work and Run details use the same width, 16px body inset and 12px detail inset. Instructions, approval previews and results share labeled, focusable text regions capped at clamp(8rem, 24dvh, 16rem); IDs wrap in compact metadata. Version inspection remains a 52rem maximum drawer. Skill authoring/inspection reuses the shared Skill drawer. Preserve drafts, revisioned operations, confirmation, safe diagnostics, source navigation and focus return as specified in docs/13-frontend-implementation-spec.md.

FINISH: Bounded desktop/768/390 verification includes long summaries, table containment, inline button alignment, search/no-match recovery, expansion, Run/source inspection, drawer dismissal, confirmation cancellation and retained error drafts. Compare shared spacing and detail edges; preserve product contracts. Documentation-only sync uses source and link checks, without rerunning the application.
