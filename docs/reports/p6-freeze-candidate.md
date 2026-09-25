# P6 — Durable background work and triggered execution

**P6 closed/frozen.** **Final P6 behavior freeze** is [`2455de9`](https://github.com/trannamtrung1st/agent-core/commit/2455de938ad4a156189ea7affcea1ca940cd4ec1) (`2455de9`): the last behavior-affecting P6 tree (Synthetic reminder-delivery harness, e2e alignment, `data/**` compile exclude). **Core durable-runtime repair** completed at [`2067a44`](https://github.com/trannamtrung1st/agent-core/commit/2067a44a1534623dafc7803d14b8833ee1ba7890) (`2067a44`); hosted workflow [`36031813141`](https://github.com/trannamtrung1st/agent-core/actions/runs/36031813141) is **green** on that exact SHA. **Faithful Manual A** passed on 2026-09-25 on the **`2455de9` tree** (not on plain `2067a44`): wall-clock detached reminder exercising frozen P6 core through the corrected Synthetic fixture; result `Oven is ready.`. Descendant [`c7434f3`](https://github.com/trannamtrung1st/agent-core/commit/c7434f3d2d8d0c91c89da4671bde4ce417bb706d) is **documentation-only** (freeze-record SHA pointer in this table).

The prior freeze on `6900bc1` is superseded for implementation behavior but remains historical evidence (including scripted Manual A/C on Synthetic). **P7** is the next phase and is not implemented. Phase I WorkItems for Support, Compliance, and `sandbox.run` after `RequestDeactivate` remain not-applicable.

### Repair sequence (post-`6900bc1`)

| Commit | Focus |
| --- | --- |
| `458b58b` | Scheduler/worker boundary + replay safety |
| `d5ca8bf` | Independent intake + operation fence lifecycle |
| `013f707` | Crash/result recovery + cancellation evidence |
| `0291b5b` | Durable `ToolCallId` operation identity |
| `2020724` | Legacy SQLite load, multi-tool resume, schedule vs automation UX |
| `165237d` | Conservative legacy `Succeeded` + batch step reservation |
| `2067a44` | Cross-version resumed `StepCount` normalization |
| `2455de9` | Synthetic reminder-delivery harness, Manual A Playwright, durable e2e alignment |

## Candidate

| Item | Value |
| --- | --- |
| **Final P6 behavior freeze SHA** | `2455de938ad4a156189ea7affcea1ca940cd4ec1` (`2455de9`) |
| Hosted workflow (`2455de9`, exact SHA) | [`36081962547`](https://github.com/trannamtrung1st/agent-core/actions/runs/36081962547) — Synthetic Compose smoke **success**; exact-SHA offline gates **pending** at last review |
| **Core durable-runtime repair SHA** | `2067a44a1534623dafc7803d14b8833ee1ba7890` (`2067a44`) |
| Hosted workflow (`2067a44`, exact SHA) | [`36031813141`](https://github.com/trannamtrung1st/agent-core/actions/runs/36031813141) — **success**. Synthetic offline gates success. Synthetic Compose smoke success |
| Freeze-record docs descendant | `c7434f3d2d8d0c91c89da4671bde4ce417bb706d` (`c7434f3`), workflow [`36081974554`](https://github.com/trannamtrung1st/agent-core/actions/runs/36081974554) — **documentation-only**; Compose smoke success; exact-SHA offline gates pending at last review |
| Prior implementation freeze (superseded) | `6900bc1d0f0331f8696fc59acdfe7be49d50ebf2` (`6900bc1`), workflow [`35990145456`](https://github.com/trannamtrung1st/agent-core/actions/runs/35990145456) attempt 2 |
| P5 baseline preserved | `4bbc0c17bc54746f87fd211174690659869e3e45`, workflow `35954811544` |
| Faithful Manual A | **pass** on **`2455de9` tree** (2026-09-25, Synthetic, disposable SQLite `/tmp/agent-core-manual-p6-faithful.db`) |

Exact-SHA hosted gates on `2455de9` are required for full freeze evidence; parent `2067a44` gates do not substitute. A descendant docs-only commit is not a behavior freeze SHA.

## What shipped

- One `WorkItem` per `AwaitingDurableWork` occurrence. Owner is Agent Instance plus trusted profile. The source session is provenance only.
- After post-freeze repair: P5 scheduling/routing stays in `TriggerSchedulerHostedService`; `DurableWorkHostedService` runs intake and due work. There is no second recurrence scheduler.
- A scheduled reminder is tool-free and does not write chat history.
- An application event uses the live tool limits and a narrower offer. Session-scoped tools and trigger writes are not available.
- Sensitive tools suspend without a claim until Background Work approves the exact action hash. The same WorkItem resumes.
- Cancellation is durable. A stale generation cannot complete the item.
- Background Work lists, cancels, and decides owner work for a live, paused, or ended session. Results stay out of the transcript.
- Migrations: `20260924060915_WorkItemContracts`, `20260924065742_OccurrenceDurableWorkLink`.

## Acceptance and rules

| ID | Evidence |
| --- | --- |
| AC-P6-01..02 | W01–W02 store and atomic intake tests, InMemory and SQLite |
| AC-P6-03..07 | W03 headless reminder; W05 application-event branch |
| AC-P6-08..09 | W05 narrower tool offer and forged session/trigger calls |
| AC-P6-10..14 | W04 claim recovery, bounded retry, cancellation, indeterminate side effects |
| AC-P6-15..17 | W06 durable exact-action approval on the same WorkItem |
| AC-P6-18..21 | W07 owner-scoped HTTP; W08 Background Work drawer; result route only |
| AC-P6-22 | Domain, Application, Infrastructure, and API suites below; Playwright 50 passed |
| AC-P6-23 | SQLite reopen journeys; Compose volume recreation |
| AC-P6-24 | Faithful Manual A below — **pass**. Historical scripted Manual A on `6900bc1` retained for comparison |
| AC-P6-25 | This report. Core repair gated on `2067a44` / `36031813141`; final tree `2455de9` / `36081962547` (offline pending at last review) |
| RULE-01..18 | Covered by the batches above. Review 0014 passed W09 at `7a22b25` |
| RULE-19 | P4 and P5 freeze reports were not rewritten as P6 evidence. Phase I stays not-applicable |
| RULE-20 | P6 is closed/frozen on final behavior SHA `2455de9` (core repair `2067a44`; Manual A on `2455de9` tree). P7 is next and is not implemented |

## Manual evidence

### Manual A (faithful, detached wall-clock) — pass

Profile **Synthetic**. Disposable host: API `http://127.0.0.1:5098`, SQLite `/tmp/agent-core-manual-p6-faithful.db`, UI `http://127.0.0.1:5190`. **Tree at `2455de9`** (includes Synthetic reminder-delivery scripting in `ScriptedLanguageModel` for `Scheduled reminder delivery mode` + stored intent). This manual did **not** run on plain `2067a44`; it validated frozen P6 core behavior through the corrected Synthetic harness.

- User text: `check the oven in 1 minute for me` → schedule intent `check the oven`.
- Session ended before the due instant (`This conversation has ended.`).
- After ~75s wall clock: one occurrence `AcceptedDurable` (disposition 6), one WorkItem completed.
- Occurrence `e2b62c55-b74c-7f56-97f0-e801bb7d24d5` → WorkItem `01a0d620-ca48-7424-92db-bd7a576382eb`.
- Result text: `Oven is ready.` (intent-faithful; not `Hello from synthetic.`).
- Background Work showed the same result after reload; transcript did not gain a new assistant turn with that result.
- Automated replay (opt-in, ~75s wall clock): `CI=1 PLAYWRIGHT_SQLITE_PATH=/tmp/agent-core-manual-p6-faithful.db PLAYWRIGHT_API_PORT=5098 PLAYWRIGHT_WEB_PORT=5190 pnpm exec playwright test --project=faithful-manual` (excluded from default Synthetic CI suite).

### Manual A (historical, `6900bc1`) — wall-clock one-shot — pass, with a scripted-result limit

Profile **Synthetic**. Runtime behavior is `7a22b25`. Disposable host: API `http://127.0.0.1:5098`, SQLite `/tmp/agent-core-manual-p6.db`, UI `http://127.0.0.1:5190`.

- Session `060bcede-7e43-4238-a30d-7645dd05b833`, Riley, `general-assistant` v10. The chat was ended before the due instant.
- “check the oven in one minute” did not create a schedule. “check the oven in 1 minute for me” did.
- Registration `01a0d2d5-8803-769e-8f53-e6e1ce24aa57`, intent `check the oven`, created `2026-09-24T09:53:24.483Z`.
- Occurrence `f605d127-3a4e-e453-bc88-5de6afc8c141` observed `2026-09-24T09:54:24.633Z`, disposition `AcceptedDurable` (6), linked to one WorkItem.
- WorkItem `01a0d2d6-733b-726f-8e44-d7535141bc84`, status Completed, attempt 1, result completed `2026-09-24T09:54:25.746Z`.
- Result text: `Hello from synthetic.` The scripted model does not restate “check the oven”. Whole-task review 0018 accepts this scripted result.
- Chat stayed four rows: the non-schedule hello, and `Scheduled Call John.` No second work item. Background Work showed the same completed result after reload.

### Manual B — approval across API restart — pass

- Session `52754df5-726d-4687-9daa-51ddf4afee42`, Harper, `approval-demo`, ended before the event was admitted.
- Occurrence `5b688ef3-42aa-4544-a0d2-581e706c52d3` admitted `2026-09-24T09:56:45.578Z`.
- WorkItem `01a0d2d8-9a1f-744c-ac45-53a85e108e84`. Preview: `Run sensitive demo action: Synthetic sensitive approval`.
- The API process was stopped and started again on the same SQLite file. The drawer still showed Needs approval.
- Approve stored decision 1 at approval revision 2. Result completed `2026-09-24T09:58:30.097Z`: `Sensitive action completed after approval.` Attempt count stayed 1.
- The transcript stayed `hello` / `Hello from synthetic.`

### Manual C — cancel, then restart — pass as equivalent evidence

- WorkItem `cbbad6f6-9c0e-4bf1-b0dc-1e8d82c58876` was seeded `WaitingToRetry` with a future retry time, then cancelled in Background Work at `2026-09-24T09:57:46.152Z`.
- After the same API restart, the drawer still showed Cancelled. No completion text was stored.
- The stale-worker fence is `DurableWorkJourneyTests`: an expired claim with cancellation requested becomes Cancelled, and a stale `CompleteAsync` is `Conflict`. That race was not repeated by hand.
- Review 0015 accepts this equivalent: the Background Work cancel survived restart, and the stale-generation fence stays `DurableWorkJourneyTests`. Whole-task review 0018 accepts the same equivalent.

## Local gate

These commands were not one uninterrupted shell. Each listed result finished on this tree. The Playwright file change that deactivates other attached runtimes is included in the passing Playwright and Compose run. It does not change the .NET suites.

| Command | Result |
| --- | --- |
| `npm ci` in `tests/realtime-js` | passed |
| Domain `dotnet test` | 98 passed |
| Infrastructure `dotnet test` | 316 passed, 7 skipped (opt-in live probes) |
| Application `dotnet test --blame-hang --blame-hang-timeout 5m` | 712 passed, 1 skipped; no hang sequence |
| API `dotnet test` | first suite: 174 passed, 1 timeout (`Refresh_attach_loads_interrupted_assistant_after_previous_runtime_disposes` at 15s while a manual host was also running). Isolated re-run passed in 847ms. Full API re-run: **175 passed** |
| web `pnpm install --frozen-lockfile`, `pnpm run test --run`, `pnpm run build` | 54 files, **405 passed**; production build succeeded |
| `CI=1 pnpm exec playwright test` | **50 passed** (2.5m) after the isolation fix, on a fresh `data/playwright/synthetic.db` |
| `./scripts/compose-sqlite-volume.sh` | `compose sqlite volume check passed`; completed result and pending approval survived recreate |

Earlier Playwright attempts on a dirty `synthetic.db` failed the empty Background Work assertion, and a due reminder was accepted live by a Riley session still inside the 30-second detach grace. `e2e/durable-journeys.spec.ts` now deactivates other attached sessions before forcing the reminder due.

## Unverified

- P7 is the next phase and is not implemented.
- Phase I WorkItems for Support, Compliance, and `sandbox.run` after `RequestDeactivate` remain not-applicable.
