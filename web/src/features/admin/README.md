# Shared Admin compositions

Use Ant Design v6 directly for controls. These components compose recurring Agent Core product structures; behavior and persistence stay in the feature that owns the data.

The [design guide](../../../../.agents/context/DESIGN.md) owns presentation guidance and extracted tokens. Its [preview sidecar](../../../../.impeccable/design.json) illustrates the same system; previews do not replace the React components. When a shared composition changes, sync this guide, the design guide, the sidecar and the [Admin surface brief](../../../../.impeccable/surfaces/web-src-features-admin-adminapp-tsx.md) together. Keep screen behavior in the frontend specification.

`AgentConfigurationPanel` in [AgentConfigurationLayout.tsx](AgentConfigurationLayout.tsx) owns the section border, heading and body insets, heading-to-description gap, and optional body stack. It serves Definition authoring/inspection, Instance configuration, and global Events/Credentials.

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

Reuse `AgentIdentitySections` and `AgentSkillsResourcesSections` for the corresponding Definition/Instance navigation. Reuse `AdminCollectionToolbar` and `useAdminCollectionSearch` for collection search/pagination, `useAdminDetailLayout` for responsive detail labels, `SkillDrawer` for procedure authoring/inspection, and `ResourceSelectionToolbar` for bulk resource selection/deletion.

Configuration forms use the shared `admin-config-form` layout in `app.css`: available width up to 48rem, 16px between fields, full-column Selects and supporting Typography capped at 72ch. The enclosing panel owns the outer inset; form action rows use content-width buttons and wrapping token gaps. Screenshot privacy consumes this layout inside the existing Browser provider section while effective/saved Descriptions retain the full section width.

Use `admin-collection-table` for compact collection and diagnostic tables, including Built-in Event subscribers and deliveries. The shared style owns wrapping ordinary cells and tabular numerals; the feature owns column widths and table-local scrolling. Bounded name links use `admin-collection-name`, full accessible text and a full-name title. Built-in subscribers share the 8px section rhythm and 640px/full-mobile Event drawer; the existing 40px mobile action policy applies.

[shared-configuration-layout.spec.ts](../../../e2e/shared-configuration-layout.spec.ts) checks representative panels at 1440, 768, 767 and 390px, aligned heading/body edges, consistent insets, local overflow and working Workspace reload. Feature suites cover saved drafts, read-only inspection, validation, conflicts and recovery. The canonical frontend behavior remains in [Frontend Implementation](../../../../docs/13-frontend-implementation-spec.md).

The [main UI consistency report](../../../../docs/reports/main-ui-style-consistency-verification.md) records the latest bounded Synthetic privacy/subscriber/delivery checks on source `9145e66e`. Documentation synchronization reuses that evidence and does not establish a new acceptance gate.
