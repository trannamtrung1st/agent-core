---
version: 1
slug: "web-src-features-admin-adminapp-tsx"
primary_target: "web/src/features/admin/AdminApp.tsx"
related_targets:
  - web/src/features/admin/AdminShell.tsx
  - web/src/features/admin/AdminNavigationIcon.tsx
  - web/src/features/admin/InstanceConfigurationActions.tsx
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
  - web/src/features/admin/AddCredentialDialog.tsx
  - web/src/features/admin/SandboxProfilesSection.tsx
  - web/src/features/admin/SandboxProfileDetails.tsx
  - web/src/features/admin/DefinitionSandboxPolicy.tsx
  - web/src/features/admin/InstanceSandboxSummary.tsx
  - web/src/features/admin/adminFormFeedback.ts
  - web/src/features/admin/adminFailure.tsx
  - web/src/features/admin/CredentialFields.tsx
  - web/src/features/admin/CredentialConnectionTag.tsx
  - web/src/features/admin/DriveFiles.tsx
  - web/src/features/admin/DriveConnectionPanel.tsx
  - web/src/features/admin/instanceMemoryAutomation.tsx
  - web/src/features/admin/definitionDraftPublishGatePanel.tsx
  - web/src/features/admin/AdminSessionPicker.tsx
  - web/src/features/admin/EventsSection.tsx
  - web/src/features/admin/WebhookRequestExample.tsx
  - web/src/features/admin/AdminCollectionToolbar.tsx
  - web/src/features/admin/useAdminDetailLayout.ts
  - web/src/app/confirmAction.ts
  - web/src/app/appFeedback.ts
  - web/src/app/appFeedbackPlacement.ts
  - web/src/features/filePreview/FilePreview.tsx
  - web/src/app.css
---

# Admin configuration and continuity

CURRENT SYNC (2026-10-15): This brief follows the current working tree based on `0c2c0f27`. Contextual feedback is committed; newer Sandbox image-help/protection copy remains uncommitted. [DESIGN.md](../../.agents/context/DESIGN.md) owns presentation guidance; [Frontend Implementation](../../docs/13-frontend-implementation-spec.md) owns behavior. The preview sidecar records working-tree source hashes. [Feedback visibility verification](../../docs/reports/feedback-visibility-verification-2026-10-15.md) retains its runtime scope; the [design-system sync record](../../docs/reports/design-system-sync-2026-10-15.md) records source alignment and the outstanding copy mismatch. This documentation-only sync adds no application run or acceptance.

Definition Automation permissions use two primary checkboxes, Schedule and Events, with Built-in/Webhook children indented by Ant Design paddingLG. Checked/mixed/unchecked state derives from canonical child values. Concise descriptions explain explicit both-family enable and local restricted pause/resume; overall-disabled configuration is visible with inactive-authority guidance. Published inspection uses the same disabled controls. Keep ordinary text wrapping, shared section spacing and keyboard focus; no extra boxes or new tokens. Effective/Instance summaries and publish diffs name exact family grants. `/docs/13` owns behavior and canonical mappings.

Mode: Operate. Preserve shipped dark Ant Design v6 Admin panels.

THESIS: Make configuration, authority and continuity easy to inspect with compact collections and aligned details. Teaching happens in Chat; harness policy and freeze stay clear in Admin.

OWN-WORLD: Existing Admin panel insets, direct Ant Design controls, token spacing and shared confirmation; `/docs` owns behavior.

STORY: Open Admin at Instances. The global rail groups Instances/Definitions, Credentials/Events and Browser privacy. An Instance or exact draft switches to focused pages with explicit collection/back paths. Configuration, governance, operations, continuity, activity and diagnostics retain separate tasks and native icons; `adminNavigation.ts` owns the complete 27 Instance / 19 draft destination set. Definition lifecycle has Versions/Drafts. Native links and one-way legacy replacement retain source/history semantics. Chat is a fresh root; Return to last chat is the saved conversation.

FIRST VIEWPORT: Agent Core with secondary Admin text leads the shared header; a visible 24px separator precedes desktop breadcrumbs. Search Admin and Chat/Return remain separate. At 1024px+ the 248px container-tone sidebar scrolls independently; below it the labeled opener shows a 280px Ant Design Drawer. Narrow headers show 40px icon controls above one scope breadcrumb. Brand gaps use native 4px only below 768px; header/action gaps use it below 360px. Navigation owns 12px outside and links 8px inside, with no second Drawer body inset; icon/label gap is 12px. Full owner titles remain available while breadcrumbs ellipsize. The 16px owner name and 12px metadata stay secondary to the 28px/24px task title. Inventories share one count/creation heading and local table scrolling.

