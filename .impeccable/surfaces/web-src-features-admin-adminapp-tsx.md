---
version: 1
slug: "web-src-features-admin-adminapp-tsx"
primary_target: "web/src/features/admin/AdminApp.tsx"
related_targets:
  - web/src/features/admin/AdminShell.tsx
  - web/src/features/admin/adminNavigation.ts
  - web/src/features/admin/InstanceDetail.tsx
  - web/src/features/admin/InstanceProfile.tsx
  - web/src/features/admin/InstanceOverview.tsx
  - web/src/features/admin/DefinitionDetail.tsx
  - web/src/features/admin/DefinitionDraftWorkspace.tsx
  - web/src/features/admin/AgentConfigurationLayout.tsx
  - web/src/features/admin/ResourceImportFields.tsx
  - web/src/features/admin/ResourceSelectionToolbar.tsx
  - web/src/features/admin/useInstanceSettingsController.ts
  - web/src/features/admin/InstanceSettingsSection.tsx
  - web/src/features/admin/InstanceResourcesSection.tsx
  - web/src/features/admin/ExecutionBudgetsSection.tsx
  - web/src/features/admin/AutomationTriggerPermissions.tsx
  - web/src/features/admin/definitionCandidateEditor.tsx
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

Definition Automation permissions use two primary checkboxes, Schedule and Events, with Built-in/Webhook children indented by Ant Design paddingLG. Checked/mixed/unchecked state derives from canonical child values. Concise descriptions explain explicit both-family enable and local restricted pause/resume; overall-disabled configuration is visible with inactive-authority guidance. Published inspection uses the same disabled controls. Keep ordinary text wrapping, shared section spacing and keyboard focus; no extra boxes or new tokens. Effective/Instance summaries and publish diffs name exact family grants. `/docs/13` owns behavior and canonical mappings.

Mode: Operate. Preserve shipped dark Ant Design v6 Admin panels.

THESIS: Make configuration, authority and continuity easy to inspect with compact collections and aligned details. Teaching happens in Chat; harness policy and freeze stay clear in Admin.

OWN-WORLD: Existing Admin panel insets, direct Ant Design controls, token spacing and shared confirmation; `/docs` owns behavior.

STORY: Open Admin at the Instances inventory. The grouped global rail exposes Instances, Definitions, Credentials, Events and Host Browser privacy. Selecting an Instance or exact Definition draft replaces the rail with contextual pages and explicit collection/back paths. Profile, Behavior, Models, Skills, Resources, Workspace, Access, Self-management, Automations, Memory, Experience, Sessions, Runs and Effective have clear edit/inspect owners. Definition lifecycle uses Versions/Drafts; the exact revisioned draft has Overview, Identity & goals, Behavior, Models, Skills, Resources, Capabilities, Policies, Test & Publish and Advanced JSON. Native links and one-way legacy replacement retain source/history semantics. Chat is a fresh root; Return to last chat is the exact saved conversation.

FIRST VIEWPORT: A shared header carries breadcrumb, Search Admin and Chat/Return. At 1024px+ the 248px container-tone sidebar scrolls independently of the main work area. Below 1024px the labeled opener shows an Ant Design navigation Drawer; links remain labeled, current and keyboard accessible. Owner name, lifecycle and Definition version remain readable above the selected content. Overview uses saved source-aware summary and precise links, with no invented metrics. Collection tables retain full available width, local scrolling and their existing search/sort/filter/pagination. One page heading replaces nested page-wide tab bars.

FORM: Each Instance API section has one edit home and independent sparse patches/revisions. Behavior owns instructions/conversation/behavior/initiative/voice; Models owns interactive defaults/provider preferences/budgets; Access owns capabilities plus separate credential bindings; Automations owns restrictions and unattended defaults; Memory owns memory policy and learned records. Self-management owns freeze/authority/reviewed shared changes. The exact-owner controller and lazy retained pages preserve drafts, failed saves and Form/JSON text across navigation. Owner departure offers Stay / Discard and leave, including native history; protected input never enters browser storage. Registry destinations/approved field aliases power local Search Admin and stable anchors. Forms stay bounded to 48rem except full-width instructions; shared 8/12/16 rhythm, two-column/stacked field grid, provenance/unsaved text and content-width actions remain.

