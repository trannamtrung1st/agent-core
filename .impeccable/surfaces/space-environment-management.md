---
version: 1
slug: "space-environment-management"
primary_target: "web/src/features/access/TenantSpaces.tsx"
related_targets: ["web/src/features/access/TenantSpaceMembers.tsx", "web/src/features/access/TenantSpaceEnvironments.tsx", "web/src/features/access/TenantSecrets.tsx", "web/src/features/access/TenantResourceAccess.tsx", "web/src/features/chat/TenantSessionContext.tsx", "web/src/features/access/TenantAutomationContextFields.tsx", "web/src/features/files/TenantWorkspaceFiles.tsx", "web/src/app/antdTheme.ts"]
---

# Space and Environment management

Mode: Operate. Extend the established dark Ant Design v6 world. Product behavior belongs to docs/13-frontend-implementation-spec.md and docs/14-api-and-realtime-protocol.md; the proposal and implementation ledger distinguish requested scope from verified behavior.

PURPOSE: Manage shared context, member-specific responsibilities/resource availability, Space-owned home files and optional typed variable profiles without confusing those choices with IAM, capability or approval authority.

COMPOSITION: Existing AdminShell navigation adds Spaces under Agents and Secrets beside Credentials. Space detail uses exact-owner Overview/Members/Workspace/Environments/Access destinations. Reuse collection toolbars, tables, file preview/Office renderers, native responsive Drawers, scoped confirmations and unsaved guards. Chat adds a modest per-Session context row; Automation pins remain beside execution choices. No new UI kit or card system.

STATES: Private ownership is protected; Shared deletion is deliberate. Profiles remain manageable while disabled; ordinary Chat Environment selection is hidden, with explicit stale-selection clear recovery. Plain values require an explicit authorized lookup; protected values have no read-back. Read-only users see no mutation controls, file-denied users see no paths or bytes, and metadata-only variable readers get resolve-denied recovery. Known conflicts preserve drafts and block repeats until deliberate refresh. Unknown outcomes never unlock a replay. Response-progress refresh keeps an open context draft intact.

SPACING/ACCESSIBILITY: Keep 16px section and 8px action gaps at shared owners, compact additive insets and wrapping native footers. Resolve authorized resource paths/account names rather than opaque IDs. Keyboard Escape uses one discard-confirmation owner through native close completion and restores the surviving opener. Small link/selected-tab text uses the existing lighter blue after AntD dark mapping, preserving primary fills. Validate contrast and settled geometry at 320/390/768/1024/1440.

EVIDENCE: docs/reports/space-environment-ui-brief-2026-10-25.md and docs/reports/space-environment-implementation-2026-10-25.md record actual Synthetic journeys, checks, corrected failures and unverified proposal obligations. Neither file claims milestone, live-provider, hardware or hosted CI acceptance.

SOURCE SNAPSHOTS: reuse the Workspace page and existing access editor for immutable Space-owned knowledge/resources. Show logical path/kind/revision; exact home source is visible only with its separate file-read permission. Membership selectors show authorized paths/revisions. A bound delete gives a specific cleanup action, and deliberate refresh preserves identity. Native Drawers were inspected at settled 320px and 1440px, with all primary actions reachable and no document overflow. Durable screenshots are in the linked implementation report assets; source output and publication qualification remain separate.
