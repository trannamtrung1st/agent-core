# P5 — Events, durable triggers, and configurable scheduling

This report records the **P5 implementation freeze**. Whole-phase review accepted the architecture and repair chain ending on `4bbc0c1`. Do not reopen P5 without a reproducible regression or an explicit product requirement that belongs in a later phase rather than P6+.

## Freeze status

**P5 is frozen** on implementation HEAD **`4bbc0c1`** (`4bbc0c17bc54746f87fd211174690659869e3e45`, 2026-09-24). Hosted Synthetic offline gates and Compose smoke are **green** on that exact SHA (workflow [**`35954811544`**](https://github.com/trannamtrung1st/agent-core/actions/runs/35954811544)). **P6** is frozen on `30adaeb` (workflow `36085265506`); **P7** is next.

| Item | Value |
| --- | --- |
| **P5 implementation freeze** | **`4bbc0c1`** |
| Verified hosted Synthetic + Compose | workflow **`35954811544`** — **green** on `4bbc0c1` |
| Repair chain (historical) | `25f4355` → `11237fc` → `4f7a7bd` → `4bbc0c1`; parent runs `35954173257` and `35952177117` green on `4f7a7bd` / `11237fc` |
| Initial P5 implementation bulk | `44a1b87` (historical baseline before whole-phase review repairs) |

A documentation-only descendant commit records this freeze; it is not a new implementation baseline.

## What shipped

- **Ownership:** durable registrations and occurrences are scoped to **Agent Instance + trusted profile**. The source session is provenance only.
- **Persistence:** `ITriggerStore` holds registrations and occurrences. Runtime `SessionRuntime` timers remain **ephemeral** lifecycle-local delays, not the durable scheduler.
- **Schedule kinds:** **OneShot**, **Daily**, **Weekly**, and **FixedInterval** are first-class domain schedules. Calendar schedules preserve wall-clock/timezone semantics (ambiguous local time, DST gaps, weekly phase anchored to stored next occurrence, `startDate` as cadence anchor). **Fixed intervals** preserve elapsed-duration semantics with bounded minimum cadence (`TriggerPolicy`), optional **`EndAtUtc`**, **`maxOccurrences`**, **O(1) missed-slot coalescing** bounded by `EndAtUtc`, and partial conversational updates via `trigger.update` (`intervalSeconds`, `endAtUtc`, and related fields).
- **Trusted scheduling context:** profile timezone and current-time interpretation for wall-clock fields; **relative delays** (`relativeDelaySeconds`, etc.) are computed at command time and do not require a profile timezone.
- **Admission and delivery:** missed one-shots fire once; missed recurring slots coalesce with deterministic skipped counts; occurrence ids and dedupe keys are stable; delivery is **at-least-once**. Routing rejects occurrences whose captured **schedule revision** no longer matches the registration. **Owner-scoped** occurrence dedupe.
- **Authorization:** current user turn authorizes only the **requested schedule action** (create / list / update / cancel). Initiative, environment, history, memory, and occurrence runs cannot manage schedules. **Occurrence evidence does not grant tool authority.** Creating a schedule does not approve later sensitive tools.
- **Conversational hardening:** bounded **schedule referents** (`ScheduleConversationContext`); **one-follow-up schedule drafts** after recoverable validation failures (`ScheduleDraftAdmission` before `AgentContext`); interval-only corrections retain draft intent; complete new requests with their own intent do not inherit a stale draft.
- **Occurrence delivery:** scheduled reminders use **reminder-only delivery** (no schedule tools on `ScheduledOccurrence` turns). Application-event visibility is a separate product decision; the allowlisted **`order_status_changed`** durable ingress normalizes into the same occurrence boundary.
- **Live vs handoff:** compatible live runtime accepts after **`AcceptedLive`** is stored; failed begin returns to **`Pending`**. When no single compatible runtime exists, the row stops at **`AwaitingDurableWork`**. **P5 does not execute `AwaitingDurableWork`; P6 owns headless/background execution.**
- **Policy and definitions:** `TriggerPolicy` on Agent Definitions gates schedule kinds and limits. **`general-assistant` v10** enables the final P5 scheduling surface (including fixed interval). **v8/v9 remain immutable** historical definitions. **Durable scheduling policy** at create/update is aligned with firing-time eligibility; compatibility-instance forward alignment preserves pinned sessions.
- **Management surface:** `GET /api/v2/sessions/{sessionId}/triggers` and cancel derive owner from the session. Chat **Schedules** drawer lists **newest first** with intent, schedule summary, timezone, status, next occurrence, and revision; **Suspended** shows reason where applicable. HTTP management is list and cancel; create/update stay on agent tools.

## Acceptance mapping

| ID | Evidence |
| --- | --- |
| AC-E2E-01 | Separate-turn create/list/update/cancel, natural one-shot flow, due boundaries, SQLite restart, Playwright `e2e/schedules.spec.ts` |
| AC-E2E-02 | Daily/weekly wall-clock and DST tests; fixed-interval minimum, update, draft, and coalescing tests (`TriggerScheduleSemanticsTests`, `TriggerScheduleCalculatorTests`, `TriggerScheduleAdmission`) |
| AC-E2E-03 | Owner-scoped list/cancel; operation-aware authorization |
| AC-E2E-04 | Pending-proposal and confirmation fencing |
| AC-E2E-05 | Occurrence evidence vs email approval |
| AC-E2E-06 | Scheduler restart and pending occurrence survival |
| AC-E2E-07 | Store parity, owner isolation, API not-found/conflict |
| AC-E2E-08 | Typed `order_status_changed` ingress |

Focused automated coverage exists for **LongSilence** initiative paths (examiner/support policies) and for **ApplicationEvent** / environment / unfinished-interaction boundaries where no public manual injection surface is required for P5 freeze.

## Key-free gate

Hosted workflow **`35954811544`** on **`4bbc0c1`** passed the repository Synthetic offline job and Compose smoke (`.github/workflows/synthetic.yml`). Provider opt-in probes were not required for P5 freeze.

Representative offline counts on the freeze tree (exact counts may drift slightly with new tests; the hosted workflow is authoritative):

| Area | Result (freeze tree) |
| --- | --- |
| Domain | 83+ passed |
| Application | 711+ passed, 1 skipped (opt-in historical image) |
| API | 169 passed |
| Infrastructure | 288 passed, 7 skipped (opt-in live probes) |
| web unit/build + Playwright | green on hosted gate |
| `./scripts/compose-sqlite-volume.sh` | passed on hosted gate |

Earlier runs (`35840226344` on `267fcbd`, `35889243368` on `7243323`, failed Playwright on intermediate repair SHAs such as `25f4355`) are **historical** only and are not current freeze evidence.

## Migrations

- `20260923160000_TriggerContracts`
- `20260923220000_OwnerScopedOccurrenceDedupe`

## Limitations that do not reopen the phase

- No public webhook or general external-event registration UI, admin trigger console, or calendar UI.
- No durable **`WorkItem`** execution in P5.
- Unattended result delivery and richer event-source adapters remain **P6+**.
- Optional **dev trigger injector** for manual `EnvironmentUpdate` / `UnfinishedInteraction` probing is deferred harness work, not a P5 stop condition.
- List/cancel require `TriggerPolicy.Enabled` but not `AllowUserScheduling` (management of existing commitments when new user scheduling is disabled is an explicit product choice).

## P6 handoff

P5 provides reliable trigger registration, scheduling, admission, normalized occurrences, and live-or-`AwaitingDurableWork` routing. **P6** owns executing or continuing work when no compatible Session Runtime is attached.