DETAILS: Global Browser privacy is the single host-policy editor with deployment ceiling, saved/effective revision, persistence/restart notice and explicit Unmasked confidentiality confirmation. Instance Effective retains read-only provider diagnostics and a global link. Original Session and independent Run inspection/source/back/focus remain intact. Automation keeps its 640px/full-mobile drawer, persistent trigger/destination/report summary and revisioned actions. Resource/Skill owners retain import, projection, scope, safety and inspectors. Definition draft shares one candidate and sticky Save/Publish/Delete bar; invalid JSON and stale evaluation evidence remain blockers. Published versions keep their 52rem/full-mobile read-only drawer. Archived operations recover to Profile with an explanation; owner read failures stay local with retry and stale-response fences.

FINISH: Inspect one batched rendered 1440/1024/768/767/390/320 set with desktop/mobile navigation, long owner names/instructions, Skills/Resources, error/empty/invalid states and zero document overflow. Check shared panel insets, aligned fields, table-local scrolling, sticky clearance, keyboard headings/anchors, Escape and focus return. Fix the batched findings and perform at most one visual confirmation round. Functional runtime regressions continue independently. Record current-source captures/actions/counts and unrun/hosted limits in docs/reports/admin-information-architecture-reorganization-verification.md; historical evidence below stays historical.

EVIDENCE: Chat/Admin shared detail edges and long content were checked through actual component fixtures at 1440/768/390px. Local shared runtime and component regressions pass; historical hosted cutover acceptance passed on 8cec78c5d47a43e0236a5c38f2e312f4e36ce283; see docs/reports/activation-agent-run-background-sessions-verification.md. Subsequent Events, Automation summary, navigation and capability disclosure checks at 1440/768/390px are recorded in docs/reports/admin-ui-feedback-verification.md; no hosted acceptance is renewed by this documentation sync.

DESTINATIONS: Reuse AutomationDestination and CompletionDeliveryStatus across Chat, Admin and Background Work. Exact conversation links disambiguate execution and reporting; creation provenance remains separate. Admin conditionally shows eligible owned conversation selection or unattended model plus report controls. Parent completion stays a normal message with modest child provenance. Pending/admitted does not mean reported; quiet/unavailable results remain inspectable. See docs/reports/automation-targets-background-reportback-verification.md for the bounded 1440/768/390 Synthetic runtime review. Prior hosted evidence remains historical.


WAIT: Shared AgentRunDetails displays typed Waiting separately from approval/retry, with deadline and owned child links. Waiting remains active in Automation/Run polling and exposes existing Cancel. Result handling links open the exact successful parent Run. No new collection or design system is introduced; inherited drawers are confirmed at desktop/tablet/mobile widths.


### Built-in Event presets and filters

The existing Instance Automation drawer owns disabled Custom/preset drafts with Schedule or Events mode. Independent collapsible Event rows use stable identities, source type/key, current eligibility, filter/sample tests and dispatch; one grouped searchable picker discovers Built-in and Webhook definitions. Global Events shows safe code-owned Built-in definitions read-only alongside managed Webhooks; private activity stays Instance owned. Preserve separate unsaved mode branches and per-row samples, nested creation into the initiating row, shared Ant Design v6 controls, 640px/full-mobile drawer, reachable footer and focus return. Partial catalog failures retain successful definitions and draft state with retry. Product behavior is owned by docs/13 and docs/14; this is presentation context only.

## Historical Scoped Instance extension (2026-10-10)

Identity & version owns Settings sections and the existing Execution budgets form. Skills & resources keeps Skills as default and adds Resources. The two resource panels reuse collection toolbar/table/quiet metadata and source labels. Settings use an explicit Customize action for inherited values, compact Save/Discard/Reset with section-specific accessible names, unsaved header status and per-field provenance/reset. Discard exits unsaved customization; Reset confirms clearing saved section overrides and local edits. The bounded form pairs related short fields, stacks below 768px, limits short control widths and aligns switches beside labels; operating instructions keep quiet full-replacement copy. Panels own outer inset, stacks own token gaps and controls own internal padding. Drawer width is 640px desktop and full mobile; tables scroll locally; filters/actions wrap without page overflow. Current Run unchanged and next Run copy is precise. No theme palette/token changes.

