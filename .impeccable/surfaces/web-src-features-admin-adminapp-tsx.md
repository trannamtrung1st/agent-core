---
version: 1
slug: "web-src-features-admin-adminapp-tsx"
primary_target: "web/src/features/admin/AdminApp.tsx"
related_targets:
  - web/src/features/admin/HarnessManagementSection.tsx
  - web/src/features/admin/DefinitionVersionsTable.tsx
  - web/src/features/admin/InstanceContinuitySection.tsx
  - web/src/features/admin/InstanceSchedulesSection.tsx
  - web/src/features/admin/instanceMemoryAutomation.tsx
  - web/src/features/admin/definitionDraftPublishGatePanel.tsx
  - web/src/features/admin/AdminCollectionToolbar.tsx
  - web/src/features/admin/useAdminDetailLayout.ts
  - web/src/app/confirmAction.ts
  - web/src/app.css
---

# Admin configuration and continuity

Mode: Operate. Preserve shipped dark Ant Design v6 Admin panels.

THESIS: Make configuration, authority and continuity easy to inspect with compact collections and aligned details. Teaching happens in Chat; harness policy and freeze stay clear in Admin.

OWN-WORLD: Existing Admin panel insets, direct Ant Design controls, token spacing and shared confirmation; `/docs` owns behavior.

STORY: Browse Definitions, Instances and Event sources; inspect immutable versions or open an instance's Identity & version, Behavior & continuity, Connections, Memory & automation and Effective configuration tabs. Configure Manual/Assisted/Managed and allowed areas, inspect recent changes/evidence and freeze. No required harness preparation/source/eligible/publish sequence.

FIRST VIEWPORT: Inventory collections use the available width and shared search/pagination. Instance identity/version leads its tab workspace. Behavior & continuity contains Harness management, Experience, Scheduled work and Initiative. Active version and mode lead the short harness policy form; detailed recent evidence is collapsed. Freeze remains separately named.

FORM: Use existing dark Ant Design panels and token gaps. Collection summaries stay on one line with ellipsis and table-local horizontal scrolling. Experience, Schedule and Thought links expand one record at a time; shared detail labels align in a 12rem top-aligned column at 768px+ and stack below 768px. Expanded content stays within the visible table width. Version details use the existing right-side drawer, full-width on mobile, with focus returned on close. Wrap scopes/actions, observations and evidence; retain separate observation bullets, 40px narrow table actions and 72ch evidence measure. Label Core checks, partial Agent assessments, tested revisions and external limitations. Preserve safe diagnostics and publication readback after freeze.

FINISH: Bounded desktop/768/390 inspection and one correction/confirmation pass. Verify search, pagination, expansion and no-match recovery, revisioned operations, confirmation cancellation/focus return and retained drafts after errors. Compare shared detail label edges across representative consumers and verify no document overflow with long content. No new UI kit or spacing system. `/docs` remains authoritative for behavior.
