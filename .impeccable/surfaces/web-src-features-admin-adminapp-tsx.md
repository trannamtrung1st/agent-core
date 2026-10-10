---
version: 1
slug: "web-src-features-admin-adminapp-tsx"
primary_target: "web/src/features/admin/AdminApp.tsx"
related_targets:
  - web/src/features/admin/AgentConfigurationLayout.tsx
  - web/src/features/admin/ResourceImportFields.tsx
  - web/src/features/admin/ResourceSelectionToolbar.tsx
  - web/src/features/admin/InstanceSettingsSection.tsx
  - web/src/features/admin/InstanceResourcesSection.tsx
  - web/src/features/admin/ExecutionBudgetsSection.tsx
  - web/src/features/admin/HarnessManagementSection.tsx
  - web/src/features/admin/DefinitionVersionsTable.tsx
  - web/src/features/admin/InstanceContinuitySection.tsx
  - web/src/features/admin/InstanceActivitySection.tsx
  - web/src/features/admin/InstanceRunsSection.tsx
  - web/src/features/admin/useActivitySearch.ts
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
  - web/src/features/admin/EventsSection.tsx
  - web/src/features/admin/WebhookRequestExample.tsx
  - web/src/features/admin/AdminCollectionToolbar.tsx
  - web/src/features/admin/useAdminDetailLayout.ts
  - web/src/app/confirmAction.ts
  - web/src/app.css
---

# Admin configuration and continuity

Mode: Operate. Preserve shipped dark Ant Design v6 Admin panels.

THESIS: Make configuration, authority and continuity easy to inspect with compact collections and aligned details. Teaching happens in Chat; harness policy and freeze stay clear in Admin.

OWN-WORLD: Existing Admin panel insets, direct Ant Design controls, token spacing and shared confirmation; `/docs` owns behavior.

STORY: Browse Definitions, Instances and global Connections → Credentials / Events. Instance tabs are Identity & version, Skills & resources, Continuity, Automation, Activity, Credentials and Effective configuration. Identity & version contains Profile, Settings and Workspace; Skills & resources has Skills and Resources, each separating Definition and Instance ownership; Continuity contains Memory and Experience; Automation contains Triggers and Policies & models; Activity contains Sessions (default) and independent Runs. Labels preserve existing URL-backed keys. Teaching happens in Chat; Admin retains governance, inspection and explicit owner changes.

FIRST VIEWPORT: Shared inventory/search/pagination and instance panel insets. Automations summarizes Name, Destination, When, Status, Next run, Last run, Model and Origin. Activity Sessions uses meaningful titles and secondary copyable IDs, origin, lifecycle and last activity. The independent Runs collection shows readable activation, secondary Run/Session IDs, Created, Status and Updated; it opens the exact AgentRun in shared Run details rather than showing the old Automation/Instructions columns.

FORM: Prefer compact single-line summaries with full-text titles. Skills, versions, Credentials and Workspace reserve sufficient action-column width and use the shared admin-table-actions group with paddingXS gap, no wrapping and no button shrink. Events keeps View and More actions inline in a 130px action column with 1040px local table scrolling; secondary operations use the direct Ant Design Dropdown. Active subscribers and Total subscriptions are separate right-aligned counts. Narrow tables scroll locally; ordinary long cell text wraps within its column. Skill summaries ellipsize on desktop; mobile keeps metadata in the Skill cell and Skill ID separate. Forms, expanded details and drawer actions wrap. Shared detail labels align at 12rem at 768px+ and stack below. Admin owns the 40px mobile button minimum. Panels and toolbars each own their insets once.

DETAILS: New/Edit automation uses the 640px maximum drawer, full viewport below 768px, with scrolling form and persistent footer. Automation summary has 8px line gaps and a 12px gap to the content-width Save row; it presents trigger/bounds, destination, requested report and disabled/incomplete guidance. Event details separate received signals from deliveries, and the tabbed JSON/cURL example provides selectable text, a placeholder credential and inline clipboard recovery. Capability overview retains Authorized / Always selected counts without a duplicate capability disclosure. Knowledge source and saved scenario counts sit beside their headings with the shared compact gap. Supporting copy distinguishes Core-managed browser bootstrap and active Skill requirements from explicit Always selections; this summary never grants capabilities. Activity reuses the Admin toolbar/table and cursor footer with 8px row gaps. Background Work, Session and Run details use the same width and 16px body inset. Exact Session inspection gates actions on the authoritative detail read, uses Ant Design Spin while loading and one inline Alert with explicit retry on failure; stale list rows do not hide failure. Search is limited to loaded pages. Deep links and return navigation are specified in docs/13-frontend-implementation-spec.md. AgentRunDetails adds no catalog-row padding or second outer inset. Outcome, confirmed effects and approval preview use open labeled, focusable regions capped at clamp(8rem, 24dvh, 16rem); compact direct Ant Design Descriptions shows Model, Attempt and Updated. Cancel stays content-width. Automation schedule-expression insets retain 12px inner padding. Version inspection remains a 52rem maximum drawer. Skill authoring/inspection reuses the shared Skill drawer. Preserve drafts, revisioned operations, confirmation, safe diagnostics, source navigation and focus return as specified in docs/13-frontend-implementation-spec.md.

