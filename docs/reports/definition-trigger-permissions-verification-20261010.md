# Definition Automation trigger permissions verification

Date: 2026-10-10. Baseline: `24aa7bb7` on `main`. The implementation candidate is the commit containing this report; final commit and exact-HEAD hosted status are reported separately after push.

## Implementation and compatibility

`AutomationTriggerPermissions.tsx` replaces the Definition Form's three-source multiselect with Schedule and Events, nesting Built-in Events and Webhook Events. Every checked state derives from the canonical candidate. The mixed Events parent has accessible `aria-checked=mixed`; Space toggles the group and Tab reaches its children. Explicit enable from empty allows both families; disable removes both without affecting Schedule. A local paused-choice reference restores a restricted selection after parent off/on. Explicit child edits replace that undo choice. No parallel saved permission state exists.

Persisted JSON and backend admission still use only `schedule`, `coreEvent` and `applicationEvent`. Loading and unrelated edits do not normalize or expand valid saved permissions. Form/JSON views share the same unsaved candidate and existing revision contracts. Unknown `events` and duplicate values remain rejected. No migration, authorization engine, runtime admission change, live-provider request or enterprise UAT attempt was introduced.

Read-only published details reuse the editor. Effective configuration and Instance policy summaries name each source family's Allowed/Not allowed status. Disabled overall policy retains configured choices with explicit inactive-authority guidance. `AgentDefinitionDraftDiffService` names the section Automation trigger permissions and puts exact source grants at the front of its existing 240-character summary limit. Previously, trailing source details could be clipped. Before/after regression tests prove that Built-in-only and Webhook-only baselines expose an expansion to the other family.

The related Automation editor drawer now links its dialog to a unique title ID using the established `SkillDrawer` pattern. Its existing disabled-save recovery regression waits for that named editor and scopes button assertions to it; the authorization and mutation assertions are unchanged.

## Executed checks

| Check | Command / tool | Result |
| --- | --- | --- |
| Domain suite | `dotnet test tests/AgentCore.Domain.Tests --no-restore --nologo` | 191 passed |
| Application suite, final implementation | `dotnet test tests/AgentCore.Application.Tests --no-restore --nologo` | 1,643 passed; 7 opt-in live/paid tests skipped |
| Exact source authority and diff tests | Application filter `AdminDefinitionDraftDiffServiceTests|TriggerDurablePolicyTests` | 18 passed |
| Affected HTTP journeys | API filter `MultiTriggerAutomationJourneyTests|CoreEventAutomationJourneyTests|UnifiedAutomationJourneyTests|DefinitionDraft` | 15 passed |
| Disabled policy and Event stabilization | API filter `CoreEventStabilizationTests` | 12 passed |
| Frontend suite | Explicit Node 22.18.0: `node node_modules/vitest/vitest.mjs run --maxWorkers=1` | 866 passed across all 108 files |
| Affected Automation editor | Explicit Node 22.18.0, `InstanceAutomationsSection.test.tsx --maxWorkers=1` | 26 passed |
| TypeScript and production build | Explicit Node 22.18.0, `tsc -b` and `vite build` | Passed; existing large-chunk advisory |
| Synthetic browser gates | `pnpm exec playwright test e2e/definition-trigger-permissions.spec.ts e2e/automation-editor-recovery.spec.ts --project synthetic` | 6 passed |
| Documentation | Changed Markdown link/fence check, design JSON parse, `git diff --check` | Passed |

Initial frontend runs selected Node 26.7.0 through the web-directory shell/package-manager PATH, although CI configures Node 22. Those runs encountered native `localStorage` incompatibility and intermittent Admin timing/visibility failures. Isolated clipboard checks and single-worker Admin checks passed without source changes. Final verification explicitly pins `/Users/trungtran/.nvm/versions/node/v22.18.0/bin/node`; storage service regressions then passed. No test was removed, deadline increased or assertion weakened. Early browser test failures were selector mistakes (the existing Draft rev control and a diff heading that includes Modified); those selectors were corrected before the passing final browser run.

The first full Node 22 run with two workers passed 865 tests but timed out in one existing Automation disabled-save recovery case. That case passed in isolation. The final named/scoped editor regression then passed with all 26 tests in its file, and the complete final frontend suite passed all 866 tests with one worker. An intermediate named-dialog check exposed Ant Design's repeated test title ID; the explicit unique title link fixes that accessibility ambiguity rather than bypassing it.

## Observed browser behavior

Verification used an owned disposable Synthetic API at `127.0.0.1:5285` and matching Vite app at `127.0.0.1:5185`, with InMemory persistence and no provider calls. Existing Real hosts were left alone.

Playwright MCP selected Built-in-only permissions, edited an unrelated Definition name, switched to JSON and observed exactly `["coreEvent"]`. Keyboard parent off/on preserved that restriction, and the actual HTTP save returned 200 with the same canonical array. A fresh navigation separately selected Webhook-only, saved exactly `["applicationEvent"]` and showed no console errors. Desktop, tablet and mobile screenshots were inspected in the shipped dark Ant Design theme. All three widths had visible, nested, wrapping controls and no horizontal overflow; the product currently ships this dark theme. Backend restarts discarded only disposable fixture state; stale pre-restart polling was excluded from fresh-navigation observations.

Repeatable browser tests independently exercised both restricted families, unrelated editing, JSON adding Schedule, mixed-state rendering, Space off/on, Tab focus, HTTP save, reload and exact saved-draft reopening at 1440/768/390. A third flow ran the existing Synthetic validation/evaluation publish gate, verified the exact Built-in Allowed/Webhook Not allowed diff, published, and inspected immutable disabled controls with the correct mixed state and inactive-policy explanation. Browser errors and failed requests were checked for that lifecycle flow. Existing recovery scenarios verified Schedule saving under catalog failure, a healthy Built-in subscription enabled beside a disabled unavailable Webhook sibling, and disabled Event saving while activation remains fenced.

## Documentation and limits

Canonical frontend, testing and implementation-plan sections and README were updated. DESIGN/PRODUCT context, the Admin surface brief and design sidecar narrative retain existing tokens and visual identity while describing the new hierarchy. Architecture, wire schemas, event ingress, persistence and admission contracts are unchanged; their existing canonical identifiers remain appropriate. Historical verification reports were preserved.

Hosted CI is checked for the pushed implementation commit, without waiting for completion. Local results do not establish hosted acceptance. Live/paid provider tests stay opt-in, and this change does not establish real AHI UAT behavior.
