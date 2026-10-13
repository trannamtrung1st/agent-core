# Admin UX polish verification — 2026-10-13

## Source and scope

Baseline: `d425b1215be0957172f79e12d940c2ea14fd5f03`, the complete Admin reorganization in [draft PR #12](https://github.com/trannamtrung1st/agent-core/pull/12). That baseline's seven hosted gates passed, with its initial unrelated backend recovery failure and same-commit passing recheck retained in the PR notes.

Polished runtime/test source: `9601d9c74c24dcc5fe6a7fdc2928816f16f69eaf`. Subsequent evidence-only commits do not change runtime behavior. This is a bounded frontend refinement of the same Ant Design v6 Operate UI. Backend source, APIs, persisted schema, configuration authority, live conversation/audio ownership and publication gates are unchanged. No new milestone, merge or freeze is claimed.

The frontend, testing, Impeccable polish/Operate and docs-consistency workflows guided this pass. The project context had already been loaded in this session and was reused. An initial batched walkthrough covered all 15 Instance destinations, all ten exact-draft pages, Definition Versions/Drafts and all five global destinations. Findings were fixed at their shared owners. Mandatory test/reflow repairs followed, then one final desktop/tablet/mobile confirmation batch; no further discretionary polish round followed. The automatic design hook reported no deterministic issues on scanned edits; it suppressed further hints after its edit cap. No additional detector pass was substituted for rendered evidence.

## Changes and shared owners

| Finding | Change and owner |
| --- | --- |
| Inventory title repeated above and inside the table | `AdminPageHeading` owns one title, adjacent total and creation action. `InventorySection` keeps search, rows and pagination. New Instance and New Definition use the same primary-action vocabulary. |
| Definition name and route task used the same large title scale | `AdminOwnerHeading` supplies the shared 16px owner/status row and 12px identity metadata for Definition and Instance workspaces. The route title remains primary. Instance ID uses the existing Ant Design copy affordance. Latest Definition status is explicitly qualified. |
| Long breadcrumbs and action labels consumed several narrow header rows | Header owns one row of 40px Menu/Search/Chat controls and compact Return, plus one line of scope breadcrumb. The full owner is available by title and in the main area. Return keeps 8px text-control inner padding; icon hits use zero inner padding. Accessible names remain stable. |
| Drawer body 24px plus navigation 12px doubled the outer inset | Drawer body adds no inset; navigation owns 12px, links own 8px, and group/link spacing consumes existing 8/16px roles. Desktop and Drawer text edges agree. |
| Overview links formed a long undifferentiated strip | Registry categories group Configuration, Governance, Operations and Diagnostics links, using direct Ant Design native link buttons. |
| Search repeated the owner on every result | One exact-owner group and a separate Global Admin group precede concise destination descriptions. Scope uses typed identity, including when an Instance is named Global Admin. |
| Long supporting text spanned full desktop panels | Shared page/panel descriptions, Instance settings introduction and draft introduction use the established 72ch reading measure. Form fields and tables keep their existing width owners. |
| Events duplicated the same page title | Inner section is named Event catalog; its existing inspector and authoring flows retain ownership. |

The canonical frontend specification, shared Admin README, DESIGN.md, Admin surface brief and sidecar preview are synchronized. Existing Ant Design colors, typography family, panels, dialogs, sparse patches, dirty-owner guards and raw native link semantics are reused.

## Rendered walkthrough and observable outcomes

The owned API used Synthetic with disposable `/private/tmp/admin-polish-20261013.db`, `/private/tmp/admin-polish-workspaces`, API 5280, Vite 5373 and matching OAuth callback origin. Profile and speech adapters were explicitly Synthetic. The initial Vite setup used the wrong proxy environment variable; it was corrected to `VITE_API_PROXY_TARGET` before the audited walkthrough. Those setup authorization errors are not product findings. No operator database was reset.

A disposable Examiner v1 Instance and exact draft were created through the UI. Long owner names, default/inherited settings, empty inventories, Built-in Skills/Events, read-only details and sparse editor pages were traversed. The draft's normalized candidate was saved at revision 2; no publication was performed in this walkthrough.

- Inventory creation opened at narrow width, Cancel closed the modal and restored the creation control. A single title carries the inventory count and action.
- Profile edit → Models → scoped search → reasoning result focused `#modelDefaults`. Returning to Profile retained the unsaved name. Chat departure offered Stay; Stay preserved the edit. Explicit Save updated the saved owner header, and the new Instance copy control acknowledged Copied.
- A held 503 owner read showed its local error/diagnostic and left the mutation form unavailable. Explicit Retry restored the saved name. An initial one-shot injection was superseded by React StrictMode's second read; the held failure fixture verified the actual error/recovery path.
- Draft Form/JSON pages retained one candidate. Invalid JSON disabled Save, survived navigation to Policies and back, and remained repairable. Restoring the valid text cleared the error. Published Version inspection opened and closed independently.
- Search empty-results guidance, Escape/opener return, same-owner anchors, native modified/middle links and Back/Stay/Discard are exercised by the local browser suites. Built-in Event inspection opened and dismissed; global privacy remained readable at 390px.
- Shared geometry was checked at 1440/1024/768/767/390/320, with long names and IDs. Drawer body/nav width was 280px; link right edges were 268px and body scroll width 280px, establishing no horizontal Drawer overflow. Scroll remains local for long navigation and tables.

Final MCP console/network evidence contains the deliberately injected 503 read failures; no unexpected application exception was observed. Logs are in `.playwright-mcp/admin-polish-final-console.log` and `.playwright-mcp/admin-polish-final-network.log`. Cancellation of obsolete reads is distinct from a failed write. API/network metadata was inspected without printing protected values.

## Geometry

[Machine-readable metrics](assets/admin-ux-polish/layout-metrics.json) were captured from the running app.

| Viewport | Header height | Page overflow | Main inline inset | Panel heading/body inset | Owner/metadata |
| --- | --- | --- | --- | --- | --- |
| 1440 | 56px | 0px | 16px | 16px / 16px | 16px / 12px |
| 1024 | 56px | 0px | 16px | 16px / 16px | 16px / 12px |
| 768 | 89px | 0px | 16px | 16px / 16px | 16px / 12px |
| 767 | 87px | 0px | 12px | 16px / 16px | 16px / 12px |
| 390 | 87px | 0px | 12px | 16px / 16px | 16px / 12px |
| 320 | 87px | 0px | 12px | 16px / 16px | 16px / 12px |

The scope breadcrumb remains 22px high at all six widths. The 1024/1023 boundary also passed the browser regression. A narrow wordmark-wrap defect found during required reflow checks was corrected at the header's icon/text inset owner; the regression checks a single-line brand and controls inside the viewport.

## Local checks and corrections

| Check | Command from web/ | Result |
| --- | --- | --- |
| Production | `node node_modules/typescript/bin/tsc --noEmit` then `node node_modules/vite/bin/vite.js build` | Pass. Final JS 2,458.49 kB / 751.94 kB gzip. Existing >500kB bundle warning remains. |
| Affected component suites | `node node_modules/vitest/vitest.mjs run src/features/admin/AdminApp.test.tsx src/features/admin/AdminInstanceNavigation.test.tsx src/features/admin/InstanceSettingsSection.test.tsx src/features/admin/EventsSection.test.tsx --maxWorkers=1` | 4 files / 59 tests passed in one final invocation. |
| Affected browser journeys | `node node_modules/@playwright/test/cli.js test e2e/admin-polish.spec.ts e2e/admin-tab-navigation.spec.ts e2e/navigation-links.spec.ts e2e/admin-collections.spec.ts --project=synthetic` | 20/20 passed after fixture corrections; final header-only correction received the focused recheck below. |
| Final narrow header/creation boundary | Same CLI, `e2e/admin-collections.spec.ts e2e/admin-polish.spec.ts --grep 'Definition tabs keep|Admin chrome keeps'` | 2/2 passed after the final source correction; all width, spacing, focus and wordmark assertions retained. |
| Docs/design | Relative references/anchors, fence checks, YAML, sidecar structure/preview parity and `git diff --check` | Pass: 11 Markdown files / 307 relative references; sidecar schemaVersion 2 structure/preview parity; YAML/targets and whitespace. Formal sidecar schema validation is not claimed. |

CLI hosts used `CI=1`, `PLAYWRIGHT_FAITHFUL_MANUAL=1` and independent API/Vite/fixture ports with disposable SQLite paths. Local logs remain under `/private/tmp/admin-polish-*`.

Initial component checks failed two count assertions after counts moved to the page heading and a broad search regex matched several results. Queries were updated to their actual semantic owner; the Modal visibility assertion now waits for its opening transition. The new browser fixture initially fell outside the first catalog page; filtering by exact Instance ID restores deterministic selection. Existing base-version geometry initially sampled before the responsive sidebar settled; it now awaits the 1024px subscription before measuring, retaining the exact 8px neighbor-gap checks. TypeScript rejected Playwright-only `exact` options in the new Testing Library fixture; those were removed because string role names already match exactly there. Intermediate failing/setup invocations are not green gates.

## Captures and acceptance boundary

Seven [initial captures](assets/admin-ux-polish/initial/global-instances-1440.png) and 22 final PNGs are committed under `assets/admin-ux-polish/`. Representative final evidence:

- [Desktop Models](assets/admin-ux-polish/final/models-1440.png), [1024](assets/admin-ux-polish/final/models-1024.png), [768](assets/admin-ux-polish/final/models-768.png), [767](assets/admin-ux-polish/final/models-767.png), [390](assets/admin-ux-polish/final/models-390.png), [320](assets/admin-ux-polish/final/models-320.png).
- [Narrow inventory](assets/admin-ux-polish/final/global-instances-320.png), [Profile](assets/admin-ux-polish/final/instance-profile-390.png), [grouped Overview](assets/admin-ux-polish/final/overview-destinations-390.png), [Resources](assets/admin-ux-polish/final/resources-390.png).
- [Navigation](assets/admin-ux-polish/final/navigation-320.png), [scoped Search](assets/admin-ux-polish/final/search-320.png), [no results](assets/admin-ux-polish/final/search-empty-320.png), [owner read failure](assets/admin-ux-polish/final/owner-error-320.png).
- [Definition Versions](assets/admin-ux-polish/final/definition-versions-1440.png), [draft Models](assets/admin-ux-polish/final/draft-models-1440.png), [invalid JSON](assets/admin-ux-polish/final/draft-invalid-json-320.png), [Events](assets/admin-ux-polish/final/events-1440.png), [global privacy](assets/admin-ux-polish/final/privacy-390.png).

The [PR checks](https://github.com/trannamtrung1st/agent-core/pull/12/checks) test the latest exact head. They are a separate gate from this local evidence and were pending when this report was saved; the final handoff and PR notes record their completed outcome. The original reorganization's green baseline is not substituted for this polish's exact-head acceptance. Live-provider, real OAuth delivery, actual microphone/speaker and manual assistive-technology checks were not run. The existing bundle warning and Ant Design migration/jsdom warnings remain distinct from assertion results. Owned temporary hosts are stopped during cleanup; unrelated services and operator data are preserved.
