# Tenant State & Isolation design comparison — 2026-10-21

Disposition: preserve the established Operate system. This ordinary local extension needs a scoped brief and evidence comparison, without design-token or identity changes.

| Incumbent requirement | Current implementation and observed region |
| --- | --- |
| Dark Ant Design v6 native controls, one panel inset, existing type and 8/12/16px gap roles | `TenantAgentIsolation` reuses `AgentConfigurationPanel`, direct AntD Form/Select/Button and shared State toggle fields. Desktop and narrow top captures retain the existing shell, panel hierarchy and field presentation. No replacement styling is introduced. |
| One canonical coordinated editor | State policy, Memory, Voice enablement and learning eligibility share one local draft and deliberate coordinated Save. Existing policy-field and panel owners remain shared. |
| Product-language preview with inheritance and retention context | The corrected sibling `IsolationChangePreview` uses shared owner/field/choice labels, complete before → after text, source and notice. Its responsive Descriptions align wide labels and stack narrow labels; inspected actions wrap within the preview panel. |
| Explicit recovery without replay | The stale-390 region shows the current-read warning, disabled Preview/Save and retained-draft guidance. Functional recovery belongs to the separately executed CLI journey below. |

Sources compared: `.agents/context/PRODUCT.md`; normative DESIGN frontmatter and Overview/Layout/shared Admin composition guidance; docs/19 coordinated State & Isolation contract; `TenantAgentIsolation.tsx`, `IsolationChangePreview.tsx`, `StateIsolationPolicyFields.tsx`, `AgentConfigurationLayout.tsx`, `stateIsolationPresentation.ts` and `useAdminDetailLayout.ts`. `/docs` retains behavioral authority. This documenter opened all six final PNGs and recorded their dimensions/fingerprints:

| Capture in `.impeccable/review/tenant-isolation/` | Pixels | SHA256 |
| --- | --- | --- |
| desktop.png | 1440×1100 | `311245581e6e9652e7c58afbb22f05d5f13b2bd99a3bf75e145aa0bdf018822e` |
| desktop-preview.png | 1440×1100 | `260b522331ad3e97eac3ba3896e2f6bbc900bdc6c1fb831efdb253c83ce267b8` |
| mobile.png | 320×844 | `c611dff20878b664fc94e492b8a19a22737e0123b4d35daa1ed47fc8bb0fffc1` |
| mobile-preview.png | 320×844 | `380ba8f838d2b84404e3362d5c06580c999c4a666ce6275aa31cb41ff7114ee6` |
| mobile-actions.png | 320×844 | `353e273a8c07132d247b28f3ca8fa2e42da56a0b3cfa3f95dd6286d12fcc13c4` |
| stale-390.png | 390×844 | `14002476e159bb8995fe9f4839b60e85acdfdc499d2951035d5d373e946ce362` |

`finish-review.md` records SHIP after the sole material raw-label finding was resolved. The initial review covered four original visible regions; its correction recheck opened the two corrected populated previews. The final 10.9-second CLI run subsequently refreshed all six files using identical UI source after only E2E assertions were added. The reviewer did not re-review this final refreshed set. This documenter reopened all six current images as static evidence and replaced their fingerprints above. They are scrolled portions, not complete-page captures; the desktop preview heading is partly above the viewport. The verdict does not supply five-width, keyboard/focus/hover, loading/empty/read-loss or measured-contrast acceptance, broad shared-consumer acceptance or runtime/native isolation enforcement.

`browser-final.log` records one passing final native CLI journey in 10.9 seconds. The implementation handoff identifies normal save, stale retained-draft/current reload/fresh preview/save, and lost-reply read recovery without replay. The final run also checked that generic Settings shows its coordinated-editor link, omits independent Memory/State sections and retains Voice ID without an Enabled switch; there is no dedicated Settings capture here. The handoff separately reports 81 passing focused API cases in 1 minute 39 seconds, 17 passing Application cases, 26 passing focused UI cases and a passing build. `detector.json` contains `[]` from the initial scan; there was no detector rerun. Playwright MCP returned `Transport closed`, so no independent MCP actions were verified. This documentation pass runs no app checks and claims no full proposal or milestone acceptance.

Only the new scoped brief and this comparison are written by this pass. PRODUCT, DESIGN, tokens, sidecar, configuration and existing briefs remain untouched. Sidecar SHA256 is `8bdc3659a25b706454ac8cc0394323c5e0e60ea1ade616756b180efbaecef49b`. Pre-existing stale design metadata and the orphaned AdminApp/ApprovalModal briefs remain reported, unrepaired and unqualified.

A subsequent console/network confirmation passed the same journey in 9.5 seconds without overwriting these reviewed captures. Expected lifecycle-cancelled GET reads and the deliberate lost save reply are separated from unexpected failures. This additional functional evidence does not expand the static visual verdict.
