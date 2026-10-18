---
version: 1
slug: "web-src-features-access-tenantgovernance-tsx"
primary_target: "web/src/features/access/TenantGovernance.tsx"
related_targets: ["web/src/features/access/AccessApp.tsx", "web/src/features/access/accessRoute.ts", "web/src/features/admin/AdminShell.tsx", "web/src/features/admin/AdminCollectionToolbar.tsx"]
---

# Organization Governance

Mode: Operate. Ordinary code-led extension of the incumbent dark Ant Design v6 system. The existing Admin shell, collection toolbar, responsive Table, native large Drawer, Descriptions, Select, Checkbox, feedback and confirmation compositions own presentation. No new visual world, token, theme, font, CSS, asset or UI kit.

## Direction contract — 2026-10-18

**THESIS:** Inspect the Core minimum and current organization review policy for one permission, then deliberately save a witnessed change without implying permission or approval to act.

**OWN-WORLD:** Existing dark Ant Design v6 Operate. `AdminShell` owns outer width, insets, page heading and navigation. `AdminCollectionToolbar` and native Table own search/paging; narrow rows carry their current-policy summary. Native large Drawer and responsive one-column Descriptions own policy inspection; native Select and Checkbox own editing. Keep the established 8/12/16px parent spacing rhythm and shared tight metadata gaps, focus treatment, text wrapping and footer actions. The source uses the incumbent theme bridge and shared CSS unchanged.

**STORY:** Organization context → permission family/action and secondary key → Core minimum versus current review → policy source and authority restrictions → deliberate edit. Review grants no permission. People-only and nondelegatable restrictions remain explicit; independent-review help explains that the requester cannot decide their own independent review. Read-only inspection remains useful without a Save action.

**FIRST VIEWPORT:** Preserve Access & Security navigation, the Governance heading and the review-does-not-grant-permission notice. Search and Refresh policies precede the collection. Wide rows keep Current review in its own column; below the existing medium breakpoint the permission cell stacks the policy summary and source/restriction tags. Empty search offers Clear search. Permission labels and long keys wrap within the shared collection geometry through 320px.

**FORM:** Compare saved Core minimum and current organization policy before the labeled mode Select and independent-review Checkbox. Disable choices below the Core minimum. Use Core minimum stages a draft; it does not save. Relaxation uses the existing confirmation with organization, permission, before/after review and the no-grant boundary. Keep drafts on stale/transient failure; stale/denied or unreadable acknowledgement blocks another save until explicit recovery. Reload of a dirty draft requires discard confirmation. Native close/navigation use the unsaved-draft guard; busy commands and confirmations cannot overlap. Footer actions wrap, and close restores focus to the initiating row or page heading.

## Evidence and limits

The [Governance interaction contract](../../docs/19-multi-tenant-iam-ux.md#organization-governance-policy-interaction) and [Frontend](../../docs/13-frontend-implementation-spec.md#organization-governance) own behavior. The [focused verification report](../../docs/reports/tenant-governance-ui-verification-2026-10-18.md) owns executed runtime evidence. The implementation handoff reports actual Synthetic owner relaxation/cancel, second-tab tightening, stale 409 with disabled Save and retained draft, discard/reload, reader inspection with disabled controls/no Save and Escape focus restoration. This documenter did not execute those journeys, run builds/tests/browser checks or take captures.

The fresh finish reviewer returned **SHIP** with no material fixes after inspecting all 15 current PNGs in `.impeccable/review`. This documenter also individually inspected every listed image against `TenantGovernance.tsx`, its routing/navigation, shared shell/collection source, `antdTheme.ts`, `AppShell.tsx`, shared CSS and the incumbent [design system](../../.agents/context/DESIGN.md) and [product context](../../.agents/context/PRODUCT.md):

- `governance-1440-collection.png`, `governance-1440-editor.png`, `governance-1024-editor.png`, `governance-768-editor.png`, `governance-390-editor.png`.
- `governance-320-editor.png`, `governance-320-editor-lower.png`, `governance-320-confirmation.png`, `governance-320-collection.png`, `governance-390-collection.png`.
- `governance-390-stale.png`, `governance-390-empty.png`, `governance-390-readonly.png`, `governance-1440-readonly.png`, `governance-320-long-permission.png`.

The captures show shared black/container/elevated surfaces, ordinary Ant Design typography and blue actions; wide aligned descriptions become vertical on mobile. At 320px the independently scrolling Drawer body exposes lower fields while the footer wraps Save to a second row. The narrow confirmation keeps its exact context and actions readable. The stale capture keeps the draft with explicit recovery and disabled editing/Save. Read-only captures show the nondelegatable boundary, disabled fields and Close/Reload only; the long-permission collection capture retains the People only tag and wraps the key. The runtime handoff reports no page overflow at 320/390/768/1024/1440px; these static images show no visible horizontal spill but do not independently measure overflow or focus behavior.

Loading, transient failure, malformed acknowledgement and read-loss presentation have no screenshot acceptance. Hover, measured contrast and uncaptured lower content are not established by this pass. Controlled presentation is not backend authorization proof. The active design hook reported no deterministic issue; this documentation pass did not rerun it. No broad multi-tenant IAM proposal, milestone or hosted acceptance is claimed.

## System comparison and existing drift

This extension matches the incumbent system and needs no system-file rewrite. `antdTheme.ts`, `AppShell.tsx`, `AdminShell.tsx` and `AdminCollectionToolbar.tsx` still match the sidecar's historical inspection hashes. Preserve `.agents/context/DESIGN.md`, `.agents/context/PRODUCT.md` and `.impeccable/design.json`, including their normative tokens, previews and provenance.

Existing drift remains explicit: the sidecar's earlier inspection hashes for DESIGN, PRODUCT, `app.css`, `AccessApp.tsx` and `accessRoute.ts` differ from the current tree, and the retained Resources/Instance/Session context and sidecar open-scope summaries still name Governance as pending. Those historical snapshots do not describe this new bounded surface; this record and the focused report supply its current evidence. No historical fingerprint or broader pending-proposal summary is rebased here. No observed presentation defect is promoted into a system rule, and no pre-existing drift is repaired within this surface-only write boundary.