Initial rendered evidence: `local/verification/scoped-resources/{resources,settings,skills,drawer}-{1440,768,390}.png`, from the running disposable Synthetic app. Settings shows saved 2048 token override; resource catalog and inspection show a real Synthetic uploaded text file and exact preview. Budgets remain class-based beneath Settings. Review disposition and functional regression closure belong to the new scoped configuration report; these captures do not establish full milestone acceptance.

## Historical Shared configuration composition sync (2026-10-10)

Definition drafts, immutable versions and Instances share Identity → Profile / Settings and Skills & resources → Skills / Resources. Instance Identity also includes Workspace. Definition-only Capabilities and Test & Publish remain separate. `AgentConfigurationPanel` owns the surface, one 16px heading/body inset, 8px heading gap and wrapping heading actions; optional body stacks own 8/12/16px gaps and clear direct Typography margins. Tables and forms that already own layout omit the body stack. Forms remain bounded to 48rem inside full-width panels; related fields use two columns at 768px+ and stack below. Settings retains the existing expandable Execution budgets, initially open with inline attention markers.

Resources reuse the compact selection toolbar, bounded confirmation path list and shared import fields. Import tables have one heading row and centered cells on wide layouts, stacked rows below 768px; Add Instance resources allows 880px, while edit/inspection retains 640px, each capped to the viewport. These are product compositions of direct Ant Design controls; commands, revisions, validation and drafts remain in their feature owners.

See docs/reports/shared-admin-compositions-verification.md, docs/reports/definition-admin-ux-consistency-verification.md, docs/reports/instance-resource-preview-polish-verification.md and docs/reports/resource-management-enhancement-verification.md for bounded runtime evidence and exact test limitations. Current spacing/overflow checks include 1440/768/767/390px. This documentation sync updates DESIGN.md and the preview together; it does not establish new hosted acceptance.

## Historical Browser privacy and Built-in table consistency (2026-10-10)

Current source baseline: `9145e66e`. Preserve the refactored branch compositions when integrating other work. Browser provider effective/saved Descriptions remain full-width with shared responsive labels. The nested screenshot privacy editor uses `admin-config-form`: 48rem maximum, 16px field spacing, 72ch supporting copy and wrapping content-width Save/Reload actions. Its 12px stack adds no second outer inset. Unmasked review keeps the shared centered confirmation and inline failure/retry.

Built-in subscriber inspection uses an 8px heading/selector/table stack, compact collection table, 500px local scroll, 280px ellipsis name and 220px wrapping state columns. Full accessible names and titles accompany shortened display text. Delivery diagnostics use the same wrapping/tabular-numeral owner and 650px local scroll. Existing 640px/full-mobile drawers and 40px mobile controls apply to both. Exact owned navigation, stale-read guards and recovery remain feature behavior under docs/13.

The main UI consistency report records Synthetic interactions and computed widths/insets at 1440/768/767/390px, 40 focused component tests and 13 browser checks. This context sync reuses that evidence; it does not rerun the application or renew hosted acceptance.

## Heading metadata sync (2026-10-10)

Inventory/draft counts, draft title/ID, Persona revision, Lifecycle status, Settings markers and collapsed attention labels follow their titles in a left-aligned wrapping row with an 8px compact gap. Draft revision/update remain below the title/ID. Heading actions and numeric table alignment retain their separate owners. See [local verification](../../docs/reports/heading-metadata-layout-verification.md): live Admin checks at 1440/768/767/390px; chat drawer component coverage only. This sync adds no runtime run or hosted acceptance.

Operating instructions width refinement: reuse `admin-settings-instructions` to fill the section in Definition drafts, immutable inspection and Instance settings; exclude its textarea from the short-field cap. Other forms retain 48rem and compact controls. Verify saved long text and no horizontal page overflow across wide/mobile layouts.

## Historical Agent self-management (2026-10-11)

Identity & version → Settings contains the single management panel, above existing typed settings. Off/Assisted/Managed, inherited areas and automatic allowances use the shared bounded panel/forms; Freeze/Admin unfreeze uses existing confirmation and diagnostics. Preserve conflict drafts until explicit refresh. Definition Settings exposes published defaults through the same composition. Built-in management Skills are read-only OnDemand rows and inspections in the existing collection, with stored choice/source and effective master suppression. Automation retains execution defaults and ordinary registrations; Harness shared promotion/evidence stays separate from the master authority controls. Next Run means a fresh Run in the same Session.
