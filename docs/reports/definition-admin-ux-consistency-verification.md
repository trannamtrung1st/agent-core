# Definition admin UX consistency

Date: October 10, 2026. Bounded frontend refinement of the existing Ant Design v6 Admin experience; no milestone or hosted acceptance claim.

Definition drafts and immutable version inspection now follow Instance configuration grouping: Identity & version → Profile / Settings, and Skills & resources → Skills / Resources. Definition-only Capabilities and Test & Publish retain their separate responsibilities. Profile contains identity/goals; expandable Settings separate instructions, Conversation, Behavior, Initiative, Voice, Model defaults, Memory policy, Trigger restrictions, Provider preferences and metadata. Execution budgets remain the existing shared form.

`AgentIdentitySections` and `AgentSkillsResourcesSections` in `AgentConfigurationLayout.tsx` own the repeated product navigation. Definition revisions/candidate/publication and Instance inheritance/override logic remain in their existing owners. Shared settings grid/form rules in `app.css` own bounded widths and field spacing; `admin-content` owns the outer inset. The extra Definition lifecycle/editor panel inset was removed. Mounted editor sections retain unsaved edits, expanded settings and pending resource input through navigation and Form/JSON changes.

## Runtime evidence

Used Playwright MCP against a disposable key-free Synthetic .NET/Vite app on 5098/5198. Existing user hosts and data were preserved. The initial manual host used InMemory; the repeatable browser run used a fresh SQLite database at `/private/tmp/definition-ux/sqlite/verification.db` for the published Skill pin assertions.

- Edited Definition instructions, visited Skills/Resources, returned to Settings and saved; edits and navigation were retained.
- Entered invalid JSON, confirmed Skill edits were disabled with inline recovery, returned to the retained invalid text, corrected it and saved without losing the selected Settings section.
- Inspected built-in v1 through the version drawer; instructions remained read-only and selectable. Escape returned focus to the exact version link and retained the table search.
- Compared draft, Instance and immutable-version consumers at 1440/768/767/390px. Draft and Instance forms had matching left edges, widths and 16px gaps: 768px, 702px, 709px and 332px respectively. Fields used two columns at 768px and above and one below; all pages had no horizontal overflow. The version drawer used up to 832px and fit the viewport at narrow widths.
- Reviewed browser console and API requests for these flows: no current-flow console errors or failed requests. Initial readiness navigation before the host started was retried after startup.

Screenshots and logs: `local/verification/definition-ux/` (local, ignored evidence). The settled `*-confirmed-{1440,768,767,390}.png` captures are the final rendered comparison.

## Repeatable checks

- Frontend production build: `cd web && pnpm run build` — passed. Existing Rollup annotation/chunk-size warnings remain non-blocking.
- Component regressions: `pnpm run test --run --maxWorkers=2` with `AdminApp.test.tsx`, `DefinitionVersionsTable.test.tsx`, `AdminInstanceNavigation.test.tsx`, `InstanceSettingsSection.test.tsx` — those four files passed, covering 54 tests. The final isolated `definitionCandidateEditor.test.tsx` run passed all 14 tests. Across the final file results, 68 component tests passed.
- Browser regressions: `PLAYWRIGHT_SQLITE_PATH=/private/tmp/definition-ux/sqlite/verification.db pnpm exec playwright test --config .definition-ux.verify.config.ts` using a temporary configuration for the isolated host, with Definition layout, collections, tab navigation, P7.6, P8 Skills, resource management and Definition lifecycle/resource journeys — 21 passed.
- Shared consumers: the same isolated-host command with `admin-polish.spec.ts`, `instance-skills.spec.ts` and `p8.5-messaging-skill-journey.spec.ts` — 9 passed, including Definition upgrade/rollback, independent Instance Skills and the P8.5 persisted Skill pins. Across both browser batches, 30 scenarios passed.
- Resource input retention extension: the Definition layout scenario reran successfully after adding assertions for pending logical-path input and selected Resources tab across group changes.
- Impeccable layout detector on the shared layout, candidate editor, version table and CSS returned no deterministic findings. Tooling also reported an existing DESIGN.md/design.json freshness warning; no context repair was performed as part of this refinement.
- Local Markdown link targets/fences and `git diff --check` — passed.

The temporary host, Vite process, browser page and isolated Playwright configuration were removed after verification. Local evidence and the disposable SQLite fixture remain under the paths above.

Backend/full Synthetic/voice/Compose/hosted gates were not rerun for this frontend layout change. Existing backend/resource changes already present in the checkout are outside this report. Their milestone acceptance remains owned by their existing verification reports.

## Follow-up consistency and behavior review

The second review reproduced a Definition switch label that did not activate its control, a stretched grid switch row that did not align with its adjacent input, and missing recovery context when invalid Trigger restrictions were collapsed. Save guidance also misidentified a failed capability catalog as loading authoring options and retained the old Definition-tab location for execution limits.

Corrections associate Definition switch labels with stable control IDs, retain keyboard activation and disabled/read-only behavior, and align grid switch rows at the bottom like Instance controls. Trigger restrictions display Needs attention while invalid, including when collapsed; the sticky save explanation names Identity & version → Settings. Catalog failure points to the existing Capabilities → Retry tool registry action, and execution-budget guidance points to its current Settings location. Validation, revision checks and publication rules remain in their existing owners.

