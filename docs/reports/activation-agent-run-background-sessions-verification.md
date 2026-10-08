# Activation, AgentRun and background Sessions verification

## Status and reviewed source

**In progress — foundation only; the complete proposal is not implemented or accepted.**

Reviewed baseline: `bd44046896df6f3e0fc2e7d15d60dd479a5349cd`, the proposal's reviewed main, with a clean starting working tree on 2026-10-08 (Asia/Ho_Chi_Minh). The development foundation is derived from that baseline. There is no final migration behavior SHA or exact-SHA hosted result. Baseline CI is historical evidence and does not verify these changes.

Phase A domain proof, the initial Phase B storage foundation, a batched admission factory and the common claim/recovery coordinator are implemented. The new execution store is not registered as the production execution owner and has no production dispatcher. Existing WorkItem and ConversationTurnExecution engines, APIs and UI still operate. No adapter, alias, dual writing or old-execution conversion was introduced. The additive foundation migration is an intermediate step, not the proposal's required final destructive schema cutover.

## Implementation mapping

| Concern | Current artifact | Evidence and limit |
| --- | --- | --- |
| Immutable effective-turn admission | `src/AgentCore.Domain/Conversation/Activation.cs` | Frozen ordered source IDs, bounded inputs, source fingerprint, dedupe identity and provenance validation |
| Shared lifecycle | `AgentRun.cs`, `AgentRunModels.cs`, `AgentRunAdmission.cs`, `AgentRunActionHash.cs`, `AgentRunKnownEffects.cs` in the same directory | Stronger retry/claim/approval/effect transitions ported into an independent target model; no old execution object dependency |
| Execution configuration | AgentRun admission/projection state | Frozen model, Definition/persona and effective Skill catalog; bounded Skill/capability load counters survive copies and recovery |
| Origin and presentation | `SessionOrigin.cs`, `ConversationModels.cs` | Immutable origin, separate surface flags, initial-run-only report eligibility; task completion leaves Session lifecycle separate |
| Shared persisted transitions | `src/AgentCore.Application/Execution/AgentRunCommand.cs` | Store CAS, monotonic UTC time and expired-lease checks precede domain transitions |
| Batch construction and dispatch coordination | `AgentRunAdmissionFactory.cs`, `AgentRunCoordinator.cs`, `Ports/AgentRunDispatcher.cs` | Stable input-batch dedupe and single response ownership; common direct/scheduled CAS claim and safe recovery. Dispatcher is a port, not a production runtime implementation |
| Atomic admission port | `src/AgentCore.Application/Ports/AgentRunStore.cs` | One operation for snapshot/input/Activation/run; no standalone Activation insert |
| Storage | `InMemoryAgentRunStore.cs`, `SqliteAgentRunStore.cs`, `AgentRunStoreMapping.cs`, `AgentRunRecords.cs` under Infrastructure Persistence | One memory gate/shared state or one SQLite transaction with the existing Session mapper; owner-scoped reads and commands |
| Relational proof | `20261008013446_ActivationAgentRunFoundation` | Unique Session/dedupe, owner/background receipt, accepted source entry and run/Activation identity; required foreign keys and revision fencing |
| Current-schema reopen | `SqliteMemoryStore.StampAgentRunFoundationAsync` | Stamps only a complete current-model EnsureCreated schema after column/index/foreign-key checks; rejects incomplete shapes without repair/conversion |
| Historical fixture seeding | `MigrationSessionSeed.cs`, `UnifiedWorkspaceMigrationTests.cs` | Copies only columns present in the target historical schema; uses actual SessionSnapshots/ConversationEntries table names; no production legacy writer |

Phase B still needs occurrence acceptance receipts, atomic occurrence routing and the destructive current-schema/store cutover. Phases C–G remain incomplete: production runtime/coordinator adoption; background.start; Automation child execution; safe completion receipts and parent activation; same-Session UI/API foregrounding; legacy deletion; Impeccable; complete canonical synchronization and hosted closure.

## Acceptance scope

| Criteria | Status |
| --- | --- |
| AC01 | Domain/storage batch admission and replay tested; production live admission not migrated |
| AC02 | Atomic Session/input/Activation/run and immediate source-receipt dedupe tested; Automation occurrence transaction not implemented |
| AC03–AC04 | Domain/store identity, attempts, concurrent claim, revision/generation and expired-lease fencing tested; unified production dispatch/retry not implemented |
| AC05–AC06 | Exact action decisions, approval same-attempt resume, bounded checkpoint/effect rules and uncertain-effect reopen tested at domain/store boundaries; attached/detached runtime integration pending |
| AC07 | Frozen configuration and load state covered by the domain; current execution authorization and capability background policy remain application cutover work |
| AC08 | Origin/surface persistence, owner isolation, fresh SQLite/current-model reopen and historical migration regressions exercised; final reset/Compose cutover pending |
| AC09–AC18 | New production/browser journeys remain unverified and their missing implementation is tracked below |
| AC19 | No web/composer or cross-Session context-reference changes introduced |
| AC20 | Not met: old production engines/schemas/routes remain; full migration and exact-SHA hosted gates are pending |

