---
version: 1
slug: "web-src-features-access-tenantevents-tsx"
primary_target: "web/src/features/access/TenantEvents.tsx"
related_targets: ["web/src/features/access/AccessApp.tsx", "web/src/services/tenantWebhooks.ts", "web/src/features/admin/AdminCollectionToolbar.tsx", "web/src/features/admin/AgentConfigurationLayout.tsx", "web/src/features/admin/useAdminDetailLayout.ts", "web/src/features/access/useAccessDraftGuard.tsx", "web/src/app/confirmAction.ts"]
---

# Tenant webhook connections

Mode: Operate. Ordinary code-led extension of the incumbent organization Integrations → Events surface.

## Direction contract

THESIS: Make an exact organization's readable webhook sources and accepted data understandable through the existing collection and inspector. Receipt acceptance remains distinct from matched subscriptions, admitted work and completed execution.

OWN-WORLD: Preserve dark Ant Design v6, native Form/Table/Descriptions/Collapse/Alert, the shared configuration panel and 8/12/16 spacing. Existing access Drawer, confirmation, dirty guard and focus owners retain their roles. No new token, CSS, asset, wrapper or UI kit.

STORY: Find a source, inspect immutable identity and bounded receipts, rename deliberately, or confirm exact token rotation/revocation/reactivation. Save a newly issued token once; current independent authority and explicit recovery remain visible.

FIRST VIEWPORT: Shared organization heading and Reload Events precede the webhook configuration section. Loaded-page search and New webhook sit above a native paged source table; the independently read built-in metadata catalog follows. Details use the existing 640px Drawer, capped to the viewport and full width on narrow phones, with reachable wrapping footer actions.

FORM: Incumbent composition extension; no concept seed or replacement visual world. Feature state and canonical transport stay in their existing owners.

FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance

## Interaction and recovery boundaries

Current `webhooks.read` filters rows/counts/pagination; management alone reveals no protected source. Create and exact-source management hints remain independent. The saved event key is immutable. Stale rename keeps the local draft while explicit Reload refreshes revision witnesses; unknown or malformed acknowledgements fence replay and cannot recover a lost token.

Successful create/rotation displays a masked transient token with Copy/Dismiss and sender guidance. Clipboard failure preserves manual selection recovery; Reload Events retires the token. Shared confirmation names the exact source and organization. Rotation invalidates the previous token; issuing a token for a revoked source explicitly confirms reactivation. Revoke preserves readable historical receipts. Source/receipt read loss retires protected detail, token/evidence and restores a usable collection focus target.

Accepted receipts expose source event ID, source revision, occurrence/admission dates and bounded literal JSON through native read-only text controls. Sender examples and external data grant no execution authority. The built-in catalog uses its independent `core-events.read` metadata read and grants no occurrence/subscription access.

Behavior belongs to [Frontend](../../docs/13-frontend-implementation-spec.md#tenant-webhook-connections), the [interaction contract](../../docs/19-multi-tenant-iam-ux.md#tenant-webhook-connections-interaction-contract) and [canonical routes](../../docs/14-api-and-realtime-protocol.md#canonical-tenant-webhook-connections-and-bearer-ingress).

## Bounded finish evidence — 2026-10-21

The fresh finish reviewer returned **SHIP**, with no material fixes, after a full review of 15 corrected captures under `.impeccable/review/tenant-webhooks/`: `header-final-{1440,1024,768,390,320}.png`, `details-final-{1440,1024,768,390,320}.png`, `collection-final-{1440,390}.png`, `activation-confirmation-final-390.png`, `rotation-confirmation-390.png` and `rotated-token-masked-390.png`. Transition-obscured earlier captures are superseded. Dedicated hover/loading/empty/failure/stale/permission captures and independently measured contrast are outside that visual verdict.

This documentation pass sampled the final desktop header, 320px receipt details, masked token and activation confirmation. It checked `TenantEvents`, `AccessApp`, the typed webhook service, theme, shared collection/configuration/detail alignment and narrow access-control CSS owners. Existing native spacing, wrapping action rows, responsive labels and Drawer containment match the incumbent system; no new presentation recipe is required.

The [qualification report](../../docs/reports/tenant-webhook-verification-2026-10-21.md) owns 77 focused passing cases, builds, detector `[]`, actual Synthetic ingress/source journeys, controlled recovery/access cases, five-width measurements and remaining limits. This documentation pass runs no browser or application checks. Receipt/source qualification does not close event/core/watch subscriptions, Automation fanout/admission, the full proposal, live sender/provider qualification or milestone acceptance.

Historical `.impeccable/design.json` timestamps and fingerprints remain unchanged. The sampled theme and collection-toolbar fingerprints still match; app CSS, AccessApp, draft guard and PRODUCT/DESIGN have pre-existing drift. This bounded documentation merge neither repairs that drift nor claims whole-repository freshness. No shipping raster asset is introduced.
