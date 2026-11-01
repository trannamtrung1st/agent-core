# UI layout review and polish — 1 November 2026

Completed for the scoped local Synthetic review. Baseline: `21fc8971fea5af3a084d7dc8b75336d13d6cc161`. Changes remain in the working tree. This is a presentation refinement of the existing Ant Design v6 navy interface, not milestone or deployment acceptance.

## Plan and coverage

The completed plan was: inventory existing routes and shared layout owners; capture and assess wide/narrow UI and content/state boundaries; fix demonstrated defects at those owners; exercise affected tasks and recovery; confirm screenshots and retain evidence.

- **72 administration destinations/route variants:** 17 organization pages, 18 Agent pages, 6 platform pages, 21 Definition draft pages, 3 model pages, 2 Account pages and 5 Space pages. The two route-visit manifests record exact paths and widths.
- **10 inherited settings sections**, including the long capability list, plus Cooperation and Chat visibility.
- **13 editor variants:** provider connection, credential, Secret, webhook, Sandbox profile, Space, invitation, role, Evaluation Suite, Definition, Agent, completed-work review and organization creation.
- **Chat and overlays:** empty/populated conversation, model/composer controls, retained draft, files and protected Markdown/CSV/XLSX/Word previews, Tasks, Goals, Workflows, Sandbox and Background work. Synthetic voice covered capture/playback, mute/unmute and reconnect.
- **State samples:** normal, empty, loading, unavailable, disabled, read-only, validation failure, saved success, dirty-navigation confirmation, stale edit/recovery, long labels and a full organization catalog. These are representative shared states, not every possible role × data × transition combination.

The evidence contains **226 baseline and 339 confirmation PNGs**. Desktop/mobile contact sheets were visually evaluated; targeted originals checked fine details and 320px boundaries. Primary captures use 1440/390; representative checks include 1024/768/320 and 480/481 around the changed breakpoint. The long-picker case also uses 320×568. Dialog-element crops are identified separately from viewport captures. No generated design imagery was introduced.

## Demonstrated defects and corrections

| Finding | Shared owner and result | Evidence |
| --- | --- | --- |
| Narrow Chat actions reduced organization context to a few characters | ChatHeader/TenantChatEntry keep named 40px icon buttons at ≤480px. Four toolbar controls align with 8px gaps; the organization selector retains at least 140px in the tested phone widths. | [Before](assets/ui-layout-polish-2026-11-01/before-chat-320.png), [after](assets/ui-layout-polish-2026-11-01/after-chat-toolbar-320.png) |
| Long organization names shrank the icon as well as the name | TenantSwitcher preserves native icon width; only its text truncates. | [Final 320px capture](assets/ui-layout-polish-2026-11-01/after-chat-toolbar-320.png) |
| Mobile Selects remained 32px beside 40px text fields/buttons; several Account modals missed the editor policy | Existing access/editor CSS owns native 40px control hits across pages and portals. Composite fields retain native inner padding, and multi-line controls can grow. Account dialogs reuse the existing editor class. | [Provider before](assets/ui-layout-polish-2026-11-01/before-editor-connection-320.png), [after](assets/ui-layout-polish-2026-11-01/after-editor-connection-320.png); [invitation after](assets/ui-layout-polish-2026-11-01/after-editor-member-320.png) |
| Side actions squeezed setup/recovery explanations into a very narrow column | Shared phone Alert rules move actions below explanatory content with the existing compact gap. At 320px, the sampled setup text retains a 202px column. | [Before](assets/ui-layout-polish-2026-11-01/before-route-models-models-320.png), [after](assets/ui-layout-polish-2026-11-01/after-models-setup-320.png), [read failure](assets/ui-layout-polish-2026-11-01/after-models-read-error-320.png) |
| A full organization picker grew to 1744px on a 900px viewport | Account/access modals reuse the existing centered, viewport-bounded Modal container and scrolling body. A full catalog remains inside a 320×568 viewport; paging/search remain usable. | [Unbounded](assets/ui-layout-polish-2026-11-01/unbounded-organization-switcher-320.png), [bounded](assets/ui-layout-polish-2026-11-01/after-organization-picker-bounded-320.png) |

