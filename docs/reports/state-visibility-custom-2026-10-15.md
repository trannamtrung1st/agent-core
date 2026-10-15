# Visibility defaults and Custom selection — 2026-10-15

Scope: separate the Definition visibility default from its permitted maximum, use aggregate Instance summary for Persistent, and make Custom selectable in both editors. This is focused local verification, not milestone acceptance or CI.

## Result

Definitions have an optional `maxAgentStateVisibility`; an omitted value keeps the legacy ceiling equal to the default. Existing immutable publications/pins are not rewritten. Persistent Definition authoring sets default and maximum to InstanceSummary; the Instance recipe requests Summary within the adopted Definition maximum. No inspection capability is granted by a recipe or maximum. Custom keeps current values as a local editor choice, requires Save only for actual data changes, and derives from saved settings after reload.

## Checks

| Local command | Result |
| --- | --- |
| `dotnet test tests/AgentCore.Application.Tests --no-restore -m:1 -p:UseSharedCompilation=false --filter 'FullyQualifiedName~StateVisibilityCeilingTests\|FullyQualifiedName~StateIsolationPolicyTests' --logger 'console;verbosity=minimal'` | 22 passed |
| `dotnet test tests/AgentCore.Api.Tests --no-restore -m:1 -p:UseSharedCompilation=false --filter FullyQualifiedName~StateIsolationConfigurationTests --logger 'console;verbosity=minimal'` | 9 passed; InMemory/SQLite override and clear, maximum-edit rejection, atomic preset handling |
| `dotnet test tests/AgentCore.Api.Tests --no-restore -m:1 -p:UseSharedCompilation=false --filter FullyQualifiedName~StateIsolationContextTests --logger 'console;verbosity=minimal'` | 10 passed; scope/forged IDs, counts, throttling, live revocation and context boundaries |
| `dotnet test tests/AgentCore.Infrastructure.Tests --no-restore -m:1 -p:UseSharedCompilation=false --filter 'FullyQualifiedName~Reopen_preserves_published_definition\|FullyQualifiedName~Sealed_run_configuration_hashes_and_inert_receipts_survive_conversion_and_reopen' --logger 'console;verbosity=minimal'` | 3 passed |
| `pnpm exec vitest run src/features/admin/DefinitionStateIsolationPolicy.test.tsx src/features/admin/StateIsolationConfigurationSection.test.tsx src/features/admin/IsolationPresetControl.test.tsx` from `web/` | 46 passed; then the Definition file passed 4 tests after adding legacy-ceiling preservation (47 distinct tests total) |
| `pnpm run build` from `web/` | Passed; existing chunk-size and SignalR annotation warnings |
| `git diff --check` | Passed |

## Synthetic browser journey

Playwright MCP used an isolated InMemory Synthetic API on 5088 and Vite on 5188. `/health` confirmed Synthetic; no provider credentials/calls were used. The existing user host was left running.

1. Created an Approval Demo Instance on built-in v1. Its legacy visibility selector offered only Off, as expected.
2. Forked Definition v1; chose Persistent and observed default and maximum Summary. Saved, selected Custom and confirmed Save stayed disabled. Changed the default to Off while retaining maximum Summary, saved and validated/published v2.
3. Inspected v2: all preset/policy controls were disabled, with default Off and maximum Summary intact.
4. Explicitly adopted v2 in the existing Instance. Its visibility choices became Off, Current Session and Summary. Saved Summary and reloaded: Summary remained an Instance override.
5. Selected Custom from the matching Persistent recipe without dirtying configuration. Edited visibility to Current Session, attempted Stateless, then chose Keep editing: the edit survived. Discard restored saved Summary.
6. Used Definition defaults, saved and reloaded: inherited Off returned.
7. In a new Definition draft, reduced maximum to Current Session: Summary disappeared from default choices. After choosing default Current Session, maximum Off was disabled.
8. Both editor pages had document width equal to viewport width at 1440, 768, 767, 390 and 320px. Inspected the Definition screenshot at `.playwright-mcp/state-visibility-definition.png` (local ignored evidence).

Expected outcomes matched observed outcomes. Related API requests completed successfully. The browser console emitted the existing Ant Design `List` deprecation warning on Test & Publish; no flow-related error or failed API request was observed. Initial locator/animation waits were corrected during exploration; those were automation issues, not product failures.

CI and live-provider checks were not requested or run. Existing published Definitions need a new draft/publication and explicit Instance adoption to raise their maximum. Running user applications need the updated backend build; they were not restarted by this task.
