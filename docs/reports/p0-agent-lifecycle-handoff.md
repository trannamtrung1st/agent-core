# P0 agent-lifecycle handoff

Run `run-20260917T172245-9d5497`, plan revision 3, P0-F plus review repair so the named `dotnet test AgentCore.sln` gate is green. Synthetic/offline only. Hosted LLM/speech and headset checks were not run and are not claimed. Playwright MCP was not used; repeatable evidence is `CI=1 pnpm exec playwright test` with disposable servers.

## Skills

develop, architecture, backend, frontend, realtime, persistence, providers, operations, testing, document, docs-consistency.

## Baseline (item-1ac308fe49bd)

HEAD `f118878` (later P0 commits built on that). Live primitives already included InitiativePlan clamp, IdleBackoff, SessionPauseSemantics, persist-before-ACK, ResponseEnvelope. Queue/interrupt, suffix, Send/Stop, envelope UI were delivered in later batches of this run.

## Batch → commit

| Batch | Item | Commit |
| --- | --- | --- |
| 01 | item-1ac308fe49bd inspect | none (evidence only) |
| 02 | item-e43f48b3dd70 P0-A | `e93182d` |
| 03 | item-8802fc884f9e P0-B | `1930290` |
| 04 | item-c25c89e80d6b P0-C protocol | `3ba7e7f` |
| 05 | item-47ab1f746d90 suffix | `bb2d232` |
| 06 | item-b70466c9d2ac pending-user | `5682c5c` |
| 07 | item-981a46e4872c Send/Stop | `2d24763` |
| 08 | item-aeb896d504f7 envelope | `5d1b633` |
| 09 | item-67061d5a4dfc P0-F | `f99d723` (handoff then recorded combined sln FAIL) |
| 10 | item-67061d5a4dfc P0-F sln-gate repair | `65fc8fa6470380a96576358270c79a5c425bc0ef` |
| 11 | Kestrel interrupt/cancel + queued-attachment repair | `e7f3c292332333addf26f7c293a4c0407f942989` |

Exact hashes for 03–07 match `git log` on `main` after P0-B through P0-D. Batch 10 made `dotnet test AgentCore.sln` pass. Batch 11 delivered the loopback-Kestrel interrupt/cancel-active scenarios and queued-attachment matrix.

## P0-AC01–20

| ID | Result | Code / tests | Observed this item |
| --- | --- | --- | --- |
| P0-AC01 | met | InitiativePlan clamp; `InitiativePlanTests`/`InitiativeTests`; meter `initiative.next_wait_ms` tags `source\|clamp\|mode` | Covered by P0-A commit; `InitiativePlanTests\|InitiativeTests\|SessionPauseSemanticsTests` **64 passed** |
| P0-AC02 | met | Evaluator prompt no longer ascribes wait to deactivate | P0-A |
| P0-AC03 | met | Silent cap / inactivity pause, not end | `SessionPauseSemanticsTests`; P0-B |
| P0-AC04 | met | Pause ≠ end in runtime, store, API, UI | P0-B; Playwright Resume |
| P0-AC05 | met | First-party **client pending-send queue** while live (Send→Queue); Steer=`interrupt`; wire `behavior=queue` for external clients | `realtime.queue.test.ts`; Playwright hold-the-line/Steer; JS `user-text-queue` (backend contract) |
| P0-AC06 | met | Omitted behavior = interrupt | JS `user-text-omit-interrupt-live`; `UserTextQueueTests.Omitted_behavior_interrupts_like_interrupt` |
| P0-AC07 | met | `CancelResponse` / Stop, no user entry | JS `cancel-response-active` plus idempotent/stale; `UserTextQueueTests.CancelResponse_*`; Composer Stop |
| P0-AC08 | met | Voice barge-in still interruptive | `voice-interrupt.spec.ts` (Stop then text); existing barge-in Application tests |
| P0-AC09 | met | U2+U3 one next assistant | `Two_queued_users_become_one_next_response`; Playwright `queued U2 and U3` count 2 assistants after Stop |
| P0-AC10 | met | Persist before ACK | User-text persist-then-ACK; `SqliteHostRecoveryTests` killed-before-persist |
| P0-AC11 | met | Restart/reconnect no dup | `Attach_recovers_trailing_suffix`; `Recover_keeps_trailing_queued_users`; Playwright session path refresh |
| P0-AC12 | met | History suffix, no second queue store | `TrailingUserSuffix`; no `PendingUserEntryIds` |
| P0-AC13 | met | Attachments bound to queued user entries | `QueuedAttachmentRuntimeTests` (attachment-only, text+file during R1, two queued files, SQLite restart); Playwright `queued attachment stays bound to the live send` |
| P0-AC14 | met | Pending user over initiative/pause | `Pending_suffix_blocks_long_silence_inactivity_pause_and_environment` |
| P0-AC15 | met | `ResponseEnvelope` canonical | P0-E; RichEnvelope tests |
| P0-AC16 | met | Thinking live-only | `activityState`; Playwright markdown reload without Thinking |
| P0-AC17 | met | Reconnect/history/a11y | P0-E Conversation/ChatMessage tests; markdown reopen E2E |
| P0-AC18 | met | See commands | Combined `dotnet test AgentCore.sln --nologo`: Domain 22, Application 268, Infrastructure 78 passed / 9 skip, Api 76. Live OpenAI/OpenRouter and Docker sandbox skipped as designed. |
| P0-AC19 | met | Playwright | `CI=1 pnpm exec playwright test`: **16 passed** (isolated ports/servers). |
| P0-AC20 | met | docs 16/18 table; TODO P0 `[x]`; this handoff | Canonical P0 observed table in `docs/18-implementation-plan.md` |

