# Capability authorization and projection follow-up review

Reviewed the accepted enhancement on `a561cd0e`. Scope remains the bounded capability follow-on; no milestone is reopened and P10/P11 remain unopened. Two defects were reproduced, corrected and covered by regressions.

## Findings and corrections

1. **Embedded exact capability names did not match.** `please use email.search` returned no match, and `workspace please use workspace.write` could load `workspace.read` through a family tie instead. The Session runtime integration with `Please use WORKSPACE.WRITE.` finished with a missing-schema response instead of writing the file. [CapabilityDiscoveryMatcher](../../src/AgentCore.Application/Tools/CapabilityDiscoveryMatcher.cs) now scores exact name tokens before category/tag/name/summary matches and trims surrounding sentence periods. Matching remains case-insensitive and deterministic, and authority/configuration/discoverability filters run first. Regression coverage includes several named interfaces, ordinal ties, limit-one ranking, already-projected results and an unauthorized name in the query.
2. **In-memory load admission ignored cancellation tokens.** A canceled `AdmitCapabilitiesAsync` call mutated IDs/count/revision while SQLite rejected it. [InMemoryConversationTurnExecutionStore](../../src/AgentCore.Infrastructure/Persistence/InMemoryConversationTurnExecutionStore.cs) now checks the admission token inside its mutation lock before reading or writing execution state. A temporary SQLite / in-memory parity regression checks cancellation leaves all three fields unchanged and a subsequent valid admission still succeeds.

The first reproduction had four failing Application cases and one failing in-memory store case; the SQLite cancellation case already passed. All focused regressions pass after correction. No provider, wire, approval, authority, published Definition or frontend contract changed. Canonical backend matching and persistence cancellation descriptions were updated.

## Executed checks

Commands ran from the repository root with installed dependencies and key-free Synthetic adapters.

| Command / gate | Observed result |
| --- | --- |
| `dotnet test tests/AgentCore.Application.Tests --no-restore --nologo -m:1 -p:UseSharedCompilation=false --filter FullyQualifiedName~CapabilityProjectionTests` | 23 passed. Includes a real Session mailbox/tool loop, persisted load state, exact UTF-8/CRLF write and new-turn reset for both bare-name and prose queries. |
| `dotnet test tests/AgentCore.Infrastructure.Tests --no-restore --nologo -m:1 -p:UseSharedCompilation=false --filter FullyQualifiedName~SkillPinStoreTests` | 3 passed, including both cancellation-token parity cases and SQLite reopen/reclaim. |
| `dotnet test tests/AgentCore.Application.Tests --no-restore --nologo -m:1 -p:UseSharedCompilation=false` | 1,306 passed / 1 hosted-provider opt-in skip. Includes live occurrence connection/isolation, durable WorkItem recovery, cancellation and supersession. |
| Infrastructure filter `FullyQualifiedName~SkillPinStoreTests\|FullyQualifiedName~AdminLifecycleConcurrencyTests\|FullyQualifiedName~AdminLifecycleDeletionTests` with the same test flags | 29 passed. Checks execution-store changes and lifecycle concurrency/deletion. |
| `dotnet build src/AgentCore.Api --no-restore --nologo -m:1 -p:UseSharedCompilation=false` | Passed with no warnings/errors. |
| Node 22 Playwright CLI, `test e2e/capability-projection.spec.ts --project=synthetic` against API 5086 / web 5176 | 1 passed. Selected unauthorized-always exclusion, All snapshot publication, wide/narrow layout, Chat load/write and next-turn reset. |
| Playwright MCP against the same disposable host | Saved/reloaded an authorized always selection: 54 authorized / 3 always; viewport and document both 390px. A new managed Chat sent inspect → write → inspect through the composer; write schema was initially absent, loaded for continuation, then absent on the next turn. Download matched all 31 UTF-8 bytes of `Loaded exact capability café\r\n`. Console had zero errors and recorded flow HTTP requests succeeded. |
| Documentation and whitespace | 13 files / 375 local links / 125 anchors / 15 JSON examples checked with no findings; `git diff --check` passed. |

Both disposable hosts were stopped. Storage was isolated under `/tmp/capability-projection-review`; user sessions, published repository Definitions and the running nopCommerce project were untouched. Frontend source was unchanged, so the local unit/build gate was not repeated; browser execution was repeated and the push triggers the complete hosted workflow. No live-provider probe was enabled.

## Hosted receipt

Corrective behavior commit: `455110d91548cc82f85e6902e502a0e7575925b2`. [Hosted Synthetic run 37523229767](https://github.com/trannamtrung1st/agent-core/actions/runs/37523229767) completed successfully on that exact commit. All five jobs passed: backend, Compose smoke, dedicated acceptance (17 tests), core Playwright (102 tests), and frontend (94 files / 705 tests plus TypeScript and production build). Backend passed Domain 145, Infrastructure 767, Application 1,306 and API 387, with the existing 18 opt-in/runtime-availability skips. The original enhancement acceptance remains recorded in [the original verification report](capability-authorization-projection-verification.md).

The review corrections are complete with no remaining required gate. This receipt is a documentation-only follow-up; no behavior changed after `455110d9`. Optional live-provider checks remain unrun by design, and historical milestone freezes remain unchanged.
