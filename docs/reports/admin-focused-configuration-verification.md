# Admin focused configuration and actions verification — 2026-10-13

Base: `764994312191c950aa2bfbc0fb7659417a454e17`. This follow-up implements the supplied screenshots and requested navigation, branding, table-action and configuration-editing changes. Source and evidence ship together. Hosted CI is not a completion gate for this request, as explicitly requested; no new milestone, freeze, live-provider or device acceptance is claimed.

## Result and ownership

- `adminNavigation.ts` owns 27 focused Instance and 19 exact-draft destinations. Instructions, Conversation, Behavior, Initiative, Voice, Models, Providers, budgets and policy owners are separate. Profile, Definition version and Lifecycle are distinct. Capability authority and credential bindings are separate. Old combined leaves and approved field anchors migrate to the corresponding owner; existing native links and guards remain.
- `AdminNavigationIcon` uses the existing Ant Design outline family. All 27 Instance navigation items have decorative icons and stable accessible names. Contextual Global integrations & platform is removed; actual global destinations remain reachable from collection navigation and scoped Search.
- `AdminShell` always brands Agent Core with secondary Admin text. A native vertical Divider separates desktop breadcrumbs. The narrow wordmark and all three header actions fit at 320px; only tight header gaps consume native `paddingXXS` (4px). Existing 8/12/16 roles, single Drawer inset, dark theme and 1024px navigation breakpoint remain.
- `admin-owner-actions` supplies one bordered, rounded, sticky frame with a 16px inset for both Definition draft and Instance configuration actions.
- `InstanceConfigurationProvider` coordinates the existing persona, sparse settings, budget, self-management and unattended-model commands. Saves pass the preceding receipt revision to the next write, stop on error and retain uncommitted edits. Writes are sequential, not atomic across endpoints. Refresh preserves edits; Discard clears pending configuration edits without clearing saved overrides. Owner replacement fences following writes. Collection dialogs and consequential lifecycle/version/freeze actions retain their explicit controls.
- Inherited sparse settings are editable immediately. Changing only a field stages only that field’s override. Per-field reset still clears only that override and unchanged fields inherit. Stable label rows prevent source/reset/restriction indicators from moving inputs. Inherited self-management area controls are also directly editable; explicit inheritance controls remain.
- Familiar table operations use tooltipped icon buttons. Resources, credentials, Event secondary operations and publication deprecation use native More menus with an icon before every label. Existing permission/revision/loading fences and confirmations remain. Resource Edit and credential Replace keyboard flows restore focus to More; deleted triggers fall back to creation controls. Credential bindings has one page heading and a distinct Bound credentials collection heading.

The frontend specification, Admin README, DESIGN.md, surface brief and static sidecar preview match the implementation. The sidecar uses actual installed Ant Design SVG definitions. No second UI kit, alternative backend architecture, API/schema change or provider call was introduced.

## Exercised Synthetic outcomes

Owned native hosts used API 5280/Vite 5373, Synthetic speech adapters and a disposable `/private/tmp/admin-enhancement-20261013.db`, attachment root and workspace root. CLI suites used separate API/Vite/fixture ports and disposable SQLite paths; no operator database was reset.

1. Created a General Assistant v24 Instance. Edited persona, inherited Instructions and Conversation on separate pages; revisiting retained the drafts. One Save persisted all edits. Added budget, self-management and unattended-model changes and saved together. Browser regression read the real endpoints and asserted the saved persona, sparse patches, budget preset, policy and model.
2. Failed the second sparse write with an injected 503. The first section committed, the error disclosed that partial success and the other field stayed staged. Refresh obtained current revisions without losing input; explicit Save recovered. Per-field Reset followed by Save removed only that field override. Discard restored the saved field without another write.
3. Measured Language before/after a source marker: relative input top stayed 40px. At 320px the long Maximum consecutive proactive turns label retained a 53px input offset after a real edit and visible unsaved marker. No field movement occurred.
4. Walked every Instance and draft destination. Forked a General Assistant v25 draft, edited Instructions and saved revision 2. Invalid Advanced JSON blocked Save, survived navigation to Memory policy and back, and remained repairable. Publication was not performed in the MCP walkthrough; the existing browser authoring scenario still exercises gated publication.
5. Entered scoped Search and followed the reasoning alias at desktop and mobile. `#modelDefaults` received focus below the sticky bar. Native modified/middle links, same-owner history and dirty Stay/Discard remain covered by browser tests.
6. Imported a disposable Instance resource. Opened More → Edit with the keyboard, cancelled and returned focus to More. Created a disposable synthetic credential; More → Replace → Cancel also restored focus. Every observed menu item had one decorative icon. Normal import validation still required an explicit kind for an unclassified path.
7. Drawer Escape returned to its opener after the close transition. The final captures wait for transitions and use fresh resting views to exclude transient toast/mask overlays.

