# Shared Admin compositions

Use Ant Design v6 directly for controls. These components compose recurring Agent Core product structures; behavior and persistence stay in the feature that owns the data.

The [design guide](../../../../.agents/context/DESIGN.md) owns presentation guidance and extracted tokens. Its [preview sidecar](../../../../.impeccable/design.json) illustrates the same system; previews do not replace the React components. When a shared composition changes, sync this guide, the design guide, the sidecar and the [Admin surface brief](../../../../.impeccable/surfaces/web-src-features-admin-adminapp-tsx.md) together. Keep screen behavior in the frontend specification.

`AgentConfigurationPanel` in [AgentConfigurationLayout.tsx](AgentConfigurationLayout.tsx) owns the section border, heading and body insets, heading-to-description gap, and optional body stack. It serves Definition authoring/inspection, Instance configuration, and global Events/Credentials.

`AdminPageHeading` owns the current task title, adjacent inventory count and creation actions. `AdminOwnerHeading` in [AdminShell.tsx](AdminShell.tsx) owns the shared selected-owner name/status row and secondary identity metadata for Instance and Definition workspaces. Use its slots instead of creating another owner header. The shell owns the compact narrow header, breadcrumb truncation, navigation's single outer inset and grouped scoped/global Search results. Overview uses the registry's categories for its native link buttons.

- Heading and body each own one 16px inset through the shell's resolved Ant Design spacing token.
- The heading owns an 8px gap and wraps its title/action row.
- Use `bodyGap="compact"`, `"default"`, or `"section"` for 8/12/16px between content siblings. Leave `bodyGap` unset for a single table or a form that already owns its layout.
- The body stack clears direct Typography margins so paragraphs do not add another gap.
- Use `extra` for heading actions. Preserve accessible action labels and loading/disabled states at the call site.
- Avoid another padded container inside the panel body. Child controls retain their normal Ant Design internal padding.
- Operating instructions uses the shared `admin-settings-instructions` form variant to fill the section width. Its multiline textarea is excluded from the short-control cap; other Settings forms retain their 48rem bound.

```tsx
<AgentConfigurationPanel
  title="Agent Workspace"
  label="Agent Workspace"
  description="/home · Durable across sessions."
  bodyGap="section"
>
  <WorkspaceUsage />
  <WorkspaceActions />
  <WorkspaceTable />
</AgentConfigurationPanel>
```

The example illustrates composition; the actual Workspace feature owns these elements and commands.

`AdminShell` and `adminNavigation.ts` own global/contextual navigation, page titles, breadcrumbs and bounded Search Admin. `appRoute.ts` resolves legacy links once; it never routes malformed Admin IDs to Chat. `InstanceDetail` retains exact-owner settings and visited editors; `InstanceOverview` is saved-data inspection. `DefinitionDetail` owns the revisioned lifecycle and `DefinitionDraftWorkspace` retains the singular candidate/resource/evidence state across addressable pages. `useInstanceSettingsController` owns exact-owner reads, independent sparse drafts and revisioned patches; `InstanceSettingsSection` renders the current typed groups. `RetainedAdminPage` performs lazy first mount and state retention; hidden polling stays governed by feature active/open props. `AgentIdentitySections` and `AgentSkillsResourcesSections` serve only local immutable inspector controls or pure single-section composition, never a parallel workspace tab system. Reuse `AdminCollectionToolbar` and `useAdminCollectionSearch` for collection search/pagination, `useAdminDetailLayout` for responsive detail labels, `SkillDrawer` for procedure authoring/inspection, and `ResourceSelectionToolbar` for bulk resource selection/deletion.

Configuration forms use the shared `admin-config-form` layout in `app.css`: available width up to 48rem, 16px between fields, full-column Selects and supporting Typography capped at 72ch. The enclosing panel owns the outer inset; form action rows use content-width buttons and wrapping token gaps. Global Platform → Browser privacy consumes this layout while effective/saved Descriptions retain the full section width.

Use `admin-collection-table` for compact collection and diagnostic tables, including Built-in Event subscribers and deliveries. The shared style owns wrapping ordinary cells and tabular numerals; the feature owns column widths and table-local scrolling. Bounded name links use `admin-collection-name`, full accessible text and a full-name title. Built-in subscribers share the 8px section rhythm and 640px/full-mobile Event drawer; the existing 40px mobile action policy applies.

[shared-configuration-layout.spec.ts](../../../e2e/shared-configuration-layout.spec.ts) checks representative panels at 1440, 768, 767 and 390px, aligned heading/body edges, consistent insets, local overflow and working Workspace reload. Feature suites cover saved drafts, read-only inspection, validation, conflicts and recovery. The canonical frontend behavior remains in [Frontend Implementation](../../../../docs/13-frontend-implementation-spec.md).

The [main UI consistency report](../../../../docs/reports/main-ui-style-consistency-verification.md) records historical bounded Synthetic privacy/subscriber/delivery checks on source `9145e66e`. Documentation synchronization reuses that evidence and does not establish a new acceptance gate.


Navigation checks cover every canonical route, all legacy leaves, reload/Back/Forward, native modified/middle-click, same-owner draft retention, departure/unload guards, invalid/deleted owner recovery and exact Run/Automation/conversation sources. Browser privacy is global; Instance Effective is read-only. Verify the responsive navigation Drawer and local table overflow at 1440/1024/768/767/390/320px. `admin-tab-navigation.spec.ts` now tests pages and canonical destinations rather than tab snapshots. Use `e2e/support/admin-navigation.ts` to operate the responsive rail; it retains local read-only inspector controls. The [focused configuration report](../../../../docs/reports/admin-focused-configuration-verification.md) records the current source/captures and local checks; reorganization and earlier reports remain historical.

