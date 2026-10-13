# Admin information architecture reorganization verification

Date: 2026-10-13. Scope: the approved local Admin UX reorganization proposal, on `main`.

## Baseline and candidate

The proposal inspected `a7a44ebafb52d285696c9e9cb4081f0082ab57e4`. Work began on fetched `main` at `8fc22203dd1666482be38e5b7c9b3f3655771afb`; the intervening commit changed only TODO.md. The implementation preserves the existing backend, APIs, persistence, provider boundaries and security authority. All runtime data used here belongs to disposable Synthetic fixtures.

Final runtime/test source: `05f1b18f02d6223bf015c921b24ca23fb215abf4`. The following evidence commit changes only this report. The implementation and all source corrections are committed on the existing local `main`; the review branch is `admin-ux-reorganization-2026-10-13`, attached as [draft PR #12](https://github.com/trannamtrung1st/agent-core/pull/12). No release milestone or merge is claimed.

## Result and module ownership

`AdminShell` owns the shared header, breadcrumbs, Search Admin, contextual sidebar and responsive navigation Drawer. `adminNavigation.ts` is the typed source for canonical pages, groups, heading copy, routes and approved search labels. `appRoute.ts` parses canonical/legacy/malformed routes and retains only approved redirect queries. `AppRouter` preserves Chat suspension and guards native history departure.

`AdminApp` composes inventories and exact-owner workspaces. `AdminGlobalPages` owns inventory/create dialogs; `adminPresentation` and `AdminRecordSummaries` own shared inventory formatting and record summaries. `InstanceDetail` retains private editors; `InstanceProfile`, `InstanceOverview` and `EffectiveConfigView` separate editing from saved inspection. `useInstanceSettingsController` owns revisioned sparse patches and independent section drafts; `InstanceSettingsSection` selects presentation groups. `DefinitionDetail` owns the revisioned lifecycle; `DefinitionDraftWorkspace` composes one shared candidate, resources, Skills and evidence. `RetainedAdminPage`, draft-status reporting, anchor focus and unsaved guards provide workspace behavior without a second UI kit.

## Capability and route preservation

`I` below means `/admin/instances/{instanceId}`. `D` means `/admin/definitions/{definitionId}/drafts/{draftId}`.

| Existing editor or workflow | Canonical owner | Preservation evidence |
| --- | --- | --- |
| Default Admin inventory | `/admin/instances` | Bare `/admin` replacement; canonical/global history checks |
| Definitions / immutable inspection | `/admin/definitions`, `/admin/definitions/{id}/versions` | Original read-only drawer, copyable instructions, local inspector tabs, deprecation/fork |
| Credentials / email / OAuth | `/admin/connections/credentials` | Shared inventory independent from Instance bindings; protected input and one-time value handling retained |
| Built-in / Webhook Events | `/admin/connections/events` | Catalog, subscribers, credential issuance, source navigation and Automation editor retained |
| Host Browser privacy | `/admin/platform/browser-privacy` | One global editor; protected/unmasked/disabled, CAS recovery, deployment ceiling and restart copy |
| Persona, lifecycle and Definition adoption | `I/profile` | Persona Form/JSON, archive/unarchive, apply/version, independent retained draft |
| Instructions / conversation / behavior / initiative / voice | `I/behavior` | Sparse section Customize/Save/Discard/Reset; one save preserves another draft |
| Interactive models, providers, execution budgets | `I/models` | One budget editor; model reasoning order, provenance, concurrency and next-Run behavior |
| Skills / resources / Workspace | `I/skills`, `I/resources`, `I/workspace` | Separate inherited/local authority; existing drawers, import, toggles, download, revisioned deletion |
| Capabilities / credential bindings / Browser state | `I/access` | Exact grants and host locks; protected bindings remain distinct from authority |
| Management and reviewed Harness change | `I/self-management` | Off/Assisted/Managed, freeze/unfreeze and reviewed shared promotion |
| Automation registrations / restrictions / unattended defaults | `I/automations` | One collection/editor, separate unattended model, exact `?automation=` source and `?run=` inspection |
| Memory / Experience | `I/continuity/memory`, `I/continuity/experience` | Memory policy, learned records, consolidation/maintenance, checkpoints and source provenance |
| Sessions / independent Runs | `I/activity/sessions`, `I/activity/runs` | Exact Session/Run details and search; continued Chat and original results remain distinct |
| Effective diagnostics | `I/effective` | Saved read-only values; Browser diagnostics link to global privacy |
| Definition candidate | `D/overview`, `profile`, `behavior`, `models`, `skills`, `resources`, `capabilities`, `policies`, `test-publish`, `advanced-json` | Shared candidate and JSON; invalid JSON retained/repairable; explicit save, evaluation, publish and delete |

Legacy migration checklist (unit and browser exercised):

- `/admin` → Instances; `/admin/connections` → Credentials; bare Definition → Versions; bare Instance → Overview.
- `I/identity` and `I/identity/profile` → Profile; `I/identity/settings` → Behavior with relocation links; `I/identity/workspace` → Workspace.
- `I/skills/skills` → Skills; `I/skills/resources` → Resources; `I/continuity` → Memory.
- `I/automation` and `I/automation/automations` → Automations; `I/automation/controls` → Automations `#execution-defaults`; `I/automation/harness` → Self-management.
- `I/activity` → Sessions; `I/runs` → Runs; `I/credentials` → Access `#credentials`.
- Exact draft page URLs use the Definition and draft identities together. Unknown/malformed Admin routes remain in Admin with recovery.
- Redirects replace history and retain `session`, `run`, `automation`, `emailAccount`, `event`, `builtin`, `returnTo`, `q` and relevant fragments; unrelated credential-bearing query fields are omitted.
- Archived Automations recover at Profile with an explanation. Native modified/middle-click keeps the source tab and its unsaved draft unchanged.

## Runtime actions and observations

Playwright MCP ran against disposable SQLite Synthetic API on port 5280 and Vite on 5373. It exercised two independent settings drafts, navigation away/back, Conversation-only Save, retained Instructions, owner-departure Stay, draft navigation/save/validation, no-result Search, Escape/opener restoration two sequential setting anchors on the same Models page, keyboard-only mobile destination focus, cancellation restoring Search focus, a fragment-only exact Activity return, the 992/1000/1023/1024 navigation boundary, and a retained pending resource path whose publish blocker returned to Resources. Clearing that pending input re-enabled validation. Expected retained values, separate saved state and exact anchor focus were observed. No provider credentials or live OAuth/mail delivery were used.

Repeatable browser coverage includes every Instance/draft route at 1440px and 390px with reload, Back/Forward; invalid JSON retained through Skills and repaired before Save; native owner-departure Back with Stay/Discard; immutable version inspection and required Synthetic evaluation before publication; explicit dirty-draft deletion without a second discard prompt, and recovery when reopening the deleted URL. Existing workflow tests retain their original API/persisted-state assertions.

## Rendered Impeccable pass

Context was loaded once. One initial batched rendered inspection identified mouse/reload focus outlining and undersized narrow header/navigation controls. Fixes belong to shared shell/token owners: keyboard-only heading focus, 40px narrow navigation/header/close targets, and Search dismissal/destination focus. Drawer screenshots wait for settled bounds. One final confirmation batch was inspected; no additional discretionary polish round was performed. Required functional checks subsequently corrected cancelled-search focus and the exact 1024px navigation boundary. The final assets were refreshed from the final source after these corrections, without another design iteration.

| Viewport width × 900 | Main width | Document horizontal overflow | Navigation evidence |
| --- | ---: | ---: | --- |
| 1440 | 1192 | 0px | 248px permanent contextual rail |
| 1024 | 776 | 0px | 248px permanent contextual rail |
| 768 | 768 | 0px | Drawer trap and Escape/opener return |
| 767 | 767 | 0px | Drawer trap and Escape/opener return; 40px actions |
| 390 | 390 | 0px | Drawer trap and Escape/opener return; wrapped names |
| 320 | 320 | 0px | Drawer trap and Escape/opener return; wrapped IDs/actions |

Final captures live in [assets/admin-reorganization/final](assets/admin-reorganization/final/behavior-1440.png):

- Behavior: [1440](assets/admin-reorganization/final/behavior-1440.png), [1024](assets/admin-reorganization/final/behavior-1024.png), [768](assets/admin-reorganization/final/behavior-768.png), [767](assets/admin-reorganization/final/behavior-767.png), [390](assets/admin-reorganization/final/behavior-390.png), [320](assets/admin-reorganization/final/behavior-320.png).
- Open navigation: [768](assets/admin-reorganization/final/navigation-768.png), [767](assets/admin-reorganization/final/navigation-767.png), [390](assets/admin-reorganization/final/navigation-390.png), [320](assets/admin-reorganization/final/navigation-320.png).
- Long-name/resource stress (21 uploaded local resources plus two inherited): [1440](assets/admin-reorganization/final/resources-1440.png), [390](assets/admin-reorganization/final/resources-390.png).
- Global privacy: [1440](assets/admin-reorganization/final/privacy-1440.png), [390](assets/admin-reorganization/final/privacy-390.png).
- Failure states: [invalid JSON at 320](assets/admin-reorganization/final/draft-invalid-json-320.png), [malformed Admin ID at 320](assets/admin-reorganization/final/not-found-320.png).

These captures show this implementation, not historical golden images. Initial captures remain separately labeled under `assets/admin-reorganization/initial`. Table overflow is contained locally. Active links use `aria-current`, dirty links retain a stable accessible label with unsaved description, modal/drawer focus stays inside while open, and complete long IDs/names wrap or remain available through existing titles/details. Automated focus tests do not establish screen-reader narration quality or human headset/audible quality.

## Checks and acceptance boundary

The local checks below record both passing evidence and intermediate failures explicitly. The final hosted whole-suite gate is exposed by the [PR checks](https://github.com/trannamtrung1st/agent-core/pull/12/checks); an earlier working-tree run is not substituted for exact-head CI.

| Check | Actual command (from `web/`, except docs checks) | Result |
| --- | --- | --- |
| Production build | `pnpm run build` | Pass: TypeScript and Vite. Final bundle 2,456.86 kB / 754.38 kB gzip; existing >500 kB chunk warning remains. |
| Full unit, controlled workers | Node 22: `node node_modules/vitest/vitest.mjs run --maxWorkers=2` | 116 files / 956 tests passed before the last two return-target cases. |
| Final complete unit, CI worker policy | Node 22: `node node_modules/vitest/vitest.mjs run --maxWorkers=1` | 115 files passed; 957 tests passed / one async inventory-navigation fixture failed. Corrected by awaiting the navigation/catalog update through async `act`, preserving the enabled-button assertion. |
| Complete affected Admin suite after correction | Node 22: `node node_modules/vitest/vitest.mjs run src/features/admin/AdminApp.test.tsx --maxWorkers=1` | 42/42 passed. |
| Final route/owner regressions | Node 22: `node node_modules/vitest/vitest.mjs run src/app/AppRouter.test.tsx src/app/appRoute.test.ts src/features/admin/AdminInstanceNavigation.test.tsx --maxWorkers=1` | 25/25 passed, including query/fragment returns and external-target rejection. |
| Publication gate and route follow-up | Same Node 22 command plus `src/features/admin/definitionDraftPublishGate.test.ts` | 32/32 passed. |
| Complete Synthetic core invocation | `pnpm exec playwright test --project synthetic` | 200 passed / two seeded resource fixtures used the bare Definition URL and failed. Both corrected to exact draft URLs; no workflow/assertion was removed. |
| Resource correction recheck | `pnpm exec playwright test e2e/resource-management.spec.ts --project synthetic` | 4/4 passed, including repeated failed refresh, stale CAS and partial deletion recovery. |
| Preserved integration/recovery batch | `pnpm exec playwright test e2e/admin-tab-navigation.spec.ts e2e/definition-trigger-permissions.spec.ts e2e/definition-configuration-layout.spec.ts e2e/diagnostic-details.spec.ts e2e/shared-events.spec.ts e2e/system-credentials.spec.ts e2e/email-accounts.spec.ts e2e/email-harness.spec.ts e2e/multi-trigger-automation.spec.ts --project synthetic` | 30/30 passed. |
| Final canonical/native/boundary checks | `pnpm exec playwright test e2e/admin-tab-navigation.spec.ts e2e/navigation-links.spec.ts --project synthetic` | 7/7 passed at the final source, including 1024/1023/1000/992 transitions and Stay/search focus. |
| Browser speech | `pnpm exec playwright test --project browser-stt --project browser-browser` | 6/6 passed with Chromium fake devices. |
| Global Browser privacy | `pnpm exec playwright test e2e/browser-privacy.spec.ts --project admin-privacy --config .admin-privacy.local.config.ts` | 5/5 passed; temporary two-host config removed after execution. |
| Separate acceptance projects | `pnpm exec playwright test --project PROJECT` for each project below | All six projects passed on separate databases. |
| Docs and design | Relative-link/anchor/fence checker; PyYAML frontmatter parse; JSON schemaVersion 2 shape/component/preview checks; `git diff BASE --check` | 10 Markdown files / 284 relative references valid; Admin brief targets exist; JSON/parity and whitespace checks pass. |

Acceptance totals: `admin-lifecycle` 1, `p76-admin` 1, `p97-harness` 6, `p9899-continuity` 2, `p910-continuity-maintenance` 1, `secretary-demo` 4. The lifecycle/Secretary/resource fixtures were migrated to the actual addressable editor owners. P7.6 explicitly verifies that a pending oversized import blocks testing, follows the blocker back to Resources, then removes it before evaluation/publication. The Secretary picker now targets its own combobox portal rather than a retained hidden Select dropdown.

All CLI hosts used `CI=1` with disposable `PLAYWRIGHT_SQLITE_PATH` files under `/private/tmp`, isolated API/Vite/fixture ports, and `PLAYWRIGHT_FAITHFUL_MANUAL=1` for the main-host-only projects. Browser speech used its separate configured hosts. OAuth test callbacks explicitly use the current test web origin; inherited operator configuration no longer redirects isolated tests to port 5173. Browser privacy used `Browser__AllowUnmaskedCaptures=true` with `https://example.test` as its only synthetic unmasked/trusted origin. No operator database was reset. Only test hosts started for this work are stopped during cleanup.

Logs remain under `/private/tmp/admin-ia-*`: `build-final-contract`, `vitest-final`, `vitest-delivery`, `adminapp-delivery`, `final-contract-unit`, `core-final-02`, `resource-recheck-01`, `regression-09`, `boundary-final`, `audio`, `privacy`, and acceptance project logs. Initial diagnostic runs and setup/transport failures are not green gates. Node 26's default jsdom storage environment failed setup; Node 22 matches CI. An earlier over-parallelized run and an interrupted diagnostic core run are superseded by the explicit results above. Existing AntD List/Alert migration and jsdom layout/act warnings remain distinct from assertion results.

Documentation is synchronized in docs/13 (behavior), DESIGN.md (presentation), schemaVersion 2 design.json, the Admin surface brief, Admin README, docs/11, docs/16, docs/18 and the current demo instructions. Historical reports remain unchanged. Relative links/anchors, balanced Markdown fences, YAML frontmatter, JSON structure/preview parity and `git diff --check` are checked at delivery.

Hosted `synthetic.yml` acceptance is distinct from local build/unit/browser results. It tests the PR head, including backend, both full frontend shards, both Synthetic/browser speech core shards, separately isolated acceptance projects and Compose durability smoke. It was still running when this evidence was saved; the live PR checks and final handoff record its outcome. Earlier candidate workflows are superseded when source corrections are pushed. No new release milestone, P10/P11 implementation or hosted freeze is claimed. Backend source is unchanged; live-provider, real OAuth delivery and manual microphone/speaker checks are outside this navigation change.
