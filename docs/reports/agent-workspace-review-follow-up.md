# Agent Workspace review follow-up

Local verification on 2026-10-07, based on `main` at `5a1d50c3`. This follow-up addresses the three findings in the supplied review of `a5a9a79d`.

## Corrected behavior

- SQLite commits instance removal, home metadata removal and the `InstanceDeleted` receipt before physical purge. InMemory appends the receipt successfully before clearing workspace metadata/bytes. Failed logical deletion preserves the archived owner and exact workspace content.
- Committed receipts also provide cleanup recovery evidence. An exact internal command retry purges idempotently without another receipt. The host sweeps receipts at startup and every five minutes, skips existing owners and retries failures. Cleanup dependencies resolve inside supervision so a failure cannot prevent host startup. The HTTP endpoint still generates its own operation id; a post-commit cleanup error can return 500 with the owner already deleted and cleanup pending.
- `/working` to `/home` copies carry the trusted copying Session id separately from the pure byte/tree transfer. Files, directory entries and new parents retain provenance, including CAS replacements and SQLite reopen.
- The canonical persistence/architecture/operations documents now require one authoritative writer per database/blob persistence root. Process-local file/tree guards are not a multi-host serialization boundary.

## Executed scenarios

The temporary SQLite and InMemory regressions injected failure at durable save or event append. Expected and observed: archived owner, original metadata/revision and exact binary bytes survived, with no deletion receipt. A subsequent valid deletion removed metadata and bytes.

A physical-cleanup failure was injected after logical commit. Expected and observed: the owner stayed absent and its receipt remained; exact retry and recovery sweep both removed leftovers without another receipt. SQLite recovery used reconstructed adapters. A stale revision or mismatched target failed, and an existing owner with an old deletion receipt was never purged.

The Synthetic API journey created scratch binary content, copied a file and a complete tree into home, performed a guarded replacement, then checked Session provenance and exact bytes. Session deletion preserved home; SQLite reopen preserved bytes and directory metadata. A separate two-host journey simulated exit between logical commit and purge, awaited the restarted host's cleanup pass, and observed removed blobs plus 404 for the deleted owner's workspace.

## Checks

| Command | Observed result |
| --- | --- |
| `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --filter 'FullyQualifiedName~AgentWorkspaceDeletionTests\|FullyQualifiedName~AgentWorkspaceStoreTests' --nologo -m:1` | 16 passed |
| `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --filter 'FullyQualifiedName~AgentWorkspaceJourneyTests' --nologo -m:1` | 11 passed, including actual host startup recovery |
| `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --filter 'FullyQualifiedName~HttpDiagnosticTests\|FullyQualifiedName~SqliteHostRecoveryTests\|FullyQualifiedName~AgentWorkspaceJourneyTests' --nologo -m:1` | 22 passed after the startup-resolution correction |
| `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --nologo -m:1` | 781 passed; 9 opt-in checks skipped |
| `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo -m:1 --logger 'trx;LogFileName=workspace-review-api.trx' --results-directory /tmp/agent-core-workspace-review-results` | 388 passed; 2 opt-in checks skipped |
| `dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-restore --filter 'FullyQualifiedName~Workspace\|FullyQualifiedName~AdminInstance\|FullyQualifiedName~AdminLifecycle' --nologo -m:1` | 71 passed |
| Changed-document local Markdown file links and new persistence anchors; `git diff --check` | Passed |

The first broad API run exposed eager cleanup dependency construction against decorated test persistence stores. It failed 32 tests; supervised resolution corrected that regression, and the complete rerun passed. The initial sandboxed test attempt could not create MSBuild sockets; local execution with approved permissions completed the checks above.

All executed scenarios were key-free. Frontend code was unchanged, so verification used integrated backend/API journeys. Optional Real-provider checks and hosted CI were not run. This is local fix verification; historical milestone freeze receipts retain their original scope.