`AdminNavigationIcon` supplies native Ant Design icons for the focused registry destinations. The brand is Agent Core with a secondary Admin marker and a desktop breadcrumb separator. Contextual navigation has no misleading global shortcut. `InstanceConfigurationProvider` coordinates the existing revisioned configuration editors; its sticky actions frame also styles the Definition draft actions. Saves use the previous receipt revision, stop on failure and retain uncommitted changes. Collection dialogs keep their explicit mutations. The exact owner fences a following write after unmount. Field-level override/reset semantics are unchanged; inherited inputs are editable without Customize and labels reserve icon height. Common table actions are tooltipped icon buttons; mixed action sets use an icon-labeled More menu and restore focus to the surviving trigger or creation action.

Presentation ownership: `.admin-owner-actions` supplies the shared container-tone fill, 1px border, 8px surface radius, 16px inset and 8px stack gap; feature action rows wrap inside it. `.instance-settings-field-label` owns the minimum 32px label row (40px below 768px), allowing long text to wrap while metadata controls stay non-shrinking. `AppShell` maps `--ac-space-tight` to native `paddingXXS` (4px); use it for the narrow brand gap, header/action gaps below 360px and the tightly paired Artifact icon actions. Regular panel/control/sibling roles remain 8/12/16px. Navigation owns its 12px inset, links their 8px inset and 12px icon/text gap; the Drawer body has no second padding. Do not restore combined workspace tabs or per-section Customize gates. Skill-copy Customize and explicit budget/self-management inheritance controls retain their independent meanings.


File preview actions reuse `features/filePreview/FilePreview`; source tables retain action spacing and adequate column width (Workspace 160px, draft Resources 112px). The preview shell owns canvas size, responsive containment and action/content gaps. Resource details keep their editor/metadata Drawer and open this read-only dialog above it; closing restores the surviving preview trigger, then Drawer close restores Inspect. Do not create separate binary viewers or add outer padding inside the shared shell.

AddCredentialDialog owns the single purpose selector and reuses CredentialFields, DriveAccountFields and EmailAccountFields. Existing specialist dialogs own immutable-purpose edits and safe Gmail draft restoration. CredentialConnectionTag projects saved Email/Drive state separately from credential status without provider calls. Instance aliases expose bounded DriveFiles browsing. Generic Artifact/home actions contain only Preview/Download; conversational tools own Drive mutation. Shared FilePreview owns source authorization, focus and cancellation. Tokens, upload-session URLs and model grants never belong in presentation state.

Sandbox catalog: shared searchable collection, guarded Add prebuilt image Modal and shared saved-profile editor, wrapped saved source/digest, exact-revision availability, advanced limits/scripts and read-only history. Image entry accepts tags, Docker Hub short names or digests; Save resolves tags to a fixed digest, and public images need no credential. The existing Credentials purpose workflow owns protected Container registry input; profiles select safe IDs only. Definition and Instance pickers preserve unavailable saved IDs but fence new choices. Server readiness is independent of inventory, so empty-catalog onboarding can Verify. See [frontend contract](../../../../docs/13-frontend-implementation-spec.md#oci-catalog-interaction).

Sandbox profile creation reuses the bounded credential-dialog body/footer layout, with draft-safe Cancel, save recovery and trigger focus restoration. Global and Instance resource fields reuse `admin-settings-field-grid` and `sandbox-settings-fields`; the grid owns 16px sibling gaps, controls fill their column, and MiB storage fields preserve exact byte persistence. Instance metadata, inheritance and Reset remain owned by the settings controller.

The Image helper describes ordinary Linux images with `/bin/sh` and `/proc`, platform-mounted writable `/working`, and optional Python. Workload protections and initialization help name the image default user. Keep image compatibility separate from Save-time pinning and exact-revision Verify. The [design-system sync record](../../../../docs/reports/design-system-sync-2026-10-15.md) retains the earlier snapshot and records the copy mismatch resolved in `0ca35e86`.

`adminFormFeedback` owns submission validation feedback for native forms and modal actions: a keyed Ant Design warning, persistent field errors and deferred scroll/focus after disclosure. Use `validateAdminForm` for explicit validation and `showAdminFormValidation` for `onFinishFailed`. Reuse `showAdminFailure` with an action summary for failed writes, preserving inline details and existing owner guards. Shared Instance configuration coordinates one notification; background catalog reads and polling retain local recovery without submission toasts.

Admin and Chat both pass `appMessageConfig` directly to Ant Design App. It owns a 40rem maximum, full wrapping, opaque elevated fill, a 1px native `colorBorder` outline and 16px viewport edge spacing plus the bottom safe area. AppShell's `useAppFeedbackPlacement` measures the visible frontmost dialog/footer or page action surface and raises overlapping feedback only as far as needed, leaving an 8px control gap. Covered page controls do not add clearance. Resize, scroll, overlay close and composer growth update placement; normal-flow notices avoid stale height offsets. Keep form focus/recovery in the feature owners; do not add per-page offsets or a second notification system.

The [design guide](../../../../.agents/context/DESIGN.md#admin-forms-creation-dialogs-and-validation) records current form/dialog spacing and feedback presentation; the [feedback verification record](../../../../docs/reports/admin-form-feedback-verification-2026-10-15.md) records the previously exercised recovery paths. Product behavior remains in the frontend contract.
