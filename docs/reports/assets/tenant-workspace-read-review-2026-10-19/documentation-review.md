# Workspace read Review — fresh documentation handoff

disposition: ship

Reviewed on 2026-10-19. This completes the fresh documentation handoff for the exact tenant Workspace read Review checkpoint after the [finish review](finish-review.md) returned `ship`. It is an ordinary Operate/code-led extension of the shared native Ant Design v6 approval inspector. The full proposal and milestone acceptance remain outside this disposition.

## Current source and design agreement

The [surface direction](../../../../.impeccable/surfaces/web-src-features-access-tenantapprovals-tsx.md), [Product context](../../../../.agents/context/PRODUCT.md), incumbent [Design system](../../../../.agents/context/DESIGN.md), current shared approval source and canonical interaction requirements agree on the existing visual system. No new world, comp, seed, component kit or visual recipe is needed.

| Contract | Current source agreement |
| --- | --- |
| One shared inspector | `TenantConversationApprovals` imports `TenantApprovalDetail` and `approvalResultLabel` from `TenantApprovals`; Organization and Chat retain the same detail, confirmation and result owners. |
| Exact saved observation | `inspect_workspace` is classified as a read. The saved section is **Exact saved read** and `path` is labeled **Exact path**. Existing `Typography.Text` renders values as escaped, pre-wrapped text with anywhere wrapping. Page, disclosure and read-boundary rows use the existing native Descriptions composition. |
| Consequential decision | `confirmAction` retains the centered native confirmation, saved summary/Agent/organization, **Keep reviewing**, and the exact one-continuation consent boundary. Approval grants no file changes, recipient download access or later-read authority. |
| Decision versus consent | The decision acknowledgement explicitly says the operation result is separate. A completed `inspect_workspace` consent result is **Read authorized**, including in Chat; it does not claim file mutation or result delivery. |
| Changed source and access | Existing server-derived decision eligibility fences Approve/Reject, while cancellation retains its independent eligibility. `workspace_source_changed` requests a fresh exact read; HomeAccess disablement has its own explanation. Refresh and no-automatic-resubmission copy remain in the shared inspector. |
| Incumbent composition | Native large, viewport-capped Drawer, wrapping footer, semantic Alerts/Tags, responsive `useAdminDetailLayout`, shared focus ownership and 8/12/16 spacing remain. `useAdminDetailLayout` keeps 12rem top-aligned labels at the native `md` breakpoint and stacked mobile labels. |

The read preview maps saved protected metadata; it adds no source-body editor or directory-entry view. The existing publication-only content branch remains separately classified. The protected backend projection, rather than a UI filter alone, owns the no-body/no-directory-name guarantee.

The [finish reviewer](finish-review.md) independently inspected all 13 supplied final captures and found the expected presentation at 1440/390/320px, including scrolled exact-path/result boundaries, settled confirmation, Chat and changed-source states. This documentation pass inspected source and recorded evidence; it did not independently repeat visual or runtime acceptance.

## Documentation ownership

| Fact | Owner |
| --- | --- |
| Exact saved read, independent authority, source witnesses, consent receipt and successor/checkpoint semantics | [Backend Interfaces](../../../04-backend-interfaces.md#tenant-workspace-read-review) |
| Shared inspector classification, copy and retained layout owners | [Frontend Implementation](../../../13-frontend-implementation-spec.md#workspace-read-consent-inspector) |
| Decision/consent distinction, scope boundary and changed-source interaction | [IAM UX](../../../19-multi-tenant-iam-ux.md#exact-workspace-read-review) |
| `inspect_workspace` request/projections and separate `applied=false`, `readAuthorized`, observation and no-replay facts | [Protocol](../../../14-api-and-realtime-protocol.md#tenant-workspace-read-review-protocol) |
| Required focused consent scenarios | [Testing Strategy](../../../16-testing-strategy.md#tenant-workspace-read-consent) |
| Executed checks, actual browser/host-recreation evidence and limitations | [Checkpoint verification](../../tenant-workspace-read-review-verification-2026-10-19.md) |
| Current summary and bounded supersession of older pending notes | [README](../../../../README.md) and [Implementation Plan](../../../18-implementation-plan.md#tenant-workspace-read-review-checkpoint--2026-10-19) |
| Presentation system and tooling context | Existing `.agents/context/DESIGN.md`, `.agents/context/PRODUCT.md` and `.impeccable/design.json`; `/docs` wins on behavioral conflicts. |

README and the implementation plan both describe exact home/working read/list Review as this bounded checkpoint and retain mutation/search/virtual-resource/Office/publication conversion, autonomous origins, other proposal families and final authority/schema cutover as open. Their newer bounded statements explicitly supersede older workspace-read-Review-pending notes. Historical Product/Design and sidecar evidence is not a new product specification or a current-tree acceptance claim. A dedicated global Design token or component entry would duplicate this unchanged inspector recipe; the current surface direction and canonical frontend/interaction owners carry this extension.

## Preserved world and preexisting drift

The dark Ant Design v6 algorithm, resolved `AppShell` token bridge, system typography, native Drawer/Modal/Descriptions/Alert controls, existing spacing and mobile/focus behavior are preserved. No CSS, token, markup/layout owner, font, image, route or kit was changed by this handoff. No shipping raster asset or new provenance obligation was added; the supplied PNGs are review evidence.

Read-only SHA-256 comparisons against the historical `extensions.inspection.sourceSha256` entries show:

| File | Observation |
| --- | --- |
| `web/src/app/antdTheme.ts` | Matches the historical fingerprint. |
| `web/src/app/AppShell.tsx` | Matches the historical fingerprint. |
| `web/src/features/admin/useAdminDetailLayout.ts` | Matches the historical fingerprint. |
| `web/src/app.css` | Differs from the original inspection fingerprint; matches the later recorded `3b86dd33…` documentation-refresh fingerprint. This is already-recorded historical evolution. |
| `.agents/context/DESIGN.md` and `.agents/context/PRODUCT.md` | Differ from the original inspection and prior bounded refresh hashes. Their later scoped context merges and historical claims remain preserved; this handoff does not re-extract or rebase them. |
| The two current approval TSX files | Not fingerprinted in the original inspection; absence is not a mismatch or fresh acceptance. Current agreement is established by the named source/direction/evidence review above. |

The sidecar remains schema version 2 with its existing `generatedAt`, inspection base commit, component previews, narrative and scoped refresh records. The older inspection explicitly describes a historical checkpoint. Existing broad context statements about remaining tenant work keep their original scope; newer canonical checkpoint owners govern the exact supported path. No drift is silently repaired or promoted into new normative guidance.

Preserved file SHA-256 values for this handoff:

- `.agents/context/DESIGN.md`: `22995f36649d8c7d12788960e9c004761d434302aaeaba1925a4f361070cc522`
- `.impeccable/design.json`: `de57eea0b00d5b638772d5474afd2dfdef402104fd4adfc5c03cc169949faf79`

## Completion and limits

No documentation correction or implementation change is required for this bounded checkpoint. The only write is this report; Design and sidecar remain intact. The earlier verification report's pending fresh-documentation handoff is fulfilled by this artifact. Its parent-owned final documentation checks and any broader proposal work remain separate.

The verification report owns the 191 distinct selected local automated cases, passing frontend build, actual SQLite application-host recreation, actual Synthetic browser approval/changed-source cancellation and focus/recovery evidence. The finish report owns the capture validity/presentation disposition and supplied empty detector result. This documenter ran no browser, application, build, test, server, context launcher or detector, inspected no shipped agent-definition files and claims no additional runtime, live-provider, hardware, CI, full-proposal or milestone acceptance.