## Section 17 mapping

### 17.1 Domain

`TrailingUserSuffixTests` (pending, no pending after assistant, sequence). Envelope merge/fallback: `RichEnvelopeRuntimeTests`. **5 passed**.

### 17.2 Application (16 named)

| # | Case | Test |
| --- | --- | --- |
| 1 | normal turn | `Queue_while_idle_still_starts_a_turn` / synthetic text slice |
| 2 | queue no supersede | `Queue_during_live_response_persists_without_superseding` |
| 3 | two queued → one response | `Two_queued_users_become_one_next_response_and_stay_distinct` |
| 4 | distinct durable users | same |
| 5 | interrupt supersedes | JS `user-text-interrupt-live`; runtime `Omitted_behavior_interrupts_like_interrupt` |
| 6 | queue earlier than interrupt | JS `user-text-interrupt-after-queue`; runtime `Interrupt_after_queued_user_keeps_queue_earlier_in_history` |
| 7 | after explicit cancel | `Queued_users_start_after_explicit_cancel` |
| 8 | healthy failure advances suffix | `Healthy_response_failure_still_advances_the_pending_suffix_once` |
| 9 | initiative not ahead of pending | `Pending_suffix_blocks_long_silence_inactivity_pause_and_environment` |
| 10 | no auto-pause with pending | same |
| 11 | queue + attachments | `QueuedAttachmentRuntimeTests` (queued attachment-only, text+attachment during R1, two distinct files, SQLite restart) |
| 12 | stale cancel | JS `cancel-response-stale`; runtime `CancelResponse_is_idempotent_stale_and_unknown` |
| 13–16 | nextWaitMs / clamp / null / deactivate | InitiativePlan/Initiative tests (P0-A) |

Focused Application filter including those types (`UserTextQueueTests|QueuedAttachmentRuntimeTests|InitiativePlanTests|InitiativeTests|SyntheticTextSliceTests`): **66 passed**.

### 17.3 API/realtime

Loopback Kestrel + `tests/realtime-js/client.mjs` (`SignalRMessagePackTests`): `user-text-queue`, `user-text-interrupt-live`, `user-text-omit-interrupt-live`, `user-text-interrupt-after-queue`, `user-text-unknown-behavior`, `user-text-behavior-retry`, `cancel-response-active`, `cancel-response-idempotent`, `cancel-response-stale`, `cancel-response-unknown`. Application `UserTextQueueTests` remain runtime evidence only. Persist-before-ACK: SqliteHostRecovery. Pause/reopen: lifecycle tests. `SignalRMessagePackTests` **33 passed**.

### 17.4 SQLite

`Recover_keeps_trailing_queued_users_after_interrupting_streaming` **passed**. `QueuedAttachmentRuntimeTests.Sqlite_restart_after_persisted_queued_attachment_supplies_bindings` **passed**. Per-file `SqliteConnection.ClearPool` replaces `ClearAllPools` so parallel suites do not drop other databases.

### 17.5 Web unit

Vitest **174 passed** (`pnpm run test --run`). Pre-existing NaN-height / act stderr. Send/Stop/queue: `realtime.race.test.ts`, `Composer.test.tsx`, pause copy `ChatApp.test.tsx`. `tsc --noEmit` via `pnpm run build`. Build succeeded (chunk-size warning only).

### 17.6 Playwright (`CI=1`)

| Flow | Expected | Observed |
| --- | --- | --- |
| Queue U2/U3 | R1 not interrupted by Send; one next reply | 2 assistant messages after Stop; users Alpha/Beta visible |
| Stop + queue | R1 stop, U2 answered | `queued send and Stop keep history` passed |
| Reload | no dup, history remains | `session path survives refresh` passed |
| Pause/Resume | Paused + Resume | deactivate POST + `connection` = Paused; Resume passed |
| Voice barge-in | capture continues | `voice-interrupt.spec.ts` passed |
| Queue attachment while R1 live | chip bound to queued user send | `queued attachment stays bound to the live send` passed |

Full suite **16 passed** after adding the queued-attachment case.

## Section 15 telemetry

`UserTextQueueTelemetryTests`: `behavior` only `queue|interrupt`; `pending_batch_size` values 1..8 with no tags (99 clamped to 8); cancel counters have no user text/IDs. Docs: `docs/17-observability-and-operations.md`.

## Minimum commands

| Command | Expected | Observed |
| --- | --- | --- |
| `dotnet test AgentCore.sln --nologo` | all pass | **pass** Domain 22, Application 268, Infrastructure 78 + 9 skip, Api 76 |
| Application csproj | pass | **268 passed** (standalone) |
| Domain / Infrastructure / Api sequential | pass | 22 / 78+9 skip / 76 |
| `cd web && pnpm run test --run` | pass | 174 passed |
| `pnpm exec tsc --noEmit` | pass | via build |
| `pnpm run build` | pass | pass |
| `CI=1 pnpm exec playwright test` | pass | 16 passed |

## Deviations / not claimed

- Combined `dotnet test AgentCore.sln` previously failed `TwentyTurnDemoTests` when the interrupt assertion sampled after releasing the model gate. The demo now waits for `ResponseCompletedOutput` with `InterruptReason == newText` while the scripted model is still gated, then the named sln command passes. Not a section-17 case.
- Playwright MCP not exercised.
- Hosted/headset/OpenAI/OpenRouter live and Docker sandbox: skipped/opt-in only.
- No push.

## Isolation

Playwright `CI=1` starts disposable backend/Vite. SQLite tests use unique temp files. No unknown/Real host. No catalog wipe of a user instance.
