# Canonical Approvals inbox and exact-action review — 2026-10-17

Baseline: `ceaf0efd4797ad6bcdaa255fae701f96a7b7ab45`. This is the next bounded slice of the active multi-tenant IAM/approval proposal. The user authorized consistency review, fixes, commit and push. No CI, installed-data reset, Real provider call, deployment or milestone acceptance was requested or performed.

## Delivered behavior

Organization Admin now offers `/t/{tenantId}/admin/approvals` and a linkable `/t/{tenantId}/admin/approvals/{requestId}` inspector. The default Pending inbox has server status/type filters, authorized counts and pagination. Resource-scoped readers are not excluded by a tenant-wide navigation check. Details are independently fetched, so a terminal request remains readable after it leaves the Pending list.

Saved persona/preference requests retain typed display snapshots in their existing bounded payload. Before values, Definition defaults and nullable overrides remain unchanged after execution. These fields do not change the canonical action hash, semantic root deduplication, grant scope or effect identity. Earlier payloads without snapshots remain valid; historical values are only reconstructed while exact target witnesses still match, otherwise marked unavailable. Mapping rejects malformed mixed snapshots. No migration is needed.

The detail/list API projects actual human display names, exact typed changes, default-restoration meaning, expiry, current qualification/cancellation hints and server time. It reads the actual waiting source Run and rechecks human session/security revision after enrichment. Hints are pure presentation, with no grant or permission snapshot. The existing POST and effect boundary remain authoritative. A stopped source does not falsely offer decisions or cancellation; its request may remain Pending until durable expiry under the existing lifecycle.

Review and one-time Exceptional authorization have different explanations and primary labels. Confirmations submit the saved hash plus request/security witnesses only. Decisions are single-flight, stale writes require explicit refresh, expiry disables actions, and scope retirement aborts requests and destroys open confirmations. A recorded mutation remains acknowledged even if the following read fails. Approval is separate from execution: only a completed receipt is Applied; absent receipts remain explicitly unrecorded and can be refreshed. No automatic mutation retry or fabricated Chat link is introduced.

## Focused local checks

| Check | Result |
| --- | --- |
| Infrastructure: `FullyQualifiedName~TenantPreferenceOperationTests\|FullyQualifiedName~TenantOperationAuthorizationTests` | 233 passed, including both stores, exact effect/receipt, stale/revoked/cancelled and root suppression regressions |
| Infrastructure: new/expanded review, visible inventory and Synthetic fixture filter below | 12 passed; includes preserved snapshot after effect/reopen, set/clear defaults, expiry, stale configuration cancellation and stopped-source hints |
| Snapshot codec follow-up: `FullyQualifiedName~Approval_request_retains` | 2 passed; legacy missing snapshot preserves exact operation, malformed mixed snapshot rejects |
| API: `FullyQualifiedName~Model_preference_authorization` | 14 passed, real worker and both stores including SQLite; approve/reject/cancel, Review/Exceptional, frozen/stale and capacity retry |
| Vitest: `TenantApprovals.test.tsx`, `accessRoute.test.ts`, `TenantAudit.test.tsx`, `identity.test.ts` | 23 passed |
| `pnpm run build` | Passed; existing large-bundle and third-party annotation advisories remain |
| `git diff --check` and changed-document local link/fence checks | Passed |

Infrastructure commands use `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --nologo --filter '<filter>'`. The 12-case filter is `FullyQualifiedName~Approval_request_retains|FullyQualifiedName~Saved_preference_review|FullyQualifiedName~Review_displays|FullyQualifiedName~Stopped_source|FullyQualifiedName~Approval_inventory_filters|FullyQualifiedName~SyntheticAuthorizationAuditTests`. API uses the equivalent command for `tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj`. Vitest/build run from `web/`. Counts overlap; they are not an aggregate total.

The API journey now asserts safe before/after DTOs, current action hints, named filters, authorized totals/offsets, invalid numeric status/page bounds, terminal hints and unchanged changes after the actual successor effect. Existing inventory coverage verifies exact approval and underlying target access before totals/pages; hidden requests cannot displace an older authorized one.

## Actual Synthetic browser journeys