FORM: Instructions, Conversation, Behavior, Initiative, Voice, Models, Providers and Execution budgets have independent focused edit homes. Capabilities and Credential bindings are separate; Trigger restrictions, Unattended model, Memory policy and their collections remain distinct. Self-management owns authority/freeze/reviewed changes. One exact-owner configuration provider coordinates visited persona, sparse settings, budgets, self-management and unattended drafts through global Save/Discard/Refresh. Saves pass receipt revisions sequentially, stop on error and retain remaining edits; Refresh preserves edits. Immediate inherited-field editing, explicit inheritance/reset and original archived/required-field fences remain. Minimum 32px desktop / 40px narrow label rows reserve icon space without moving controls. Collection dialogs and consequential commands retain their own mutations. Exact draft Form/JSON shares one candidate and its Save/Publish/Delete panel. Owner departure guards and native history remain; protected input never enters storage. Registry destinations and field aliases power scoped Search. Forms cap at 48rem except full-width instructions; the enclosing panel owns insets, stacks own token gaps and controls own inner padding.

DETAILS: `.admin-owner-actions` owns both sticky configuration frames: container fill, 1px border, 8px radius, one 16px inset and 8px gaps. Familiar table actions use named tooltipped icon buttons; larger sets use a native More menu with icon-before-text choices. Keyboard close returns to the surviving trigger or creation control. Browser privacy remains the single global host editor; Instance Effective is read-only. Sessions and Runs keep independent inspections. Automation retains its 640px/full-mobile drawer and persistent save summary. Resource/Skill owners retain their import/projection/safety controls. Published versions keep their 52rem/full-mobile inspector. Archived Automations/Unattended routes recover to Lifecycle; owner failures retain retry and stale-response fences. Behavior and gates stay in docs/13.

FINISH: For future UI changes, batch representative 1440/1024/768/767/390/320 views with long labels, navigation, table actions and invalid/error states. Verify one inset, stable markers, local overflow, sticky clearance and keyboard focus; keep discretionary visual rounds bounded. Current source/evidence is the focused configuration verification report. This documentation-only synchronization reuses its captures and measurements and does not rerun the application or establish hosted acceptance.

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

Email monitoring adds saved controls to expanded Credential details, preserving the incumbent Operate world. The shared collection is an inline-size container; expanded credential controls fit its visible width across 1440/768/390/320 while inventory columns scroll. New inbox Switch, three-option nonvirtual Select, wrapping Save/Cancel/Check/Reload, semantic statuses, local dates and collapsed diagnostics retain existing tokens and shared detail labels. Unsaved drafts survive status reads/conflicts; actions distinguish connect/test/observe/subscription authority. The shared Credential detail stack owns 16px section spacing. Monitoring separates connection actions with a quiet divider, pairs its 16px title with live status, groups saved-form controls at 12px and actions at 8px, and places subscriber guidance beside setup links. Reload is a text action; mobile interval/label hits use 40px minimum height. Saved feedback clears on new edits. See the monitoring verification report for actual gates.

Email/main integration keeps Monitoring inside the contextual Admin Credentials surfaces. Shared canonical route helpers own Automations and Credential bindings links; the focused Credential bindings page supersedes the earlier combined Capabilities & credentials destination. Instance monitoring drafts participate in retained workspace dirty status and departure protection. Account refreshes retain mounted details. Containment uses the available content width beside the 248px sidebar at 1024px and above, with shared navigation Drawer below. Integration captures and observed journeys are recorded in docs/reports/email-monitoring-main-integration-2026-10-13.md.
## Admin reorganization polish (2026-10-13)

The follow-up uses the same Ant Design world and shared 8/12/16 roles. One batched walkthrough covered all new global, Instance and draft destinations; mandatory functional/reflow repairs remained separate from discretionary visual iteration. The current polish report is [Admin UX polish verification](../../docs/reports/admin-ux-polish-verification.md). It records actual interactions, the six-width confirmation, test corrections and the hosted acceptance boundary; earlier reports remain historical.

Current source: `9a798360`. Matching [focused configuration verification and captures](../../docs/reports/admin-focused-configuration-verification.md) supersede the earlier combined-page/Customize presentation below historical headings; no additional runtime run or hosted acceptance is implied.

Focused configuration refinement: preserve Agent Core branding with a secondary Admin marker and desktop breadcrumb separator. Native outline icons accompany every canonical destination; remove the contextual Global integrations & platform shortcut. Separate unrelated settings into focused pages while preserving exact-owner candidates, field anchors, native links and draft guards. The sticky bordered actions frame is shared by Definition drafts and Instance configuration. Instance inherited settings accept edits immediately, retain sparse per-field source/reset semantics and reserved minimum label-row height. Common table operations use accessible icon buttons; longer choices belong to an icon-labeled More menu with keyboard focus restoration. At 320px only brand/icon-header spacing uses native paddingXXS (4px); all normal shell/control/panel roles remain 8/12/16px. `/docs/13` remains authoritative.

## Drive credentials successor (2026-10-14)

