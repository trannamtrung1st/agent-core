# Focused Admin configuration review — 2026-10-13

Review baseline: `c3503db1`, following implementation `9a798360` and its design-documentation synchronization. This is a bounded frontend follow-up; it does not reopen milestone freezes or establish hosted/provider/device acceptance. The user explicitly requested commit/push and retained the earlier no-CI-wait instruction.

## Findings and corrections

- Execution budgets still described unattended defaults as owned by Automations. Its copy now names the focused Unattended model page; the existing exact destination remains intact.
- An archived Automations fallback labeled Open Lifecycle linked to Profile. Its destination now matches Lifecycle.
- A recovered Lifecycle URL retained `notice=archived-automations` after unarchive and continued showing an archived-only warning beside Active status. The warning now requires authoritative archived state. A new browser regression archives an Instance, follows Unattended model into recovery, unarchives, asserts the warning disappears and verifies unattended access resumes.
- The static navigation preview labeled configuration saved while enabling Save/Discard. Those controls now reflect the saved-state disabled treatment, Refresh remains available, and the illustrated sticky frame uses the live stacking value. No theme or component-system replacement was introduced.

The owning [frontend specification](../13-frontend-implementation-spec.md) records notice recovery. The [design guide](../../.agents/context/DESIGN.md) and [preview sidecar](../../.impeccable/design.json) retain the established Ant Design baseline; earlier [full walkthrough and captures](admin-focused-configuration-verification.md) remain separate evidence.

## Runtime observations

Playwright MCP used native Synthetic API 5280/Vite 5373 with disposable SQLite `/private/tmp/admin-review-20261013.db` and separate temporary persistence roots. No operator data was reset and no hosted provider was called.

1. Created a General Assistant v24 Instance, edited persona and inherited Instructions on separate pages, then used one global Save. The success state appeared and a real server read confirmed the sparse Instructions override.
2. Injected one Conversation write 503. Language remained staged; Refresh retained it, and explicit Save recovered. The recorded browser error is that intentional 503, with no additional unexpected error observed in the exercised flow.
3. At 320px, followed Execution budgets → unattended defaults and reached Unattended model with zero page overflow. Its focused anchor received focus with top 393.45px, below the action panel bottom 377.45px.
4. Archived the Instance and opened Unattended model. It redirected to Lifecycle with the restriction notice and Save disabled. Unarchive restored Active status. Before the fix the notice remained; afterward it disappeared, including when the old query stayed in the URL.

Exploratory selector mistakes were corrected without altering product behavior: Lifecycle has both a task heading and panel heading, so the task query uses level 2; Unarchive is an existing direct action, not an Archive-style confirmation dialog. These tool timeouts are not counted as successful scenarios.

Console/network evidence: `.playwright-mcp/admin-review-console.log` and `.playwright-mcp/admin-review-network.log`. Owned temporary hosts are stopped during delivery; other processes/data are preserved.

## Local verification

- **87/87 unit tests in seven files** passed: `InstanceConfigurationActions`, `InstanceSettingsSection`, `AdminInstanceNavigation`, `AdminApp`, `CredentialsSection`, `DefinitionVersionsTable` and `appRoute`, using Vitest with `--maxWorkers=1`. Log: `/private/tmp/admin-review-unit.log`.
- After the final lifecycle correction, the coordinator/navigation subset passed **6/6** again. Log: `/private/tmp/admin-review-final-unit.log`.
- The five existing Synthetic browser suites passed **17/17**: configuration actions, canonical Admin navigation, polish/recovery, native links and scoped configuration. Log: `/private/tmp/admin-review-browser.log`.
- The updated configuration suite passed **3/3**, including the new archive/unarchive regression and repeated save/partial-failure recovery. Log: `/private/tmp/admin-review-lifecycle-browser.log`. Across the runs, **18 distinct browser cases** have passing evidence; repeated cases are not additional distinct coverage.
- Final TypeScript and Vite production build passed; the existing bundle-size warning remains. Logs: `/private/tmp/admin-review-final-ts.log` and `/private/tmp/admin-review-final-build.log`.
- Relative Markdown links/anchors, balanced fences, preview structure and whitespace passed. Preview saved-state flags were compared with the live toolbar. Structural checks do not claim formal Stitch schema validation.

Browser suites used free isolated API/Vite/fixture ports 5580/5673/5591, `CI=1`, `PLAYWRIGHT_FAITHFUL_MANUAL=1` and separate disposable persistence. Hosted CI was not awaited. Backend suites, the full unrelated frontend/browser matrix, paid providers, physical audio and manual assistive-technology acceptance were not run for these frontend navigation/state corrections.
