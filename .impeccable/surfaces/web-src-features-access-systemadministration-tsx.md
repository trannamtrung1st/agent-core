# Platform administrative identity metadata

Mode: Operate. Ordinary extension of the incumbent neutral dark Ant Design v6 Access/Admin world; no comp, new visual world, system token, font or asset change.

Direction: authorized System humans inspect only organization/Account administrative identity metadata, then deliberately review an exact status change. Scope → explicit filters → count/Refresh → server metadata page → details/current-versus-proposed status → exact confirmation. Readable scope, filters and first records remain the first-viewport priority at wide and narrow widths. Existing System membership/governance distinctions and absence of private-content/impersonation links remain canonical behavior, not a new visual rule.

`SystemAdministration.tsx`, `AccessApp.tsx`, `accessRoute.ts` and `AdminNavigationIcon.tsx` extend the shared shell/navigation. Reuse Audit filter/bounded-form/mobile/detail layouts and native Form, Table/Pagination, Tag, large viewport-capped Drawer and responsive Descriptions. `CreateOrganization`, `confirmAction` and `useAccessDraftGuard` retain creation, exact confirmation and draft ownership. The shell owns outer inset; enclosing Flex owns 8/12/16 sibling gaps. At 320/390px filters stack, actions wrap and exact IDs remain copyable/wrapping. Native controls inherit the dark baseline. No module CSS, fonts, tokens, assets, new wrapper or second kit is introduced.

Canonical behavior: [System administration metadata interaction contract](../../docs/19-multi-tenant-iam-ux.md#system-administration-metadata-interaction-contract). The [focused verification record](../../docs/reports/system-administration-verification-2026-10-21.md) owns execution details and remaining limits.

## Bounded visual evidence — 2026-10-21

The fresh finish reviewer reopened all eleven valid current captures under `.impeccable/review/system-administration/` and returned **SHIP**, with no material fixes:

- `organizations-{1440,1024,768,390,320}.png`: organization collection tops and current shell scope.
- `organization-390-details.png` and `organization-320-confirm.png`: narrow native detail and exact confirmation.
- `accounts-{1440,320}.png`: Account collection tops and current shell scope.
- `account-390-details.png` and `account-320-confirm.png`: narrow native detail and exact confirmation.

All captures are 1000px high. The documenter sampled organization 1440/320px, organization details and Account confirmation, and checked all eleven dimensions/SHA256 fingerprints. Collection captures establish their visible tops; they do not establish every later page or below-fold record. Loading, empty, error, stale, permission, hover, keyboard and independently measured contrast remain outside visual acceptance. The final Platform Operator refusal copy is source/functionally qualified and is absent from these captures.

The single `/private/tmp/system-administration-detector.json` result is `[]`. It preceded the final refusal-copy-only addition; no second detector or later visual acceptance is claimed.

## Separate functional evidence

The final implementation handoff records:

- Backend: 25 passing cases in about 98 seconds, including original-session fences, atomic-store races, SQLite audit rollback and actual two-host recreation, and final Platform Operator protection. The earlier 38-case run contains 20 other identity/audit/System-privacy regressions with 18 overlapping cases; it is not added to the final run as a single total.
- UI: 44 passing focused cases in 55.15 seconds. Backend build: zero warnings in 43.65 seconds. Web build: passed in 9.03 seconds.
- Final CLI: one passing case in 20.4 seconds (26.9-second runner), exercising native exact Cancel/Confirm, stale/readback, lost committed reply without replay, 34 records paged 30+4, creation, Account session invalidation, tenant-only denial and final-operator refusal/discard.
- Independent MCP: native suspension/reactivation, stale refusal, fresh readback and fresh confirmation plus Account metadata. No app exceptions; an expected 409 resource error belongs to stale refusal, and the final Account page had zero console errors.

These executed journeys remain separately scoped from the visual verdict. This documentation pass runs no app checks and claims no full proposal or milestone acceptance.

## System preservation

Only this brief and an appended bounded extension note in `.agents/context/DESIGN.md` are updated. Normative frontmatter, fonts, tokens, theme, CSS, assets and component previews remain intact. `.impeccable/design.json` remains byte-identical at SHA256 `8bdc3659a25b706454ac8cc0394323c5e0e60ea1ade616756b180efbaecef49b`; no sidecar regeneration or broad extraction is performed. The theme fingerprint matches, while pre-existing app CSS, AccessApp and PRODUCT/DESIGN fingerprint drift is preserved without canonization or repair.
