# Agent self-management review follow-up

Date: 2026-10-11. Reviewed behavior commit: `c976f3f7041439458c776808dd82d0588464a8c9` on `main`. Checks ran on the corresponding working tree before commit. This report supersedes the affected claims in [initial verification](agent-self-management-verification.md); full proposal acceptance remains open.

## Findings corrected

- Built-in enable/reset on a fresh Instance previously materialized unrelated inherited policy fields as local overrides. Fresh owners now remain sparse; only legacy full-policy records are materialized. InMemory and SQLite tests change a Built-in choice, publish/adopt changed Definition defaults, verify mode/area inheritance, and reset the choice back to Definition defaults.
- Exact agent policy updates previously discarded independent Built-in choices. Mode, areas and automatic allowances now preserve them. Freeze and synthesized Harness state also retain empty overrides for fresh inheriting owners.
- Settings approval preparation previously checked shape without the commit's Definition/model/provider constraints. Both paths now share validation. Invalid capability, provider, model and Definition-denied memory selections fail before confirmation without changing the owner revision.
- Credential binding mutations increment the Instance revision, but Admin previously refreshed only the binding list. It now awaits owner refresh before allowing another mutation. Two consecutive bind/unbind cycles complete without navigation and leave revision 5.
- Admin previously omitted Configuration assistance when a Definition lacked its required capability. The immutable Core catalog is now independently inspectable; runtime resolution remains gated. Admin shows missing authority separately from policy suppression and offers no content authoring controls.
- Policy preview/receipt timing now distinguishes immediate authority removal from grants admitted in the next Run. The design sidecar was refreshed from the existing design document, which was preserved.

## Runtime verification

The original self-management browser scenario attempted to enable `userRetrieval`, which General Assistant v23 forbids. Its approval assertions and an earlier matching saved response did not establish a committed memory change. That success claim is withdrawn. The forbidden request now has a preapproval regression. The corrected browser scenario disables permitted session memory, rejects the first exact request and confirms persisted `true`, then approves a fresh request and confirms persisted `false`. It also verifies brief-response readback in a fresh Run in the same Session, Freeze, ordinary Hello while frozen, blocked management inspection, and explicit Admin unfreeze.

Playwright MCP independently exercised the updated disposable Synthetic host after restart: unbind, bind and unbind on one page, successful owner readback at revision 5, and restricted v21 Built-in inspection showing `Missing authority: configuration.inspect` without Customize. Existing credential CRUD, safe metadata, profile reset, archival and three-width browser checks passed. No real provider or external application action was invoked.

| Check | Result |
| --- | --- |
| `dotnet test tests/AgentCore.Domain.Tests --no-restore` | 195 passed |
| `dotnet test tests/AgentCore.Application.Tests --no-restore` | 1,668 passed; 8 opt-in skipped |
| `dotnet test tests/AgentCore.Api.Tests --no-restore` | 481 passed; 3 opt-in skipped |
| `pnpm --dir web exec vitest run src/features/admin/CredentialsSection.test.tsx src/features/admin/InstanceSkillsSection.test.tsx --maxWorkers=1 --testTimeout=60000` | 17 passed |
| AdminApp and SelfManagementSection component suites, earlier review invocation | 46 passed; the failed credential test in that invocation was corrected and rerun above |
| `pnpm --dir web run build` | Passed; existing large-chunk warning remains |
| Synthetic Playwright: self-management, system-credentials, instance-skills | 7 passed |
| `git diff --check` | Passed |

Browser runner used disposable SQLite data and isolated localhost ports:

```sh
env CI=1 PLAYWRIGHT_API_PORT=5098 PLAYWRIGHT_WEB_PORT=5188 PLAYWRIGHT_FIXTURE_PORT=5102 PLAYWRIGHT_SQLITE_PATH=/private/tmp/agent-self-management-review-e2e3/synthetic.db PLAYWRIGHT_FAITHFUL_MANUAL=1 pnpm --dir web exec playwright test --config playwright.self-management.local.config.ts system-credentials.spec.ts self-management.spec.ts instance-skills.spec.ts --project=synthetic
```

The temporary config, removed after verification, imports `standard` from `./playwright.config` and exports `defineConfig({...standard, webServer: standard.webServer.slice(0,2), reporter:'list'})`. Unit test setup failures and the initial browser failures were investigated and corrected; the final results above are the passing reruns. The Infrastructure suite and full frontend suite were not repeated in this follow-up; their initial verification results remain historical evidence.

## Acceptance remaining open

The safe continuation fallback still requires a deliberate new owner turn; automatic fresh-Run continuation is not implemented. Real tool-capable model UAT remains unrun because specific authorization to send its bounded test payload to OpenRouter has not arrived. Expanded captured-request, new-tool recovery and blocker journeys remain open as listed in the initial A01–A20 disposition. These review fixes do not close the entire proposal or reopen P10/P11.