Artifact cards now use one centered horizontal row with file icon, shrinking filename/type/size and two tooltip-equipped Preview/Download icon actions. Actions never shrink, keep full accessible filenames and retain 40px mobile targets; metadata/download failures keep local Retry. Generic Artifact and Workspace rows have no provider-specific export affordance. Existing FilePreview owns the same source authentication, read-only renderer canvas and focus/cancellation.

Credentials owns one Add credential purpose selector with reused Drive/Gmail/Custom/General fields and immutable-purpose specialist edits. Saved connection Tags remain separate from Active/Disabled status. Instance bindings own app-authorized Drive browsing; conversational tools own mutations. The shared ApprovalModal uses trusted file/name/version details, a full-width narrow-screen list, action-specific Trash/Rename/Move wording, recoverable-subset copy and the existing keyboard/Reject/footer behavior. Behavior remains owned by docs/13 and docs/14.

The [successor verification ledger](../../docs/reports/drive-credentials-ux-verification-2026-10-14.md) owns current local evidence and exact source identity. Earlier reports are historical; Real Trash requires an authorized disposable account scope and remains unverified.

## P10 local sandbox extension

Platform profiles and the Session sandbox drawer preserve the incumbent Ant Design operate surface and shared Office/Drive file preview. Theme tokens own spacing; output uses Collapse/copyable text and existing confirmations. A bounded 1440/1024/768/390/320px runtime review found no horizontal overflow after transitions settled. Save/Discard/navigation protection, actual artifacts, cancellation/reset and Core restart were exercised. Canonical docs and the OpenSandbox verification ledger own scope/remaining acceptance; this is local evidence, not production hardening.

## Current Sandbox forms and submission feedback

Add prebuilt image opens a centered, viewport-bounded 800px Ant Design Modal; saved-profile editing keeps the same fields in its existing panel. The body scrolls while Cancel/Save stay reachable. Dirty Cancel/Escape offers Keep editing or Discard changes, busy saves block dismissal, and nested credential creation owns Escape until it closes. Cancel restores the previous selection; close restores the creation trigger. Successful Save selects the saved revision for explicit verification.

Forms retain a 48rem maximum and 72ch supporting copy. The Sandbox stack and shared field grid own 16px sibling gaps with no added field margins. Related fields use two equal columns at 768px+, stacking below; numeric controls fill their column. Global and Instance Sandbox storage displays MiB and persists exact integer bytes, including inherited hints. Scripts use 4–14 rows; Instance source/reset controls remain explicit. Image comes before optional Registry authentication. Tags and Docker Hub short names resolve to fixed digests on Save; the stored image identity remains copyable. Separate Save/Discard from readiness/verification actions. Inventory uses six columns with table-local 1080px scrolling.

Client submission errors use the shared keyed AntD warning, persistent inline field guidance, disclosure of hidden invalid fields and deferred scroll/focus. Failed writes use action-specific error toasts plus retained local details, diagnostics and drafts. Reuse `adminFormFeedback` for native forms and explicit modal validation, and `showAdminFailure` for errors. Shared Instance configuration emits one coordinated notification and distinguishes a failed save from a failed refresh after a successful write. Background reads and polling keep local recovery; Reload does not stand in for correcting a failed save.

Shared `appMessageConfig` caps native messages at 40rem, wraps full text/unbroken references and centers the stack 16px above the bottom safe area with 16px inline clearance. An opaque elevated fill and 1px native `colorBorder` outline separate feedback from dark content without an extra shadow. AppShell raises feedback only when visible actions would overlap it, leaving 8px above the frontmost dialog footer, composer or sticky configuration frame. Covered page controls do not reserve space. Overlay close, scroll, resize and growing inputs recalculate placement. Normal-flow stacking handles resizing; overflow stays local within half the dynamic viewport or smaller available action clearance, with at most three messages and native hover pause. Sandbox readiness and Profile/Instance verification errors use the same six-second feedback. Do not restore blanket 96px gaps, page-wide header strips or per-feature geometry.

Credential and Sandbox creation share the bounded container/body/footer rules with 16px viewport clearance and 40px narrow controls. Preserve native Modal keyboard/focus behavior and native AntD inner insets. The [feedback verification record](../../docs/reports/admin-form-feedback-verification-2026-10-15.md) records the earlier desktop/mobile interactions and remaining gaps. The [OCI hardening record](../../docs/reports/oci-sandbox-final-hardening-2026-10-15.md) owns storage, expiry and catalog checks; historical narrower catalog captures do not define the current table width.

Current working-tree onboarding copy keeps ordinary-image requirements in the Image helper: shell and `/proc`, platform-mounted writable `/working`, no Python or pre-created-directory requirement. Workload protections names the image default user. Reuse the existing bounded helper and responsive Descriptions; Save, Check image readiness and Verify keep their distinct meanings. The initialization helper still says non-root and is tracked in the sync record; this docs-only update does not claim that source copy is corrected.
