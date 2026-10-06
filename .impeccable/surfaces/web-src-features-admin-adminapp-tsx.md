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
  - web/src/features/admin/AdminSessionPicker.tsx
  - web/src/features/admin/ApplicationConnectionSection.tsx
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

STORY: Browse Definitions, Instances and Event sources; inspect immutable versions or open an instance's Identity & version, Continuity, Automation, Runs, Connections and Effective configuration tabs. Configure Manual/Assisted/Managed and allowed areas, inspect recent changes/evidence and freeze. No required harness preparation/source/eligible/publish sequence.

FIRST VIEWPORT: Inventory collections use the available width and shared search/pagination. Instance identity/version leads its tab workspace. Continuity contains Memory and Experience. Automation contains Schedules, Thoughts and Policies & models, including Harness management. Runs uses a compact Ant Design table with Type, Task / prompt, Run ID, Status/attention and Updated columns. Whole-row clicks and the keyboard-accessible Run ID open Run details in place. Active version and mode lead the short harness policy form; detailed recent evidence is collapsed. Freeze remains separately named.

FORM: Use existing dark Ant Design panels and token gaps. Form actions keep content width in wrapping rows; fields may fill the container. The Admin content container owns the 40px mobile button minimum, without feature-specific width exceptions. Collection summaries stay on one line with ellipsis and table-local horizontal scrolling. Runs Task / prompt and Run ID use column ellipsis with native titles for the full text. Experience, Schedule and Thought links expand one record at a time; shared detail labels align in a 12rem top-aligned column at 768px+ and stack below 768px. Expanded content stays within the visible table width. Version details use the existing right-side drawer, full-width on mobile, with focus returned on close. Run details uses a shared right-side drawer, at most 640px wide and full-width on narrow viewports. Its execution composition shows the full task or thinking prompt alongside model, progress, results, safe errors, approval and cancellation; long content and actions wrap. Drawer source controls retain a 40px minimum height below 768px. Wrap scopes/actions, observations and evidence; retain separate observation bullets, 40px narrow table actions and 72ch evidence measure. Label Core checks, partial Agent assessments, tested revisions and external limitations. Preserve safe diagnostics and publication readback after freeze.

ADMIN POLISH: Policies & models uses existing sibling Admin panels for Harness management, Execution defaults and Advanced registrations. Bound record-editing forms to 48rem; selects fill their field columns, while short timing/unit controls, scope filters and table/toolbar choices intentionally stay compact. Leading panel messages use equal body insets. Label model fields and keep Save/Freeze in the same wrapping row. Preserve full-width evidence and table inspection. Collection introductions and creation actions match the active tab. Draft actions remain sticky above editor tabs; focused fields clear the bar. Memory and Experience share conversation browsing by title/date with manual Session ID entry and adjacent refresh/paging/retry. IdentityUser/User scope and actions share a compact control baseline and wrap on narrow screens. Session scope/source labels align at the top with actions below the conversation picker. Schedules use direct Ant Design date/time pickers, explicit viewer-zone text and readable previews; recurring end dates remain date-only. Runs displays the owner-authored Schedule task or Thought thinking prompt pinned for that execution; later registration edits do not change historical rows. Schedule View last run, Thought View run and Experience View generation run open the exact execution in Run details while retaining the current page, expanded source and unsaved drafts, including executions beyond the first Runs page. Close, Escape and mask dismissal return focus to the initiating row or control. Source controls close the drawer and navigate to the exact expanded and focused Schedule, Thought, Event subscription or Experience checkpoint, resetting source browsing without resetting drafts. Inline read failures expose Retry and diagnostics without a false empty state. Clipboard recovery stays inside the one-time credential dialog. Visited tabs preserve drafts while hidden polling pauses. `/docs/13-frontend-implementation-spec.md` owns these behaviors.

FINISH: Bounded desktop/768/390 inspection and one correction/confirmation pass. Verify whole-row clicks, keyboard-accessible Run ID and exact Schedule/Thought/Experience run links open the same drawer in place, including executions beyond the first Runs page; check Close, Escape and mask focus return. Confirm historical Task / prompt survives later registration edits, prompt/ID column ellipsis exposes native titles, full intent appears in the drawer and narrow tables scroll locally. Verify exact Schedule/Thought/Event/Experience source navigation in both directions, preserving drafts and restoring focus to the source. Verify search, pagination, expansion and no-match recovery, revisioned operations, confirmation cancellation/focus return and retained drafts after errors. Compare shared detail label edges across representative consumers and verify no document overflow with long content. Check sticky actions at 1440/768/390, the 640px maximum/full-mobile drawer and 40px mobile source controls, source browsing and manual entry in both consumers, local-time schedule saving, failed-read retry, clipboard recovery and absence of hidden polling. No new UI kit or spacing system. `/docs` remains authoritative for behavior.
