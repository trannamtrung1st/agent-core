# P5 — Events, durable triggers, and configurable scheduling

This report is the P5 closure candidate after the whole-phase review repairs. Hosted Synthetic workflow [`35840226344`](https://github.com/trannamtrung1st/agent-core/actions/runs/35840226344) is **green** on `267fcbd` and is historical. That review did **not** freeze P5. The repairs below stay inside P5. Local key-free and Compose gates on the repair tree passed. Hosted run [`35884024153`](https://github.com/trannamtrung1st/agent-core/actions/runs/35884024153) on `53ed9c1` failed in Synthetic Playwright; Compose smoke on that run succeeded. Hosted run [`35886400983`](https://github.com/trannamtrung1st/agent-core/actions/runs/35886400983) on `4687211` failed the same suite: the long-session composer matched queued-message labels, and the reconnect voice click ran while Voice still cancelled durable voice mode. Hosted run [`35889243368`](https://github.com/trannamtrung1st/agent-core/actions/runs/35889243368) on `7243323` is **green** for offline gates and Compose smoke. P5 is not frozen. Do not begin P6.

## Candidate

| Item | Value |
| --- | --- |
| Implementation behavior | `44a1b87` (`44a1b87cf0275c96f3ac3c5e19fafb53c737242c`), review repairs on `53ed9c1` |
| Browser gate follow-up | `7243323` (`7243323bfd02dffb4616fac511e20d05af9eca67`) |
| Hosted Synthetic workflow | [`35889243368`](https://github.com/trannamtrung1st/agent-core/actions/runs/35889243368) — **green** on `7243323` (push to `main`, 2026-09-23). Synthetic offline gates and Compose smoke both succeeded. Historical green run [`35840226344`](https://github.com/trannamtrung1st/agent-core/actions/runs/35840226344) remains the pre-review evidence on `267fcbd`. |
| Optional hosted provider probes | skipped; no credentials required for the scheduler |

An admitted occurrence with no single compatible live runtime stops at `AwaitingDurableWork`. P5 does not create a `WorkItem`, keep a Session Runtime alive for a future fire, or expose a public webhook.

## What shipped

- Owner is Agent Instance + trusted profile. The source session is provenance.
- `ITriggerStore` holds registrations and occurrences. Migration `20260923160000_TriggerContracts` adds those tables with no session or memory foreign keys. Migration `20260923220000_OwnerScopedOccurrenceDedupe` scopes occurrence dedupe to the owner.
- One-shot, daily, and weekly schedules use a trusted timezone. Ambiguous local times use the earlier UTC offset. A missing local time shifts forward across the gap. Weekly interval phase follows the Monday-based local week of the stored next occurrence. A recurring `startDate` is the cadence anchor, including when that date is already past.
- Routing rejects a pending occurrence whose captured schedule revision no longer matches the registration.
- A current user turn authorizes only the schedule action that turn requested. Separate turns can create, list, move, and cancel.
- Occurrence dedupe is unique per Agent Instance, profile, and dedupe key.
- `AcceptedLive` is stored before speech. If that runtime does not begin the occurrence, the row returns to `Pending`.
- Scheduled reminders speak. Application-event visibility is a separate decision; the allowlisted order-status occurrence is user-visible, and admission alone does not force speech for a future typed source.
- Missed one-shots fire once. Missed recurring slots coalesce. Occurrence ids are stable. Delivery is at-least-once, not exactly-once.
- An explicit current user turn may create, list, update, or cancel without a second approval dialog. Initiative, environment, history, memory, and occurrence runs cannot. A pending proposal survives only for one immediate confirming user turn.
- Creating a schedule does not approve `email.send` or any other later sensitive tool.
- Definitions without `TriggerPolicy` cannot schedule. `general-assistant` v8 can. `customer-support` v2 can admit application events only.
- Durable `order_status_changed` uses a separate ingress from the live-only environment path and the same occurrence boundary.
- One compatible live runtime accepts the occurrence only after `AcceptedLive` is stored. If begin then fails, the row returns to `Pending` and can be delivered or handed to P6 on a later route. Otherwise a row with no single compatible runtime waits for P6.
- `GET /api/v2/sessions/{sessionId}/triggers` and `POST .../triggers/{triggerId}/cancel` derive the owner from the session. The DTO is intent, schedule, timezone, status, next occurrence, and revision.
- The chat header Schedules drawer lists and cancels that owner scope.

## Acceptance mapping

| ID | Evidence |
| --- | --- |
| AC-E2E-01 | `Separate_turns_create_list_move_and_cancel_without_shared_authority`, `Natural_one_shot_can_be_listed_moved_and_cancelled_without_approval`, `Due_boundaries_one_shot_recovery_and_recurrence_are_idempotent`, `Sqlite_restart_admits_a_missed_one_shot_once`, `Schedule_remains_visible_after_sqlite_restart`, Playwright `e2e/schedules.spec.ts` |
| AC-E2E-02 | `Natural_monday_schedule_keeps_an_indefinite_wall_clock_recurrence`, `Missed_daily_slots_coalesce_and_weekly_wall_clock_advances`, `London_weekly_schedule_does_not_become_a_fixed_utc_interval` |
| AC-E2E-03 | The natural list/move/cancel runtime test and `List_and_cancel_are_owner_scoped_and_hide_scheduler_internals` |
| AC-E2E-04 | Pending-proposal tests: unrelated turn, queued suffix, idle speech final, user steer, and stored-proposal confirmation |
| AC-E2E-05 | `Occurrence_evidence_is_not_a_user_turn_and_email_still_requires_approval` |
| AC-E2E-06 | SQLite scheduler restart, `Pending_occurrence_survives_sqlite_restart`, API list after host reopen |
| AC-E2E-07 | `Both_stores_create_list_update_cancel_and_isolate_owners`, `Occurrence_admission_dedupes_and_hides_other_owners`, owner-scoped API not-found/conflict, `Missing_owner_does_not_admit_or_hand_off` |
| AC-E2E-08 | `Valid_order_event_routes_once_and_invalid_payloads_do_not` |

## Local key-free gate

Commands match `.github/workflows/synthetic.yml`. They ran on the review-repair working tree (stale schedule revision, lost begin, action-specific authorization, owner-scoped dedupe, `startDate` phase, and JSON evidence). Hosted workflow `35840226344` is not evidence for this tree. Hosted run `35884024153` on `53ed9c1` failed at Synthetic Playwright after the other offline steps and Compose smoke succeeded.

| Command | Result |
| --- | --- |
| Domain `dotnet test` | 83 passed |
| Infrastructure `dotnet test` | 287 passed, 7 skipped |
| Application `dotnet test --blame-hang --blame-hang-timeout 5m` | 635 passed, 1 skipped |
| API `dotnet test` | 169 passed |
| web `pnpm run test --run`, `pnpm run build` | 52 files, 397 passed; build succeeded |
| `CI=1 pnpm exec playwright test` | 48 passed |
| `./scripts/compose-sqlite-volume.sh` | passed (`compose sqlite volume check passed`) |

Infrastructure skips are the opt-in OpenAI and OpenRouter probes. The Application skip is the opt-in Real historical-image reread. Optional hosted provider probes were not run. Web dependencies were already installed; this pass did not repeat `pnpm install --frozen-lockfile`.

## §26 answers on this candidate

The local evidence answers yes for natural one-shot and recurring create, separate-turn list/move/cancel, restart, timezone meaning, owner-scoped duplicate admission, inspect/cancel, current-user action authorization, remembered text, untrusted occurrence data, no standing tool approval, runtime-local timers, one typed non-schedule source, stale-schedule rejection, lost-begin recovery, and a P6 handoff that does not require redesigning the occurrence. P0–P4 suites above stayed green. Hosted Synthetic run `35889243368` on `7243323` is **green**. Earlier repair pushes `35884024153` and `35886400983` are not. P5 remains **not frozen** until review accepts proposal §21 and §26. Do not begin P6.

## Limitations that do not reopen the phase

- There is no public webhook, rate limit beyond the bounded evidence payload, calendar UI, or admin trigger console.
- HTTP management is list and cancel. Update and create stay on the agent tools.
- Ant Design `List` is still the drawer list. Its deprecation warning is expected.
- `AwaitingDurableWork` is not executed. P6 owns that work.
