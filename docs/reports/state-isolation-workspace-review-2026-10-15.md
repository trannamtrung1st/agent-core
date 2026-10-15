# State & Isolation workspace review — 2026-10-15

Reviewed the visibility default/maximum, shared recipes, authoring/publication controls, coordinated preview/apply and cross-page draft lifecycle after `49d7fcc9`. This is focused local verification; historical milestone acceptance is unchanged.

## Findings and corrections

- **Stale detailed-page values:** on a fresh General Assistant Instance, selecting Stateless showed Voice On → Off in the isolation preview, but navigating to Voice still showed On. Memory fields had the same separate-draft ownership. State policy, Memory and Voice enablement now share one Instance workspace controller, draft and atomic Save. Detailed pages show the preset values, edit the same draft, reuse its preview and report their dirty navigation state. Shared reads that fail disable those fields and offer Retry while retaining independent edits.
- **Mixed-save conflicts:** independent settings could advance the Instance revision before the coordinated group saved, leaving an otherwise valid draft stale. Earlier disjoint writes in the same Save now cause an exact-revision re-preview, with unchanged Experience and maintenance revisions required. External changes still stop Save. Dependent settings refresh after the batch, preserving drafts and preventing an intervening read from superseding a save. Memory/Voice section resets stage changes for review and Save rather than issuing overlapping immediate writes.

No backend contract, capability grant, visibility ceiling, publication, runtime mode or database change was needed. Definition presets already edit one candidate across their detailed pages and retain that behavior. Other settings keep their existing sparse owner and CAS commands.

## Local checks

| Check | Observed result |
| --- | --- |
| From `web/`: `pnpm exec vitest run src/features/admin/InstanceIsolationWorkspace.test.tsx src/features/admin/StateIsolationConfigurationSection.test.tsx src/features/admin/InstanceSettingsSection.test.tsx src/features/admin/InstanceConfigurationActions.test.tsx src/features/admin/DefinitionStateIsolationPolicy.test.tsx src/features/admin/IsolationPresetControl.test.tsx` | 62 passed |
| From `web/`: `pnpm exec vitest run src/features/admin/InstanceIsolationWorkspace.test.tsx` after adding unavailable-read recovery | 4 passed; 63 distinct focused frontend tests across both runs |
| From `web/`: `pnpm run build` after the final code changes | Passed; existing SignalR annotation and chunk-size warnings |
| `dotnet test tests/AgentCore.Api.Tests --no-restore -m:1 -p:UseSharedCompilation=false --filter FullyQualifiedName~StateIsolationConfigurationTests --logger 'console;verbosity=minimal'` | 9 passed, including atomic apply, restrictive ceilings, InMemory/SQLite visibility override/clear and owner revision conflicts |
| `git diff --check` | Passed |

The existing isolated Settings tests emit React `act` and jsdom numeric-height warnings; they passed. New workspace regressions exercise shared preset/manual edits, discard, staged reset, one coordinated write, disjoint Voice ID/Memory saves, concurrent maintenance revision refusal followed by refresh/retry, and failed-read recovery without losing a Voice ID draft. Early test synchronization was corrected to wait for enabled controls before interacting.

## Synthetic browser evidence

Playwright MCP used a disposable InMemory Synthetic API on 5088 and Vite on 5188; `/health` confirmed Synthetic before mutation. No provider call or user application restart was performed.

1. Reproduced the stale Voice value before the fix, then selected Stateless again after the fix. Voice showed Off on its own page; Save there persisted it through reload. Enabling Voice while request isolation remained active showed the policy blocker and disabled Save.
2. Edited Session Memory on its detailed page and returned to State & Isolation: the label became Custom and the preview showed the same Memory edit. A common Discard from Voice restored the saved Memory value. Refresh retained the unsaved Memory edit and refreshed its preview.
3. Saved a Memory change together with a custom Voice ID: both persisted through reload without a partial-save error. Native settings retained the latest revision after the coordinated save.
4. Rechecked Definition Persistent authoring, Save, explicit Custom selection without dirtying data, and Stateless propagation to its Voice page. Existing default/maximum controls and candidate ownership remained intact.
5. Checked Instance State & Isolation, Definition State & Isolation, Voice with a blocking preview and Memory with a dirty preview at 1440, 768, 767, 390 and 320px. Document width matched viewport width throughout. Existing Ant Design field grids, panels, preview and spacing owners were reused; no CSS or UI kit was added.

Expected outcomes matched observed outcomes. Final page-console inspection showed no errors. A transient React dependency-array warning during Vite hot reload cleared on a fresh page load; no flow-related failed API request was observed. The concurrent-maintenance and unavailable-read cases were exercised by the focused component fixture; the real API transaction/conflict behavior was separately verified by integration tests.

CI, live-provider calls and deployment were not requested or run. Only the disposable verification hosts were stopped. The user's running application and durable data were preserved.
