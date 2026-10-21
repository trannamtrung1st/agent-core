# Runtime State design comparison — 2026-10-21

Ordinary code-led Operate extension. Comparison authority is the incumbent `.agents/context/PRODUCT.md` / `DESIGN.md`, existing Instance surface briefs and built shared compositions. No approved comp, new-world direction or replacement system exists. `.impeccable/config.json` retains its historical `buildPath: comp`; this pass does not change that global setting or recast this ordinary extension as comp-led.

## Source comparison

`TenantAgentState.tsx` imports direct native Ant Design controls and the existing configuration panel, Instance heading and responsive record layout. `AccessApp.tsx`/`accessRoute.ts` keep State under Activity and separate from State & Isolation. `antdTheme.ts` retains darkAlgorithm and its existing primary seed; `AppShell.tsx` maps resolved theme roles to CSS properties. `app.css` supplies the existing bounded 48rem column, shared panels and narrow control rules. No State-local CSS, colors, typography, shadows, asset or wrapper system is introduced.

The source uses 16px section / 8px compact token gaps and the shared panel default 12px body gap. `useAdminDetailLayout` retains horizontal 12rem labels at medium/wider screens and vertical wrapping details below. Shared page, Instance and panel titles retain their native hierarchy. Textual partial/restricted/Unknown states preserve observation limits; the expiry branch removes count panels and the error branch replaces the private view. These source facts do not independently qualify runtime authorization or functional recovery.

