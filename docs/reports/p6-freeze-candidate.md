# P6 — Durable background work and triggered execution

**P6 is not frozen.** This report is the freeze candidate. Independent review of proposal §§20/22 has not accepted it, and hosted `.github/workflows/synthetic.yml` has not run on the candidate SHA. Do not start P7. Phase I WorkItems for Support, Compliance, and `sandbox.run` after `RequestDeactivate` remain not-applicable.

## Candidate

| Item | Value |
| --- | --- |
| Behavior through | `7a22b25166f4c95bdabf4cf8052fe617137bfccb` (`7a22b25`) |
| Candidate commit | the W10 commit that adds this report and the Playwright isolation fix |
| Hosted workflow | **not run** — `gh auth status` reports no GitHub login, so the candidate SHA was not pushed |
| P5 baseline preserved | `4bbc0c17bc54746f87fd211174690659869e3e45`, workflow `35954811544` |
| Pre-P6 HEAD | `a92af7b6a3542472177d9c8894d35705e49b7774`, workflow `35957193903` |

A later documentation-only descendant may record a workflow id. That descendant is not the implementation freeze SHA. Parent or descendant CI is not substitute evidence.

## What shipped

- One `WorkItem` per `AwaitingDurableWork` occurrence. Owner is Agent Instance plus trusted profile. The source session is provenance only.
- The existing scheduler pass, after P5 routing, accepts awaiting occurrences and runs due work. There is no second scheduler.
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
| AC-P6-24 | Manual A below |
| AC-P6-25 | This report. Hosted exact-SHA gate is still open |
| RULE-01..18 | Covered by the batches above. Review 0014 passed W09 at `7a22b25` |
| RULE-19 | P4 and P5 freeze reports were not rewritten as P6 evidence. Phase I stays not-applicable |
| RULE-20 | Freeze is not claimed. The hosted gap is explicit |

## Manual evidence

Profile **Synthetic**. Runtime behavior is `7a22b25`. Disposable host: API `http://127.0.0.1:5098`, SQLite `/tmp/agent-core-manual-p6.db`, UI `http://127.0.0.1:5190`.

### Manual A — wall-clock one-shot — pass, with a scripted-result limit

- Session `060bcede-7e43-4238-a30d-7645dd05b833`, Riley, `general-assistant` v10. The chat was ended before the due instant.
- “check the oven in one minute” did not create a schedule. “check the oven in 1 minute for me” did.
- Registration `01a0d2d5-8803-769e-8f53-e6e1ce24aa57`, intent `check the oven`, created `2026-09-24T09:53:24.483Z`.
- Occurrence `f605d127-3a4e-e453-bc88-5de6afc8c141` observed `2026-09-24T09:54:24.633Z`, disposition `AcceptedDurable` (6), linked to one WorkItem.
- WorkItem `01a0d2d6-733b-726f-8e44-d7535141bc84`, status Completed, attempt 1, result completed `2026-09-24T09:54:25.746Z`.
- Result text: `Hello from synthetic.` The scripted model does not restate “check the oven”.
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
- Review 0015 accepts this equivalent: the Background Work cancel survived restart, and the stale-generation fence stays `DurableWorkJourneyTests`.

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

- Exact-SHA hosted Synthetic job and Compose job. `gh auth status` reports no GitHub login, so this candidate was not pushed and has no workflow id. Local gate results are not that evidence.
- Manual A does not show an intent paraphrase. The scripted completion text is `Hello from synthetic.`
- P6 freeze, and any P7 work.