## Responsive evidence

[Measured geometry](assets/admin-focused-configuration/layout-metrics.json), at 900px height:

| Width | Page overflow | Header | Main inset | Label minimum | Actions border/inset |
| --- | --- | --- | --- | --- | --- |
| 1440 / 1024 | 0px | 56px | 16px | 32px | 1px / 16px |
| 768 | 0px | 89px | 16px | 32px | 1px / 16px |
| 767 / 390 / 320 | 0px | 87px | 12px | 40px | 1px / 16px |

The Admin marker stays inside the brand at every width. At 320px its right edge has 8.71px clearance before the action group. The navigation Drawer shows 27/27 icons without an additional body inset. Six initial captures and 12 final captures are committed; representative evidence:

- [Desktop configuration](assets/admin-focused-configuration/final/conversation-final-1440.png), [768](assets/admin-focused-configuration/final/conversation-final-768.png), [390](assets/admin-focused-configuration/final/conversation-final-390.png), [320](assets/admin-focused-configuration/final/conversation-final-320.png).
- [Narrow navigation](assets/admin-focused-configuration/final/navigation-final-320.png), [desktop draft actions](assets/admin-focused-configuration/final/draft-final-1440.png), [narrow draft](assets/admin-focused-configuration/final/draft-final-320.png), [invalid draft](assets/admin-focused-configuration/final/draft-invalid-final-320.png).
- [Resource actions](assets/admin-focused-configuration/final/resource-actions-final-1440.png), [credential actions](assets/admin-focused-configuration/final/credential-actions-final-1440.png), [desktop inventory](assets/admin-focused-configuration/final/inventory-final-1440.png), [narrow inventory](assets/admin-focused-configuration/final/inventory-final-320.png).

## Local checks and acceptance limits

- TypeScript `tsc --noEmit` and Vite production build passed. The existing large-bundle warning remains.
- Twelve affected Vitest suites passed **121/121** in one invocation (`/private/tmp/admin-enhancement-unit-final.log`). Draft-candidate and memory suites passed another **24/24** (`admin-enhancement-draft-memory-unit.log`): **145 tests in 14 files**. The final owner/menu repair also passed its focused 12-test recheck.
- Five main Synthetic browser suites passed **17/17** in the final invocation (`admin-enhancement-final-browser.log`): configuration actions, Admin polish, canonical navigation, native links and scoped settings.
- Five extended suites cover **18 distinct cases**: collections, self-management, resource import, Skills and System Credentials. The first run passed 16 and exposed two obsolete selectors; the corrected full upgrade/rollback journey passed 1/1 (`admin-enhancement-version-recheck.log`) and both complete credential cases passed 2/2 (`admin-enhancement-credentials-complete.log`). All **35 distinct affected browser cases** have passing final evidence; the initial failing run is not counted as a green invocation.
- Relative Markdown links/anchors and fences, YAML/target references, sidecar schemaVersion 2 structure and native preview parity, and whitespace passed. Formal sidecar schema validation is not claimed.

Earlier failures were retained and repaired: removed Customize selectors; scoped child Save controls moved to the shared bar; combined-page helper names; old credential URLs; an unscoped status query; a version navigation helper that returned to Profile; and Playwright-only `exact` passed to a Testing Library query. The new endpoint-read test initially used the wrong effective-config path, then verified the correct existing endpoint. The first reflow pass found the narrow brand overlap; native token spacing and secondary Admin text fixed it. An immediate Drawer focus read preceded its closing transition; awaiting the actual focus return verified recovery. Capture export was refreshed after transition/toast settling, without another discretionary visual redesign.

MCP console/network logs are `.playwright-mcp/admin-enhancement-final-console.log` and `.playwright-mcp/admin-enhancement-final-network.log`; observed errors were the initial favicon 404 and deliberately injected write failure. The main browser route walkthrough asserted no page exceptions. Backend suites, hosted CI, manual assistive technology, real OAuth/provider calls and actual microphone/speaker checks were not run for this frontend refinement. Temporary owned hosts are stopped during delivery; unrelated services and operator data are preserved.
