# Scoped Instance configuration review

Date: 2026-10-10. Reviewed baseline: `679a8cf243a1b8483fb95da2471785f69b66b5ca` on `develop/branch-1`. The user authorized review, corrections, commit and push. The [original acceptance report](scoped-instance-configuration-verification.md) remains evidence for its recorded commits; this follow-up does not change those historical results or open P10/P11.

## Findings and corrections

- `InstanceSettingsSection.tsx` updated only the saved section and copied its revision onto other stale sections. Playwright MCP reproduced disabling Voice while Provider preferences continued displaying `primary-stt`; HTTP effective settings already returned null. Saving now refreshes every section, preserving unrelated drafts and expansion. A failed refresh identifies the committed save, removes actionable stale settings, and recovers through Retry without replaying the patch.
- The same component used one read generation to guard save completion. Re-entering Settings while a delayed post-save read was pending superseded the save and left busy controls disabled indefinitely. A deterministic failing regression reproduced this before correction. A pending save now owns its refresh across tab re-entry; owner switches still invalidate late responses. The corrected regression and actual browser tab navigation pass.
- The Model defaults reasoning picker used API insertion order and did not resolve a null catalog key to the host default. It now uses the shared semantic effort ordering and distinguishes pending set, clear and inherited values. A fixture with unordered host efforts and a different Definition model verifies the correct choices; native Synthetic UI selected and saved `high` with effective `catalogKey:null`.
- Current architecture, Automation UI and protocol prose still called the conversation model permanently pinned. Wording now reflects fresh-Run current defaults, retained explicit Session preferences and immutable admitted-Run retry selection. The scoped HTTP status description now matches dependency validation (400) versus stale revisions (409).

The static audit also traced typed sparse resolution and authority restrictions, defensive copying, coherent resource reads, current owner-generation resolution, immutable Run manifests, resource dependencies, atomic adoption and Automation admission. No additional actionable backend defect was identified in these reviewed paths. Static inspection is separate from the runtime evidence below.

## Local verification

All hosts used Synthetic and disposable SQLite at `/private/tmp/scoped-review-20261010.db`; no provider credentials or user database reset were needed.

| Check | Result |
| --- | --- |
| Scoped API integration | 14 passed across InMemory/SQLite: sparse settings, resource isolation/bytes, conflict/dependency rejection, adoption, promotion, frozen waiting Runs and fresh existing/background occurrence admission. |
| Application resolver/model binding | 12 passed: sparse inheritance, presence/clear, authority rejection, tri-state Skills and explicit model/effort retention. |
| Populated scoped migration | 1 passed: historical choices and reasoning intent preserved, recoverable Run pins reconstructed, unavailable provenance fails closed. |
| Corrected settings/budgets/reasoning unit group | 19 passed, including five new settings regressions. |
| Existing Admin unit surface | 42 passed. The initial combined run also executed the new tab regression against the pre-correction loaded component and failed it; the corrected focused rerun passes. |
| Existing Automation unit surface | 22 passed: owner authoring, policy restrictions, failed draft retention, source selection, pagination, revision locking and polling lifecycle. |
| Frontend build | Passed; existing large-bundle warning remains. |
| Scoped Playwright | 2 passed: sparse save/reset/conflict/resource recovery plus cross-section refresh, delayed-read tab re-entry and committed-save/read-failure retry. |
| Existing Skills Playwright | 4 passed: authorized Chat creation, restricted-role denial, upgrade/rollback isolation and retained drawer drafts. |
| Existing Automation Playwright | 3 passed: exact-conversation greeting after navigation/reload, destination/reporting validation and responsive editor failure/recovery. |

Playwright MCP independently reproduced the stale provider display, verified the corrected refresh with an unsaved `fr-FR` draft, injected a settings GET 503, retried, and observed exactly one committed patch. It also navigated Profile → Settings while a refresh was deliberately delayed and observed released controls and correct effective provider values. Console/network inspection found only the injected 503 among these settings-flow failures; no unexpected warnings or errors appeared. Model-default reasoning choices were `low`, `medium`, `high`, and saving `high` preserved sparse inheritance.

Commands: `dotnet test` with `--no-restore --nologo --disable-build-servers -m:1 -p:UseSharedCompilation=false` and filters `ScopedInstanceConfigurationTests`, `InstanceSettingsResolverTests|SessionModelBinderTests`, `ScopedConfigurationMigrationTests`; `pnpm --dir web exec vitest run` with the named settings, budgets, reasoning, Admin and Automation files and `--maxWorkers=1 --minWorkers=1`; `pnpm --dir web build`; `pnpm --dir web exec playwright test` with scoped configuration, Skills, Automation editor and destinations under `--project=synthetic`. Browser runs used isolated API/Web ports 5289/5189 and `PLAYWRIGHT_FAITHFUL_MANUAL=1` to omit unrelated extra hosts.

Local command logs are `/private/tmp/scoped-review-*.log`; they are disposable evidence. This record reports local checks. The exact resulting commit’s full hosted Synthetic status is reported separately in the final review handoff; earlier green hosted runs do not verify these corrections. No migration, Compose packaging or audio implementation changed in this follow-up.
