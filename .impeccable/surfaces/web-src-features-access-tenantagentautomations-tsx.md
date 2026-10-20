---
version: 1
slug: "web-src-features-access-tenantagentautomations-tsx"
primary_target: "web/src/features/access/TenantAgentAutomations.tsx"
related_targets: ["web/src/features/admin/AutomationScheduleFields.tsx", "web/src/features/admin/InstanceAutomationsSection.tsx", "web/src/features/access/AccessApp.tsx", "web/src/services/tenantAutomations.ts"]
---

# Tenant Instance Automations

Mode: Operate. Manage one exact saved scheduled task with current independent access; inspect its actual admitted work separately.

## Direction contract

Retain the incumbent Ant Design dark Admin world. Existing Instance navigation, configuration panel, collection toolbar and bounded native Table own the first viewport: meaningful saved objective, status, next occurrence and deliberate actions. New/Edit uses the incumbent 640px desktop/full-narrow Automation Drawer. Reuse schedule timing/model fields and the current confirmation/unsaved guard. Do not add a dashboard, alternate editor, UI kit or visual token.

The important transition is saved task → exact admitted Run → factual Activity. Admission does not claim completion. Registration cancellation stops future occurrences while keeping the admitted Run link. Current action-specific authority, stale-draft recovery, unknown-outcome fences and raw-read retirement remain legible.

## Bounded inspection

Synthetic MCP and repeatable E2E exercise desktop/narrow create, enable, save, actual manual completion, cancel and Activity plus current reader revocation. At 390px the Drawer is x=0,width=390 with no document overflow; footer actions have 40px height. Existing Automation form margins are zero, with the parent Flex owning section gaps and the body retaining 16px padding. Long saved names/instructions remain native escaped content. The first batched visual pass identified duplicate form margins and undersized narrow actions; reusing existing form/access Drawer classes corrected both. One confirmation batch inspected the terminal read-only state. Terminal next-occurrence copy was corrected and separately verified functionally; no discretionary visual loop followed.

[Canonical behavior](../../docs/13-frontend-implementation-spec.md#tenant-schedule-automation-management) and [qualification](../../docs/reports/tenant-automation-ui-verification-2026-10-21.md) own status. Other trigger/destination/admission Review work and the full design/security audit remain open.

## Exact admission consent extension

Canonical Schedule/manual admission can now reserve an occurrence for durable Review before any work exists. The native acknowledgement says “Waiting for admission approval” and links to the exact shared approval drawer. Actual Run admission keeps its separate Activity link. Current source-busy hints disable another Run; reload neither retries the action nor grants approval-read authority. The protected confirmation, current read retirement, unknown-outcome fence and shared layout remain their existing owners. [Canonical behavior](../../docs/13-frontend-implementation-spec.md#tenant-automation-admission-consent-presentation) owns the contract; this extension adds no visual token, CSS, kit or component recipe. The full proposal/design-system audit remains open.