Existing AdminShell page insets, configuration rows/panels, collection toolbars and Ant Design tokens remain the spacing owners. No per-page offsets, generic control wrappers, second UI kit, palette, state/transport redesign or backend changes were introduced. The [frontend specification](../13-frontend-implementation-spec.md#responsive-presentation) records the presentation policy; DESIGN.md remains the incumbent visual baseline.

## Functional verification

**15 unique browser scenarios have passing latest results**, plus **9 focused unit tests** and the final production build. These counts exclude repeated baseline runs and repaired test attempts.

| Check | Observed result |
| --- | --- |
| `pnpm exec playwright test --config playwright.experience.config.ts e2e/product-experience.spec.ts` | **9 passed.** Sent/received completed Synthetic replies; retained an unsent draft through administration; previewed files; saved/reset/reloaded settings; preserved a dirty draft; verified read-only controls and denied writes; authored/published/started/signed off a Workflow; recovered a stale Space edit; saved/reloaded Environment data; exercised collection actions and Office cells. This broad run preceded the final icon/modal containment corrections. |
| `pnpm exec playwright test --config playwright.experience.config.ts e2e/ui-layout-consistency.spec.ts` | Final-source **first 3 passed together**: seven-width toolbar geometry with long organization name, 13 editors at four widths, invitation validation → save → retained invitation, loading → failed read → explicit successful Reload. The fourth long-catalog scenario has a passing focused rerun after correcting an overstrict assertion that required scrolling even when desktop content fitted. It also verified paging/search, Escape and focus return. |
| `pnpm exec playwright test --config playwright.experience.config.ts e2e/tenant-voice.spec.ts` | **2 passed**, 1440/390: capture continued during output, heard receipts remained separate, mute/unmute worked, reconnect released resources and required a new Voice click. [Mobile voice](assets/ui-layout-polish-2026-11-01/after-voice-muted-390.png). |
| `pnpm run test --run src/features/access/TenantExperienceReview.test.tsx src/features/access/TenantAgentConfiguration.test.tsx` | **9 passed on final UI source**, including saved provenance/consent, stale/lost-read retirement, scope changes, persona persistence and read-only settings. |
| `pnpm run build` / `git diff --check` | Passed on final UI source. Existing chunk-size, SignalR annotation and mixed static/dynamic-import warnings remain. |

Playwright MCP also exercised a real Chat reply, editor opening/dismissal, toolbar tools and Account/organization navigation. Rendered geometry confirmed 40px provider fields, a readable alert text column, non-overlapping toolbar controls and bounded Modal content. Console inspection showed no product warnings/errors. Navigation/StrictMode aborts were distinguished from failures; the focused failure scenario intentionally returned 503 before successful recovery.

The host used fresh disposable SQLite roots at `/tmp/agent-core-layout-audit-runtime`, API `127.0.0.1:5088` and Vite `127.0.0.1:5179`, with `/health` confirmed Synthetic. Browser/OpenSandbox execution and live providers stayed disabled. The initial Vite startup omitted MessagePack: the voice wire interceptor could not observe its binary receipt. Restarting the task-owned server with `VITE_AGENTCORE_REALTIME_PROTOCOL=messagepack` resolved that setup mismatch; both voice checks then passed. Initial editor-test failures were selector/composite-field assertions and were corrected without weakening the product checks. The task-owned servers/browser have stopped, both ports are closed and the disposable database roots were removed; screenshot originals remain retained locally.

Reproduction requires the documented disposable Synthetic host, then:

```sh
CANONICAL_SYNTHETIC_URL=http://127.0.0.1:5179 \
CANONICAL_SYNTHETIC_API_URL=http://127.0.0.1:5088 \
EXPERIENCE_CAPTURE_ROOT=/tmp/agent-core-layout-review \
pnpm exec playwright test --config playwright.experience.config.ts e2e/ui-layout-consistency.spec.ts
```

## Evidence and limits

[Visual gallery](assets/ui-layout-polish-2026-11-01/visual-review.md), [source/image manifest](assets/ui-layout-polish-2026-11-01/manifest.json), [route visits](assets/ui-layout-polish-2026-11-01/route-visits.json), [nested route visits](assets/ui-layout-polish-2026-11-01/nested-route-visits.json), [settings visits](assets/ui-layout-polish-2026-11-01/settings-visits.json). Portable contact sheets and selected originals are retained beside this report. Full originals remain under ignored `local/ui-layout-polish-2026-11-01/{before,after}`. Source epochs preserve earlier captures rather than assigning final source to historical images; final toolbar/bounded-picker captures supersede the earlier icon/modal defects.

Finish review: **Ready for this local Synthetic layout-polish scope**. The review preserved the navy identity and Ant Design behavior, checked shared consumers and fixed the observed layout defects. This does not certify every possible record/permission combination, real provider/native remote frames, physical audio quality, assistive devices or all browser zoom settings. Hosted CI and the full repository suite were not requested or run. No new milestone is accepted by this report.