Playwright MCP used a disposable native Synthetic InMemory host on 55270 and Vite on 55271. The fixture marker `synthetic approval review` emits one fixed bounded conversation-preference request only when the real authorization tool is offered. It supplies no tenant, principal, target, grant or witness. Requests ran through authenticated HTTP admission, the production tenant worker and durable approval wait. No debug injection endpoint or UI success mock was used.

1. Signed in through visible controls. An actual agent without operation permission prepared a saved Exceptional request. Opened it through the inbox, reviewed `en → vi`, confirmed **Authorize once**, observed **Approved decision recorded**, then refreshed to the real **Applied** receipt. The stored settings changed once, and the saved before values remained `en`.
2. An agent with exact underlying permission and Assisted management prepared Review. A concurrent role change invalidated the displayed revision. Clicking **Approve** returned 409; actions stayed disabled. Explicit **Refresh details** restored fresh qualification; **Reject** recorded a terminal rejection. A subsequent independent Review was approved through its visible confirmation and appeared Applied in the collection.
3. A real **Cancel request** POST succeeded while only its subsequent detail GET was intercepted with 503. The open inspector retained **Cancelled decision recorded**, omitted mutation actions and offered explicit refresh. Removing the interception and refreshing recovered the actual Cancelled request, with no duplicate mutation.
4. Closed the inspector, observed an empty Pending collection, selected All statuses through the visible native Select content and read all four terminal requests, distinguishing Approved/Applied, Rejected and Cancelled. Initial automation attempted the Select's hidden virtual accessibility option; targeting the visible option content completed the interaction.

One initial browser attempt exposed a double-serialized decision body (400). It was corrected to pass the shared identity client's structured object; the subsequent real decision succeeded and a regression asserts its exact wire object. Browser console/network inspection retained that corrected initial 400, the intentional stale 409, injected 503 and normal aborted retired GETs; no additional unexpected console error was observed.

## Presentation and bounded review

Reused `AdminShell`, direct AntD Table/Select/Drawer/Descriptions/Alert, `adminCollectionPagination`, `useAdminDetailLayout`, `access-editor-drawer`, `AccessError`, `useTenantCommand` and shared `confirmAction`. No new CSS, visual tokens or UI kit. Existing owners provide 8/12/16 gaps, 12rem desktop detail labels, stacked mobile labels, 736px bounded drawer, footer padding and 40px mobile action hits.

Grouped captures covered 1440×1000, 390×844 and 320×740. Drawer widths were 736/390/320px; document and inspected drawer content had no horizontal overflow. Footer actions wrapped inside the drawer and remained reachable. The independent Impeccable finish review requested an explicit time-zone label. The shared formatter now uses viewer locale and `timeZoneName: 'short'`; all three confirmed captures show GMT+7. The reviewer scored that finding resolved and returned **ship** at the limited fix-list scope. The independent documenter found an incumbent extension with no design-system change, so design artifacts were preserved.

Temporary evidence and logs: `/private/tmp/tenant-approval-ui-20261017/` (including `desktop-confirmed.png`, `mobile-confirmed.png`, `viewport-320-confirmed.png`), `/private/tmp/tenant-approval-backend.log`, `/private/tmp/tenant-approval-review-finaltests.log`, `/private/tmp/tenant-approval-snapshot-codec.log`, `/private/tmp/tenant-approval-api-finaltests.log`, `/private/tmp/tenant-approvals-web-confirm-tests.log`, `/private/tmp/tenant-approvals-web-finalbuild.log`. Paths are local evidence, not repository assets. Only this task's known disposable servers are stopped after verification.

## Remaining boundaries

The full goal remains active. Original tenant Chat entry/return, notifications, proactive refresh/revocation, broader operation/effect families, complete tenant transport/audio/UI and obsolete-authority/schema cutover remain open. The inspector uses explicit refresh rather than live updates; no effect receipt never implies a completed action. A stopped source retains the existing expiry lifecycle rather than gaining an invented cancellation path.

Browser coverage uses preferences; persona preview/state behavior has integration evidence, but a live browser persona decision, full long-content matrix, keyboard focus restoration and contrast measurement were not separately accepted. Existing optional CI and Real providers were not run. Pre-existing Impeccable context/sidecar status drift is reported without an incidental repair; authoritative product docs track this slice. No full proposal, milestone or multi-tenant deployment acceptance is claimed.
