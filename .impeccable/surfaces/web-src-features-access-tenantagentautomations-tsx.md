---
version: 1
slug: "web-src-features-access-tenantagentautomations-tsx"
primary_target: "web/src/features/access/TenantAgentAutomations.tsx"
related_targets: ["web/src/features/admin/AutomationScheduleFields.tsx", "web/src/features/admin/AdminSessionPicker.tsx", "web/src/features/admin/InstanceAutomationsSection.tsx", "web/src/features/access/AccessApp.tsx", "web/src/services/tenantAutomations.ts"]
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

## Completion report extension — 2026-10-21

Independent tenant Automation work retains its Background work history. The existing Configuration Form adds a Completion section: a native Select chooses None or Selected conversation; the latter reveals the shared `AdminSessionPicker`. Its explicit tenant source owns list/read transport, while the existing picker owns browsing, manual Session ID entry, validation feedback and recovery. Keep the active, same-Instance destination guidance beside the field and explain that reporting is a separate delivery that may require approval. This destination choice does not select execution in an existing Session.

Retain the incumbent dark native Ant Design v6 Form/Drawer, shared schedule/model fields, timing validation, draft guards and 8/12/16 spacing. The 640px/full-narrow shell keeps one 16px body inset. Long selected conversation labels truncate within the native Select; the full exact Session ID wraps below. Existing footer Flex wraps Save Automation, Close and Reload current Automation at narrow widths, retaining native mobile sizing. No CSS, token, font, asset, component preview or UI-kit extension is required.

The fresh finish reviewer returned SHIP with five contract sections and no material fixes for the scrolled Completion section in [1440px](../review/tenant-automation-completion/configured-report-1440.png), [1024px](../review/tenant-automation-completion/configured-report-1024.png), [768px](../review/tenant-automation-completion/configured-report-768.png), [390px](../review/tenant-automation-completion/configured-report-390.png) and [320px](../review/tenant-automation-completion/configured-report-320.png) captures. These establish configured-destination presentation only, rather than whole-page or full-editor visual acceptance. This documenter sampled the actual 1440px and 320px captures and compared current Completion, picker and tenant transport sources to the incumbent system. Loading, hover, empty choices, failures, stale/access-loss presentation and measured contrast have no dedicated visual acceptance from these captures.

The implementation handoff records 120 focused passing UI cases, a passing web build, one detector result `[]`, and actual CLI picker/save/409 stale-draft/reload/save/Run/single-delivery interactions across five widths without overflow. Independent MCP exercised an owned destination save, a real 403 for a foreign-Instance destination with retained blocked draft, explicit reload and a corrected saved destination. Those are functional checks at their exercised scope; this documentation pass runs no application checks and does not expand them into visual acceptance or backend-wide qualification.

Historical `.impeccable/design.json` metadata remains intact. Its recorded theme fingerprint still matches; app CSS, AccessApp and PRODUCT/DESIGN context already differed before this merge. The picker has no recorded source fingerprint there. This bounded merge preserves that drift without re-extraction or repair. Existing-Session execution remains a later module; full proposal, design-system and milestone acceptance remain open.