No required J1–J12 production migration journey is declared passed. Store scenarios below establish storage behavior, not an end-to-end migration.

## Executed integrated scenarios

The following run against both InMemory and fresh temporary SQLite through `AgentRunAdmissionStoreTests`:

- Admit “Check A” and “Check B” together; replay concurrently. Expected one Activation/run and both persisted inputs. Observed one run, ordered source IDs, original response ownership, and changed-content conflict.
- Admit an immediate child from a claimed owned parent; replay the same receipt with fresh candidate Session/run/input IDs. Expected original committed child and no orphan candidate Sessions. Observed original run returned, candidates absent and changed objective rejected.
- Collide on run identity after staging another Session. Expected atomic rollback. Observed no new Session or Activation and the original graph intact.
- Submit a new admission with stale Session revision or already-admitted source entries under another key. Expected conflict without advancing history. Observed unchanged stored revision and one input owner.
- Read/claim with a foreign instance or profile. Expected absent reads and denied commands. Observed null/empty reads and NotFound commands. Missing, foreign and cancelled parent admission produced no child.
- Race two claims, then attempt an effect at lease expiry. Expected one winner; expired/stale worker denied. Observed one claim, same-run recovery, attempt increment and preserved Activation/response identity.
- Complete before saving the assistant response. Expected conflict. Save the matching response, complete and reopen. Observed a durable outcome-entry link and original Session response/history on reopen.
- Complete a child with NoAction, add ChatList surface, reopen and admit “Check B too.” Expected no fabricated reply, same active Session and a new Activation/run. Observed one original task entry, preserved origin, both surfaces and a second run in the same Session. This exercises persistence, not the Continue in chat UI.
- Persist approval; reject an altered action hash; approve the exact action and resume. Expected same attempt. Persist an InFlight external action, reopen and expire its claim. Observed Indeterminate/Failed with no runnable replay.
- Attempt to rewrite background origin as UserChat. Expected conflict. Observed immutable stored origin.
- Admit and reopen with submillisecond UTC time. Expected indexed-millisecond creation and exact embedded admission time to remain valid. Observed successful restore with unchanged run identity and source time.
- Reopen a complete current-model EnsureCreated database. Expected migration stamp and successful admission. Remove run/Activation uniqueness from another schema. Expected no false stamp. Observed explicit incomplete-schema rejection and no foundation migration receipt.

Eight `AgentRunCoordinatorTests` exercise accepted-batch admission/replay and invalid input rejection, direct/scheduled claim ownership, same-ID safe lease recovery with stale-worker rejection, uncertain effect terminalization without dispatch, approval expiry resuming the same attempt, missing-Session isolation and transport errors retaining a claim until lease recovery. They use the actual InMemory stores and a capturing dispatcher; they do not exercise a production model/mailbox path.

Domain tests additionally exercise cancellation/known-effect warnings, reject/expire/cancel approval paths, uncertain browser observation recovery, bounded retry exhaustion, terminal immutability, deep catalog freezing, load-state recovery and stale generation rejection on idempotent effect acknowledgements.

## Commands and results

Local evidence directory: `local/verification/activation-agent-run-foundation/` (ignored test artifacts).

| Command | Result |
| --- | --- |
| `dotnet build src/AgentCore.Infrastructure --no-restore --disable-build-servers` | Passed, zero warnings/errors |
| `dotnet test tests/AgentCore.Domain.Tests --no-restore --disable-build-servers` | 178 passed, zero failures/skips |
| `dotnet test tests/AgentCore.Infrastructure.Tests --no-restore --disable-build-servers --filter FullyQualifiedName~AgentRunAdmissionStoreTests` | 28 passed including schema-stamp and submillisecond regressions; `foundation-admission-final.trx` |
| `dotnet test tests/AgentCore.Infrastructure.Tests --no-restore --disable-build-servers --filter 'FullyQualifiedName~AgentRunAdmissionStoreTests\|FullyQualifiedName~PinnedPersonaRevisionMigrationTests\|FullyQualifiedName~DefinitionLifecycleMigrationTests\|FullyQualifiedName~UnifiedWorkspaceMigrationTests\|FullyQualifiedName~Sqlite_migrate_reopens_legacy_ensurecreated_database'` | 33 passed after all six migration failures were repaired; `foundation-migration-repair.trx` |
| `dotnet test tests/AgentCore.Application.Tests --no-restore --disable-build-servers --filter FullyQualifiedName~AgentRunCoordinatorTests` | 8 passed; `foundation-coordinator.trx` |
| Full Infrastructure first pass | 844 passed, 6 failed, 9 opt-in skips; retained in `foundation-infrastructure.trx` |
| Full Application first pass | 1306 passed, 1 opt-in skip; `foundation-application.trx` |
| Full API first pass | 371 passed, 3 opt-in skips; `foundation-api.trx` |
| Full Infrastructure after repair | 852 passed, 9 opt-in skips before the additional submillisecond parity cases; `foundation-infrastructure-final.trx` |
| Full Application after repair | 1306 passed, 1 opt-in skip before coordinator tests; `foundation-application-final.trx` |
| Full API after repair | 371 passed, 3 opt-in skips before coordinator changes; `foundation-api-final.trx` |
| `dotnet test AgentCore.sln --no-restore --disable-build-servers --blame-hang --blame-hang-timeout 5m` on current source | Domain 178 passed; Application 1314 passed / 1 opt-in skip; order-event plugin 4 passed; API 371 passed / 3 opt-in skips; Infrastructure 854 passed / 9 opt-in skips. Total 2721 passed, 13 opt-in skips, zero failures; no hang sequence generated |