Playwright MCP on isolated SQLite Synthetic 5098/5198 reproduced the label problem before the correction, then verified label and Space activation, marker recovery and successful Definition save. At 1440/768px the switch row and adjacent field had equal bottom edges; at 767/390px fields stacked without page overflow. Instance label activation followed by Save and reload retained the changed value. Instance form widths remained 768/702/709/332px at 1440/768/767/390px, with 16px gaps. Immutable inspection kept disabled switches and selectable read-only text, and Escape returned focus to the version link. The reviewed MCP console had zero errors; Instance Settings GET/PATCH/reload requests returned 200.

Follow-up checks:

- `pnpm run test --run --maxWorkers=2` with the five component files listed above, including `definitionCandidateEditor.test.tsx` — 68 passed in one run.
- `PLAYWRIGHT_SQLITE_PATH=/private/tmp/definition-ux-review/verification.db pnpm exec playwright test --config .definition-ux-review.config.ts e2e/definition-configuration-layout.spec.ts` — 2 passed, covering labels/keyboard/alignment, collapsed validation, retained JSON/resource input and catalog retry.
- The same isolated-host command with `admin-collections.spec.ts`, `admin-tab-navigation.spec.ts`, `scoped-instance-configuration.spec.ts` and `z-admin-definition-lifecycle.spec.ts` — 16 passed. These exercise publication, immutable inspection/focus, delayed navigation, Settings save/reset/conflict/read recovery and resource ownership. Follow-up total: 18 browser scenarios passed.
- `pnpm run build` — passed; existing Rollup warnings remain.
- Impeccable layout detector — no deterministic findings. `git diff --check` and local Markdown links/fences — passed.

Follow-up logs are retained in `local/verification/definition-ux/` as `review-*.log` and `review-detector.json`. The disposable SQLite host, frontend process, browser page and temporary Playwright configuration were cleaned up after the review. This bounded review does not establish full backend, voice, physical-device or hosted acceptance.


## Execution budgets panel follow-up

Execution budgets now shares an Ant Design Collapse composition across Definition drafts, immutable version inspection and Instance Settings. It opens initially, retains edits when collapsed, uses the existing bounded Settings form width, and displays Needs attention in the header for load errors or invalid values. Existing budget ownership, inheritance, limits and save admission remain in the feature components.

Validation on October 10, 2026:

- ExecutionBudgetsSection component suite: 8 tests passed, including collapse/reopen retaining invalid edits, disabled Save, loading recovery, revision conflicts and stale Instance save completion.
- Frontend production build passed; existing chunk-size and Rollup annotation warnings remain. Layout detector returned no findings; git diff whitespace check passed.
- Playwright MCP against a disposable local Synthetic SQLite host: Definition budget edit, collapse/reopen, save and reopen; fractional-value validation and header marker while collapsed; Instance override, profile change, collapse/reopen, save and reload; immutable version disabled controls and keyboard expansion. All passed.
- Definition and Instance checked at 1440, 768, 767 and 390px: document width matched viewport, identical panel/content bounds, content capped at 768px on desktop. Read-only drawer checked at 1440 and 390px. Desktop Instance and mobile read-only screenshots inspected for wrapping, panel insets and control alignment.
- Browser console had no errors; affected limits, draft and Instance budget requests succeeded.

This follow-up does not constitute full milestone acceptance. No backend or provider behavior changed.


## Configuration panel wrapping follow-up

Definition Profile, Skills, Resources, Capabilities, Test & Publish and Advanced JSON now use the existing Instance panel surface through AgentConfigurationPanel. Instance management uses the same composition. Immutable Profile, Skills and Resources reuse these panels; existing bordered Settings and capability details retain their own structure. One shared owner supplies the header, optional description/actions, border/background and body inset. Tabs own navigation; feature components still own edits and commands.

The panel spans the available tab width. Forms inside are bounded to 48rem, and Definition Profile now follows Instance persona field order: name/role, multiline description, then tone. Equivalent two-column gaps use the section token; the Instance management assignment/lifecycle column remains an intentional difference.

Runtime evidence on October 10, 2026:

- Playwright MCP on the disposable Synthetic SQLite host: Definition profile edit, tab retention, save and reopen; multiline description retention; invalid JSON blocking Skill edits and recovery; resource-path draft retention across Capabilities; Instance persona edit, Settings/Profile retention, save and reload; immutable Profile read-only controls, Skills editing actions absent, Resources panel and drawer dismissal; Test & Publish validation with truthful blocking findings and safe diff. Expected outcomes observed.
- Compared Definition and Instance Profile/Skills/Resources plus Definition Capabilities at 1440/768/767/390px; panels have the same 16px body inset, full tab width, bounded forms and no document overflow. Read-only drawer checked at 1440/390px; stable mobile panel width was 358px in a 390px viewport. Desktop Definition and Instance panels and mobile Definition/read-only screenshots were inspected.
- 14 affected collection/navigation/configuration E2E scenarios passed, then publication after the Test & Publish panel change passed separately. The initial CLI browser launch was blocked by macOS process sandbox permissions; the authorized local launch passed.
- Focused components: all 61 distinct tests passed across AdminApp, Definition candidate editor, immutable-version details and publication gate. The initial combined run had 57 passes and one editor timeout; the final editor suite passed all 14, and that timeout scenario also passed in isolation. The final description control uses three rows like Instance persona editing, avoiding unnecessary editable autosizing.
- Final production build passed with the existing Rollup annotation/chunk-size warnings. Final layout/JSON-recovery E2E rerun: 2 passed; publication rerun: 1 passed. The layout detector returned no findings, Markdown fence checks and git diff whitespace checks passed.
- Budget/profile APIs succeeded; browser emitted an existing Ant Design List deprecation warning when validation rendered its result list, with no application exception.

Evidence is in local/verification/panel-wrap. This frontend follow-up does not claim milestone or hosted acceptance.
