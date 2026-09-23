# P5 — Events, durable triggers, and configurable scheduling

This report is the P5 closure candidate. P5 is **not frozen** until the hosted Synthetic workflow is green on the commit that contains this report and the whole-phase review accepts proposal §21 and §26. Do not begin P6 from this candidate.

## Candidate

| Item | Value |
| --- | --- |
| Implementation behavior | `44a1b87` (`44a1b87cf0275c96f3ac3c5e19fafb53c737242c`) |
| Closure documentation | the commit that adds this report |
| Hosted Synthetic workflow | not run: `gh` is not authenticated, so this closure commit has no hosted run id |
| Optional hosted provider probes | skipped; no credentials required for the scheduler |

An admitted occurrence with no single compatible live runtime stops at `AwaitingDurableWork`. P5 does not create a `WorkItem`, keep a Session Runtime alive for a future fire, or expose a public webhook.

## What shipped

- Owner is Agent Instance + trusted profile. The source session is provenance.
- `ITriggerStore` holds registrations and occurrences. Migration `20260923160000_TriggerContracts` adds those tables with no session or memory foreign keys.
- One-shot, daily, and weekly schedules use a trusted timezone. Ambiguous local times use the earlier UTC offset. A missing local time shifts forward across the gap. Weekly interval phase follows the Monday-based local week of the stored next occurrence.
- Missed one-shots fire once. Missed recurring slots coalesce. Occurrence ids are stable. Delivery is at-least-once, not exactly-once.
- An explicit current user turn may create, list, update, or cancel without a second approval dialog. Initiative, environment, history, memory, and occurrence runs cannot. A pending proposal survives only for one immediate confirming user turn.
- Creating a schedule does not approve `email.send` or any other later sensitive tool.
- Definitions without `TriggerPolicy` cannot schedule. `general-assistant` v8 can. `customer-support` v2 can admit application events only.
- Durable `order_status_changed` uses a separate ingress from the live-only environment path and the same occurrence boundary.
- One compatible live runtime accepts the occurrence only after `AcceptedLive` is stored. Otherwise the row waits for P6.
- `GET /api/v2/sessions/{sessionId}/triggers` and `POST .../triggers/{triggerId}/cancel` derive the owner from the session. The DTO is intent, schedule, timezone, status, next occurrence, and revision.
- The chat header Schedules drawer lists and cancels that owner scope.

## Acceptance mapping

| ID | Evidence |
| --- | --- |
| AC-E2E-01 | `Natural_one_shot_can_be_listed_moved_and_cancelled_without_approval`, `Due_boundaries_one_shot_recovery_and_recurrence_are_idempotent`, `Sqlite_restart_admits_a_missed_one_shot_once`, `Schedule_remains_visible_after_sqlite_restart`, Playwright `e2e/schedules.spec.ts` |
| AC-E2E-02 | `Natural_monday_schedule_keeps_an_indefinite_wall_clock_recurrence`, `Missed_daily_slots_coalesce_and_weekly_wall_clock_advances`, `London_weekly_schedule_does_not_become_a_fixed_utc_interval` |
| AC-E2E-03 | The natural list/move/cancel runtime test and `List_and_cancel_are_owner_scoped_and_hide_scheduler_internals` |
| AC-E2E-04 | Pending-proposal tests: unrelated turn, queued suffix, idle speech final, user steer, and stored-proposal confirmation |
| AC-E2E-05 | `Occurrence_evidence_is_not_a_user_turn_and_email_still_requires_approval` |
| AC-E2E-06 | SQLite scheduler restart, `Pending_occurrence_survives_sqlite_restart`, API list after host reopen |
| AC-E2E-07 | `Both_stores_create_list_update_cancel_and_isolate_owners`, owner-scoped API not-found/conflict, `Missing_owner_does_not_admit_or_hand_off` |
| AC-E2E-08 | `Valid_order_event_routes_once_and_invalid_payloads_do_not` |

## Local key-free gate

Commands match `.github/workflows/synthetic.yml`. They ran on the working tree that already contained the P5 implementation (`44a1b87`) and these documentation edits.

| Command | Result |
| --- | --- |
| `npm ci` in `tests/realtime-js` | passed |
| Domain `dotnet test` | 83 passed |
| Infrastructure `dotnet test` | 287 passed, 7 skipped |
| Application `dotnet test --blame-hang --blame-hang-timeout 5m` | 627 passed, 1 skipped |
| API `dotnet test` | 169 passed |
| web `pnpm install --frozen-lockfile`, `pnpm run test --run`, `pnpm run build` | 52 files, 397 passed; build succeeded |
| `CI=1 pnpm exec playwright test` | 48 passed |
| `./scripts/compose-sqlite-volume.sh` | passed (`compose sqlite volume check passed`) |

Infrastructure skips are the opt-in OpenAI and OpenRouter probes. The Application skip is the opt-in Real historical-image reread. Optional hosted provider probes were not run.

## §26 answers on this candidate

The local evidence answers yes for natural one-shot and recurring create, restart, timezone meaning, duplicate admission, inspect/cancel, owner isolation, current-user authorization, remembered text, untrusted occurrence data, no standing tool approval, runtime-local timers, one typed non-schedule source, and a P6 handoff that does not require redesigning the occurrence. P0–P4 suites above stayed green. The hosted workflow answer is still open, so P5 is not closed.

## Limitations that do not reopen the phase

- There is no public webhook, rate limit beyond the bounded evidence payload, calendar UI, or admin trigger console.
- HTTP management is list and cancel. Update and create stay on the agent tools.
- Ant Design `List` is still the drawer list. Its deprecation warning is expected.
- `AwaitingDurableWork` is not executed. P6 owns that work.