The final current-source solution command uses a shared `foundation-current.trx` name; VSTest overwrites it as projects finish. Per-project console summaries establish the current-source counts, while earlier per-project TRX files and focused admission/coordinator files remain separately retained. This is local foundation evidence, not exact-SHA hosted closure.

Full commands include `--logger 'trx;LogFileName=…' --results-directory local/verification/activation-agent-run-foundation`. Infrastructure final uses `--no-build --no-restore`; Application/API use `--no-restore --disable-build-servers`. Application includes `--blame-hang --blame-hang-timeout 5m`.

Failed gates and corrections are retained:

1. The sandboxed test runner could not bind its local socket. Authorized local execution succeeded. A sandboxed Infrastructure build stalled; its replacement passed and only that task-owned stalled process was stopped.
2. An admission fingerprint anonymous projection duplicated the property name Kind; corrected to OriginKind before successful build.
3. The first Infrastructure test compile violated xUnit2031; changed to the predicate Assert.Single overload.
4. Four focused assertions expected Validation rather than the repository's ValidationError code; corrected, then all focused cases passed.
5. Full Infrastructure found six schema regressions: historical seed helpers copied new columns into old schemas, three legacy-owner tests used the current writer against an older schema, and current-model EnsureCreated reopen attempted duplicate OriginJson addition. Corrected test-only historical seeding and added strict current-schema stamping plus positive/negative regressions. The affected repair suite passed.

The new coordinator initially failed compilation because its owner check assumed the inverse SessionSnapshot nullability; corrected to required Instance/optional Profile. Nonblocking awaited task assertions repaired xUnit1031. SQLite timestamp restoration initially compared exact admission ticks to truncated indexed milliseconds; aligned that temporal guard to persisted precision and added both-store regression coverage.

Hosted-provider skips were intentional. No Real model/browser checks were run.

## Remaining cutover work and verification

- Finish the atomic occurrence receipt and one-child-per-occurrence mapping; complete the explicit destructive execution reset/schema migration.
- Adopt AgentRun in production SessionRuntime/SessionHost with one registered runtime, coordinator, hosted loop and shared live/detached tool checkpoint/approval/effect lifecycle. Preserve batching, initiative/native events, voice/supersession and fast ACK behavior.
- Implement current-policy-authorized background.start, bounded fan-out, own scratch/artifacts, quiet outcomes and independent child dispatch.
- Implement trusted bounded completion receipts, initial-child-only parent activation, safe arbitration and no authority escalation/recursion.
- Implement owner-scoped Background Session and AgentRun APIs, pagination/control, same-Session foregrounding, Chat rail and Admin source navigation.
- Delete old WorkItem/ConversationTurnExecution production domain, store, runner, hosted, API and web contracts; retain only immutable historical migrations/reports.
- Run all new J1–J12 Synthetic journeys, existing browser/voice/initiative/approval/Automation/Skill/workspace/artifact regressions, order-event plugin, frontend tests/build and SQLite/Compose crash/restart/ownership checks.
- Complete the bounded Impeccable batch at 1440×900, 768×900 and 390×844 with at most one coherent correction and final visual confirmation. No UI pass, screenshot batch or design-context change has been performed in this foundation.
- Finish canonical docs 07/09/11/12/13/14/17 and actual UI design-context synchronization after implementation. Current foundation docs accurately preserve the running baseline rather than claiming it already uses the new model.
- Commit/push the final complete behavior candidate and verify every required hosted Synthetic/Compose job on its exact SHA. No closure/freeze may be claimed until then.

Added documentation links and fragments (14) and balanced fences passed local checks, and `git diff --check` passed. No complete runnable JSON example was added or modified by this foundation.

A current production symbol scan still finds the previous execution owners and routes; that is an explicit unmet deletion gate. No final production compatibility-surface removal or migration acceptance is claimed.
