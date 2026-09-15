# Output review revision — remaining required gates

Date: 2026-09-15

Addresses remaining required members of `family-mandatory-gate-evidence`, `family-persistence-contract-completeness`, `family-realtime-command-admission`, `family-delivery-receipt-integrity`, `family-observed-measurement-evidence`, and `family-completion-snapshot-binding`. Optional `sf-020` (zero `playback.stopped` after flush) is deferred: supersession already preserves last accepted playback progress for heard context.

## Commands

| Command | Working directory | Exit status |
| --- | --- | --- |
| `dotnet test tests/AgentCore.Domain.Tests --filter FullyQualifiedName~ConversationRecordTests` | repository root | 0 — 3 passed (allowlisted profile validation) |
| `dotnet test tests/AgentCore.Application.Tests --filter FullyQualifiedName~SessionRealtimeLifecycleTests\|IdentityRuntimeTests\|PersistenceReceiptTests\|Twenty_turn` | repository root | 0 — 12 passed |
| `dotnet test tests/AgentCore.Infrastructure.Tests --filter FullyQualifiedName~MemoryStoreContractTests` | repository root | 0 — 10 passed (profile bounds, migrate reopen, existing snapshot upsert, successful Failed-status store) |
| `dotnet test tests/AgentCore.Api.Tests --filter FullyQualifiedName~JavaScript_messagepack` | repository root | 0 — 17 Kestrel JS scenarios including `older-retry` and `reconnect-retry` |
| `dotnet test tests/AgentCore.Application.Tests --filter FullyQualifiedName~Twenty_turn_synthetic_demo_records_observed_stage_latencies` | repository root | 0 — writes [m12-stage-latencies.md](m12-stage-latencies.md) |

## Coverage bound to findings

- `sf-015`: JS matrix now includes older-than-last exact retry after an intervening mute, plus reconnect `user.text` with the original eventId and one user history row.
- `sf-017`: runtime reconstruction retries the same `sourceEventId` without a second user entry; `Failed_terminal_end_save_does_not_ack_ended_status` still injects a failing Ended `SaveAsync`.
- `sf-013`/`sf-014`: migrate reopen of an existing SQLite file; both stores validate profile allowlist; restarted runtime prompt memory includes `preferredName`.
- `sf-005`/`sf-008`/`sf-009`/`sf-011`: typed `response.received`, locked 1,024-entry fingerprint dedupe, per-attachment `Admission` lock, speech sampleOffset deferral, delayed fatal abort — exercised by the expanded JS matrix.
- `sf-018`: observed count/p50/p95/max table regenerated from Stopwatch samples.
- `sf-019`: this revision commits remaining run-owned reports/tests; TDP completion (not this file) records the live HEAD to avoid a self-hash loop. `.agents` instructions were not modified.
