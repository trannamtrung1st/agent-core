---
version: 1
slug: "web-src-features-admin-adminapp-tsx"
primary_target: "web/src/features/admin/AdminApp.tsx"
related_targets:
  - web/src/features/admin/HarnessManagementSection.tsx
  - web/src/features/admin/DefinitionVersionsTable.tsx
  - web/src/features/admin/InstanceContinuitySection.tsx
  - web/src/features/chat/AgentRunDetails.tsx
  - web/src/features/chat/DrawerListFooter.tsx
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

FIRST VIEWPORT: Shared inventory/search/pagination and instance panel insets. Automations summarizes Name, Destination, When, Status, Next run, Last run, Model and Origin. The current bounded Runs collection shows Run ID, readable Turn, Status and Updated; it opens the exact AgentRun in shared Run details rather than showing the old Automation/Instructions columns.

FORM: Prefer compact single-line summaries with full-text titles. Skills, versions, Event sources, Credentials and Workspace reserve sufficient action-column width and use the shared admin-table-actions group with paddingXS gap, no wrapping and no button shrink. Narrow tables scroll locally; ordinary long cell text wraps within its column. Skill summaries ellipsize on desktop; mobile keeps metadata in the Skill cell and Skill ID separate. Forms, expanded details and drawer actions wrap. Shared detail labels align at 12rem at 768px+ and stack below. Admin owns the 40px mobile button minimum. Panels and toolbars each own their insets once.

DETAILS: New/Edit automation uses the 640px maximum drawer, full viewport below 768px, with scrolling form and persistent footer. Background Work and Run details use the same width and 16px body inset. AgentRunDetails adds no catalog-row padding or second outer inset. Outcome, confirmed effects and approval preview use open labeled, focusable regions capped at clamp(8rem, 24dvh, 16rem); compact direct Ant Design Descriptions shows Model, Attempt and Updated. Cancel stays content-width. Automation schedule-expression insets retain 12px inner padding. Version inspection remains a 52rem maximum drawer. Skill authoring/inspection reuses the shared Skill drawer. Preserve drafts, revisioned operations, confirmation, safe diagnostics, source navigation and focus return as specified in docs/13-frontend-implementation-spec.md.

FINISH: Bounded desktop/768/390 verification includes long summaries, table containment, inline button alignment, search/no-match recovery, expansion, Run/source inspection, drawer dismissal, confirmation cancellation and retained error drafts. Compare shared spacing and detail edges; preserve product contracts. Documentation-only sync uses source and link checks, without rerunning the application.

EVIDENCE: Chat/Admin shared detail edges and long content were checked through actual component fixtures at 1440/768/390px. Local shared runtime and component regressions pass; final hosted cutover acceptance passes on 8cec78c5d47a43e0236a5c38f2e312f4e36ce283; see docs/reports/activation-agent-run-background-sessions-verification.md.

DESTINATIONS: Reuse AutomationDestination and CompletionDeliveryStatus across Chat, Admin and Background Work. Exact conversation links disambiguate execution and reporting; creation provenance remains separate. Admin conditionally shows eligible owned conversation selection or unattended model plus report controls. Parent completion stays a normal message with modest child provenance. Pending/admitted does not mean reported; quiet/unavailable results remain inspectable. See docs/reports/automation-targets-background-reportback-verification.md for the bounded 1440/768/390 Synthetic runtime review. Prior hosted evidence remains historical.


WAIT: Shared AgentRunDetails displays typed Waiting separately from approval/retry, with deadline and owned child links. Waiting remains active in Automation/Run polling and exposes existing Cancel. Result handling links open the exact successful parent Run. No new collection or design system is introduced; inherited drawers are confirmed at desktop/tablet/mobile widths.