Behavior authority remains [Runtime State interaction contract](../../../19-multi-tenant-iam-ux.md#runtime-state-interaction-contract). Read-only counts cover current authorized scope, null/missing renders Unknown, expired counts hide and denied refresh retires protected contents. Collection grants no mutation/allocation authority and starts no work. Source-specific permission and passive model/runtime constraints remain in canonical docs.

| Compared source | SHA256 at inspection |
| --- | --- |
| `web/src/features/access/TenantAgentState.tsx` | `69956cdc56c698ce8477e1837cef1147d99fce1aa245ac6fda99fe3536be4ee4` |
| `web/src/features/access/AccessApp.tsx` | `0d2add4dba924e8145c4da065a9a77a32ecf3898d076feeb74342060a1b972ad` |
| `web/src/features/access/accessRoute.ts` | `6a517f04b2913ed50b564ea2010a287fb050141cd05eb97baf15a6bbe3fecf64` |
| `web/src/features/access/TenantAgentHeading.tsx` | `32e80f91fa6bbfc4a0e07952801d23054f58de66898d8be077f1579c6750ef7e` |
| `web/src/features/admin/AgentConfigurationLayout.tsx` | `b36bde3bb2f8a11cc14abb20f29df25656b9ee2ee5490b1663d724513589e73b` |
| `web/src/features/admin/useAdminDetailLayout.ts` | `b0f7d9585d713164dd1cf79a44b3c427a9dade81fa6d8df58c2bee2e86593c71` |
| `web/src/app.css` | `f750dd4f8126c888e6a0e67a69f4afa03b63abafd49cac1facccd730e373301a` |
| `web/src/app/antdTheme.ts` | `64f582375261f51bb82b2041b8fef9f872e13929b309ad3978011619f5ac2e66` |
| `web/src/app/AppShell.tsx` | `35826591f0e988cd4a1ac068c23b61fd05404fb8a6a3a1f494832cd32a5998d1` |
| `docs/19-multi-tenant-iam-ux.md` | `af9416b19f7fd46e8849fcdbdb045da00ab057b62177be97848f800b296fe1b7` |

## Capture evidence

The fresh [finish reviewer](finish-review.md) returned `disposition: ship`, without material fixes, for the supplied visible regions. This documenter reopened each listed PNG and checked dimensions and fingerprints. All images show loaded content rather than blank/malformed regions.

| File | Pixels | Visible evidence | SHA256 |
| --- | --- | --- | --- |
| [desktop.png](desktop.png) | 1440×1100 | Page/shell, freshness and partial-state alert, Sessions, most of Runs; lower Run edge and Browser/Sandbox excluded. | `f3ffff5519e5a99df23295a4591c0eb6779d572c9aa685c886b26ed683c87f1f` |
| [mobile.png](mobile.png) | 320×844 | Narrow page top, wrapping Instance/freshness copy and partial-state alert; ends at Sessions heading. | `d5d5fa9bbad3983efbcafde2ffb661eb3ac9d248f268718dbcd50dda200f3ba1` |
| [restricted-runs-320.png](restricted-runs-320.png) | 320×844 | Internally scrolled Runs panel: read restriction, six stacked Unknown values and Activity link; only Browser heading begins below. | `5e67f4e56f95b8dc9fddbb004b3c764943f38c38aa641554eef4b3ca73dbc110` |
| [revoked-390.png](revoked-390.png) | 390×844 | Denied view: private details absent, native access explanation and surviving Back/Refresh controls. | `01f875c5a384fceeb4b8ae4a56bd2e25866e7cfd8451fda1c8e49d1e2481f543` |

Acceptance is limited to these regions of the internal scroller and their shared composition. It excludes Browser/Sandbox lower-panel presentation, whole-page/five-width coverage, expired/loading/hover/focus visuals, keyboard interaction, measured contrast and full behavioral correctness. No new render, detector run or application test was performed by this documentation pass. The supplied `/private/tmp/tenant-runtime-state-verification/detector.json` parses as `[]`; that result has its original execution scope. Parent implementation verification is separate evidence. No full proposal, design-system or milestone acceptance is inferred.

## Preserved system and reported drift

| Preserved artifact | SHA256 before/after documentation |
| --- | --- |
| `.agents/context/PRODUCT.md` | `91d150e831221099db68188545cf728f92ac4ccce96afee97df9cf3f6ebac8c4` |
| `.agents/context/DESIGN.md` | `88ce20c359a4f228304dcde03aecefe73957674e2f1fa4d75a1f4f912b1c0c44` |
| `.impeccable/design.json` | `8bdc3659a25b706454ac8cc0394323c5e0e60ea1ade616756b180efbaecef49b` |

Historical sidecar source metadata is stale for the following inspected incumbent files; the theme source still matches. The fingerprints are retained as historical evidence, not rebased by this ordinary extension.

| Historical source record | Current comparison |
| --- | --- |
| `.agents/context/PRODUCT.md` | drift: stored `e2bdbdf0696ebfe2c07d82670c0ad6d33b475cf246f930a8123f5cc19a804eaa`; current `91d150e831221099db68188545cf728f92ac4ccce96afee97df9cf3f6ebac8c4` |
| `.agents/context/DESIGN.md` | drift: stored `4002b76912eaf27e79c60271df0b610d995cab98ebb28cce57c04554ecbb5c6a`; current `88ce20c359a4f228304dcde03aecefe73957674e2f1fa4d75a1f4f912b1c0c44` |
| `web/src/app.css` | drift: stored `dc468ad01500703521a4f484455e71531646aa453e21c5379d5f7051c9e111bd`; current `f750dd4f8126c888e6a0e67a69f4afa03b63abafd49cac1facccd730e373301a` |
| `web/src/features/access/AccessApp.tsx` | drift: stored `7fb72d4986d97ea78e30c0e435850065ed52d3d7bda505ddaf70dd481b7c4190`; current `0d2add4dba924e8145c4da065a9a77a32ecf3898d076feeb74342060a1b972ad` |
| `web/src/app/antdTheme.ts` | matches: stored `64f582375261f51bb82b2041b8fef9f872e13929b309ad3978011619f5ac2e66`; current `64f582375261f51bb82b2041b8fef9f872e13929b309ad3978011619f5ac2e66` |

Pre-existing orphan surface briefs `.impeccable/surfaces/web-src-features-admin-adminapp-tsx.md` and `.impeccable/surfaces/web-src-features-chat-approvalmodal-tsx.md` target absent `web/src/features/admin/AdminApp.tsx` and `web/src/features/chat/ApprovalModal.tsx`. Historical sidecar source mentions remain. These are mention-only findings: no repair, broad extraction or canonization is authorized.

Only the new [Runtime State surface brief](../../../../.impeccable/surfaces/web-src-features-access-tenantagentstate-tsx.md) and this comparison record were written. PRODUCT.md, DESIGN.md, `.impeccable/design.json`, code and global documentation remain untouched by this pass.