FINISH: Bounded desktop/768/390 verification includes long summaries, table containment, inline button alignment, search/no-match recovery, expansion, Run/source inspection, drawer dismissal, confirmation cancellation and retained error drafts. Compare shared spacing and detail edges; preserve product contracts. Documentation-only sync uses source and link checks, without rerunning the application.

EVIDENCE: Chat/Admin shared detail edges and long content were checked through actual component fixtures at 1440/768/390px. Local shared runtime and component regressions pass; historical hosted cutover acceptance passed on 8cec78c5d47a43e0236a5c38f2e312f4e36ce283; see docs/reports/activation-agent-run-background-sessions-verification.md. Subsequent Events, Automation summary, navigation and capability disclosure checks at 1440/768/390px are recorded in docs/reports/admin-ui-feedback-verification.md; no hosted acceptance is renewed by this documentation sync.

DESTINATIONS: Reuse AutomationDestination and CompletionDeliveryStatus across Chat, Admin and Background Work. Exact conversation links disambiguate execution and reporting; creation provenance remains separate. Admin conditionally shows eligible owned conversation selection or unattended model plus report controls. Parent completion stays a normal message with modest child provenance. Pending/admitted does not mean reported; quiet/unavailable results remain inspectable. See docs/reports/automation-targets-background-reportback-verification.md for the bounded 1440/768/390 Synthetic runtime review. Prior hosted evidence remains historical.


WAIT: Shared AgentRunDetails displays typed Waiting separately from approval/retry, with deadline and owned child links. Waiting remains active in Automation/Run polling and exposes existing Cancel. Result handling links open the exact successful parent Run. No new collection or design system is introduced; inherited drawers are confirmed at desktop/tablet/mobile widths.


### Core Event preset and filter authoring

The existing Instance Automation drawer owns disabled Custom/preset drafts with Schedule or Events mode. Independent collapsible Event rows use stable identities, source type/key, current eligibility, filter/sample tests and dispatch; one grouped searchable picker discovers Built-in and Webhook definitions. Global Events shows safe code-owned Built-in definitions read-only alongside managed Webhooks; private activity stays Instance owned. Preserve separate unsaved mode branches and per-row samples, nested creation into the initiating row, shared Ant Design v6 controls, 640px/full-mobile drawer, reachable footer and focus return. Partial catalog failures retain successful definitions and draft state with retry. Product behavior is owned by docs/13 and docs/14; this is presentation context only.

## Scoped Instance extension (2026-10-10)

Identity & version owns Settings sections and the existing Execution budgets form. Skills & resources keeps Skills as default and adds Resources. The two resource panels reuse collection toolbar/table/quiet metadata and source labels. Settings use an explicit Customize action for inherited values, compact Save/Discard/Reset with section-specific accessible names, unsaved header status and per-field provenance/reset. Discard exits unsaved customization; Reset confirms clearing saved section overrides and local edits. The bounded form pairs related short fields, stacks below 768px, limits short control widths and aligns switches beside labels; operating instructions keep quiet full-replacement copy. Panels own outer inset, stacks own token gaps and controls own internal padding. Drawer width is 640px desktop and full mobile; tables scroll locally; filters/actions wrap without page overflow. Current Run unchanged and next Run copy is precise. No theme palette/token changes.

Initial rendered evidence: `local/verification/scoped-resources/{resources,settings,skills,drawer}-{1440,768,390}.png`, from the running disposable Synthetic app. Settings shows saved 2048 token override; resource catalog and inspection show a real Synthetic uploaded text file and exact preview. Budgets remain class-based beneath Settings. Review disposition and functional regression closure belong to the new scoped configuration report; these captures do not establish full milestone acceptance.

## Shared configuration composition sync (2026-10-10)

Definition drafts, immutable versions and Instances share Identity → Profile / Settings and Skills & resources → Skills / Resources. Instance Identity also includes Workspace. Definition-only Capabilities and Test & Publish remain separate. `AgentConfigurationPanel` owns the surface, one 16px heading/body inset, 8px heading gap and wrapping heading actions; optional body stacks own 8/12/16px gaps and clear direct Typography margins. Tables and forms that already own layout omit the body stack. Forms remain bounded to 48rem inside full-width panels; related fields use two columns at 768px+ and stack below. Settings retains the existing expandable Execution budgets, initially open with inline attention markers.

Resources reuse the compact selection toolbar, bounded confirmation path list and shared import fields. Import tables have one heading row and centered cells on wide layouts, stacked rows below 768px; Add Instance resources allows 880px, while edit/inspection retains 640px, each capped to the viewport. These are product compositions of direct Ant Design controls; commands, revisions, validation and drafts remain in their feature owners.

See docs/reports/shared-admin-compositions-verification.md, docs/reports/definition-admin-ux-consistency-verification.md, docs/reports/instance-resource-preview-polish-verification.md and docs/reports/resource-management-enhancement-verification.md for bounded runtime evidence and exact test limitations. Current spacing/overflow checks include 1440/768/767/390px. This documentation sync updates DESIGN.md and the preview together; it does not establish new hosted acceptance.
