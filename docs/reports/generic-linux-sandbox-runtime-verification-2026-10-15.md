# Generic Linux sandbox runtime — October 15, 2026

Baseline: `main` at `82d136682ee0cb8d2767022997bfa41ad7476719`. This owner-authorized successor replaces language-specific build-server shutdown with generic generation retirement. It preserves the existing trusted, single-user local scope and does not renew milestone or hosted acceptance. The user's application and historical Sessions were not restarted/reset.

## Provider findings and decision

The installed SDK is `Alibaba.OpenSandbox` 1.1.1; its NuGet repository metadata points to `b7d7697750b45be2ccc876da645c8880a0cf69fc`. Public XML API documentation in that package exposes command run/status/interrupt, SandboxManager deletion and persistent shell-session APIs. None demonstrates a reusable execution scope that reaps every escaped descendant after the main command exits. The runtime pin is `531700ca18a7fa5fab6a2f25d6dc68a76eddab12`; exact server/execd/egress digests remain in [images.json](../../scripts/opensandbox-proof/images.json).

Pinned implementation inspected locally before the adapter changes:

- [Command execution](https://github.com/opensandbox-group/OpenSandbox/blob/531700ca18a7fa5fab6a2f25d6dc68a76eddab12/components/execd/pkg/runtime/command.go#L246) starts process groups, waits for the main process and records its completion. Cancellation avoids signaling a reaped main process because numeric process/group identifiers can be reused.
- [Interrupt](https://github.com/opensandbox-group/OpenSandbox/blob/531700ca18a7fa5fab6a2f25d6dc68a76eddab12/components/execd/pkg/runtime/interrupt.go#L32) refuses finished command sessions and signals a running group with TERM/KILL. This is not a post-exit escaped-descendant guarantee. An explicitly interrupted process may report Go's `ExitCode() == -1`; Core retains that provider evidence as Canceled after `running=false`, without fabricating a normal shell exit code. Unrequested negative/null completion remains fail-closed.
- [Persistent bash sessions](https://github.com/opensandbox-group/OpenSandbox/blob/531700ca18a7fa5fab6a2f25d6dc68a76eddab12/components/execd/pkg/runtime/bash_session.go) close shell resources but do not establish full escaped-descendant scope. No alternate execution mode was adopted.
- [Docker deletion](https://github.com/opensandbox-group/OpenSandbox/blob/531700ca18a7fa5fab6a2f25d6dc68a76eddab12/server/opensandbox_server/services/docker/docker_service.py#L1111) resolves the exact sandbox container, kills it and synchronously removes it with force before successful completion; failures are errors. Success or authoritative absence (404) for Core's stored owned provider identity is the retirement boundary.

Decision: retain ordinary quiescent containers; retire the entire owned generation when generic bounded process inspection finds residual identities. Core never identifies an SDK/language, invokes a managed build-server shutdown or signals post-exit numeric PIDs/groups. No Docker socket was exposed to workloads; no daemon policy, supervisor, upstream fork or orchestrator was added. Compatible images still need `/bin/sh`, Linux `/proc`, shell utilities used by readiness probes and OpenSandbox bootstrap compatibility. Python, .NET, Node and Java are optional image contents.

## Durable ordering and ownership

[Finalization](../../src/AgentCore.Infrastructure/Sandbox/OpenSandboxService.Finalization.cs), [generic quiescence](../../src/AgentCore.Infrastructure/Sandbox/OpenSandboxService.Quiescence.cs), [background processing](../../src/AgentCore.Infrastructure/Sandbox/OpenSandboxService.Processes.cs) and [recovery](../../src/AgentCore.Infrastructure/Sandbox/OpenSandboxService.Recovery.cs) use the existing Session execution gate, generation/receipt records and Workspace transaction owner.

1. Persist main-command completion and actual exit at `processInspection` before inspection/import.
2. Perform up to three PID/start-time scans with short grace inside a three-second bound. Reuse quiescent generations. Persist `retirementRequested` before owned-container deletion for residuals or inspection deadline.
3. Keep staging until authoritative remote absence; persist `remoteTerminationConfirmed`, `generationRecycled` and `retired` evidence. A lost acknowledgment leaves a known-completed receipt, no import and a blocked generation; later DELETE/404 is idempotent.
4. Strictly collect retained staging, revalidate current authority and the original canonical fingerprint, and import through the existing atomic Workspace CAS/journal using the execution ID. Nonzero exits remain Completed and can commit valid files.
5. Persist the commit outcome and `stagingCleanup`; delete staging descriptor-relatively only after pending import/journal resolution. `terminationConfirmed` includes final staging deletion, while `remoteTerminationConfirmed` alone does not. Unknown file effects retain staging and NeedsResync.

Crash recovery can resume validated import only with known command completion and current authority. Once a commit entered the journal, it resolves the original transaction rather than reimporting an old tree. It never replays shell or initialization. Run cancellation, Session end, source/profile revocation and explicit reset do not permit a pending import. Reset's durable lease discard intent also fences recovery if the receipt write was interrupted. An unresolved latest receipt keeps staging and NeedsResync even if the active execution pointer was lost; explicit reset cannot erase that uncertainty. Background completion checks current Instance allowed-profile narrowing before publication, matching foreground authority.

The originating dispatched tool call remains an observed effect even if finalization returns recovery_required. A new blocked call that only reports a previous receipt creates no effect; accounting matches `(Session, Run, tool call)` to the execution receipt. A null JSON error is not a definitive rejection. Original-call command/file uncertainty remains Indeterminate even when recovery_required is also set; a new blocked call does not inherit that older uncertainty.

Canonical `/working` and Session-selected profile survive internal recycling. The next independent authorized command creates a fresh selected-profile generation and hydrates current canonical files. Container-only processes, packages and caches are lost. Setup must leave its original generation quiescent; uncertain setup is not replayed. New generations run their newly pinned setup once through the existing initialization lifecycle.

## Deterministic and integration evidence

Tests are in [OpenSandboxCleanupTests](../../tests/AgentCore.Infrastructure.Tests/OpenSandboxCleanupTests.cs), [SandboxMountFilesTests](../../tests/AgentCore.Infrastructure.Tests/SandboxMountFilesTests.cs), [OpenSandboxIntegrationTests](../../tests/AgentCore.Infrastructure.Tests/OpenSandboxIntegrationTests.cs) and [SandboxFailureSummaryTests](../../tests/AgentCore.Application.Tests/SandboxFailureSummaryTests.cs). The previous language-recognition test file was removed.

| Check | Observed result |
| --- | --- |
| Application: `SandboxFailureSummaryTests`, `ToolRegistryPolicyTests`, `AgentRunCoordinatorTests`, `BrowserLifecycleTests` | 39 passed; null-error rejection, original/blocked effect uncertainty and Run/browser recovery covered |
| Infrastructure: `OpenSandboxCleanupTests`, `SandboxMountFilesTests`, `OpenSandboxReadinessTests`, `SandboxStoreTests` | 44 passed including the narrowing case; final `OpenSandboxCleanupTests` recheck: 22 passed including reset without an active pointer |
| Infrastructure: `OciSandboxAuthorityTests`, `SandboxImageReadinessTests`, `OpenSandboxOptionsTests` | 62 passed |
| Focused API Sandbox routes | 16 passed |
| Final focused real OpenSandbox set | 26 passed, no skips, 6 m 24 s; final duplicate/background effect recheck 2/2 passed |
| Linux ARM64 strict collector | 16 passed using current test assembly in the installed stock SDK image |
| Emulated Linux AMD64 strict collector | 16 passed using current test assembly and the installed immutable AMD64 SDK image |
| Drawer/service frontend tests | 37 passed after final cleanup/reset presentation review |
| Backend API build | Passed, zero warnings/errors |
| Frontend TypeScript/production build | Passed; existing large-chunk advisory remains |

Local command logs and inspected captures are retained in `local/verification/generic-linux-runtime` (ignored verification artifacts, not source-controlled credentials). Only the recorded disposable API/Vite process groups on 5096/5196 were stopped after verification.

The deterministic pending-retirement theory first returns provider 403: staging remains, canonical files do not change and execution/switching stay fenced. After provider 204, valid live authority imports exact `retained.txt` and cleans staging. Run cancellation, explicit reset, reset-intent crash and narrowed background authority instead retain the original exit, discard unpublished staging after safe termination and do not import it. Unknown commit evidence retains staging and cannot become definitely uncommitted. Existing filesystem tests exercise symlinks, directory links, hard links, FIFO, protected paths, quotas and concurrent replacement without following links.

Real SDK checks use opt-in `AGENTCORE_OPENSANDBOX_TESTS=1`, private server authentication loaded from the existing local private file without printing it, and installed immutable image IDs. No hosted inference is used. Each integration fixture owns a disposable SQLite store, Session, generation and temporary canonical Workspace. Filtered `dotnet test tests/AgentCore.Infrastructure.Tests --filter '<named scenarios>' --nologo -v minimal -m:1 /nodeReuse:false /p:UseSharedCompilation=false` runs the selected cases. The host runner's compiler flags are not passed into workload commands.

| Real scenario | Expected and observed behavior |
| --- | --- |
| Ordinary shell writes | Same generation reused; exact canonical bytes retained |
| Generic persistent child | Known main exit preserved; only owned generation retired; exact files committed |
| Detached TERM-resistant child | `setsid` child ignoring TERM removed by whole-container retirement; Completed exit 7, committed bytes and both termination proofs retained |
| Available Python and Node | Detached Python subprocess / Node unref child treated generically; exact language fixture files retained |
| Concurrent Sessions | Sibling remained Ready on its original generation and retained its own exact bytes while the other Session recycled |
| Ordinary stock .NET | Plain `dotnet new`, `dotnet build` and `dotnet run` succeeded and committed each command; no workload server-suppression flags or SDK shutdown calls |
| Selected alternate and switches | Explicit alternate survives recycling; omitted selection reuses it on a fresh generation; switch back preserves exact files |
| Timeout defaults and blocked tools | Explicit/retained selected profile supplies its ceiling; explicit allowance clamps; a newly blocked call reports the older execution without claiming its effect |
| Background/status/stop and cancellation | Status remains observational; confirmed completion/authorized stop finalizes safely; Run/request cancellation fences unpublished files; committed post-swap effects stay committed |
| Unsafe tree / quotas / CAS | Unsafe entries and live excessive writes do not partially import; canonical conflicts retain existing files; unknown effects remain unknown |
| Reopen and duplicate calls | Durable receipt observed without duplicate dispatch; altered command under the same operation conflicts |

Eight injected crash checkpoints are exercised by `Generic_retirement_crash_boundaries_reconcile_files_once_without_shell_replay`: command completion before inspection; retirement intent before acknowledgment; provider acknowledgment before proof persistence; persisted retirement before collection; collection before commit; canonical swap before terminal receipt; staging cleanup intent; staging deletion before final receipt. On reopen, `once.txt` contains exactly one `x`. Post-swap checkpoints additionally preserve a newer canonical file rather than overwriting it from stale staging. Separate lost-terminal-receipt and before/after-swap cancellation cases verify the existing journal outcome.

Initial broad runs exposed receipt-envelope inconsistencies, an explicitly stopped process's upstream -1 status, and a fixture's assumption of a case-sensitive Docker Desktop bind mount. These were corrected and the affected cases rechecked. The case-collision fixture now probes whether names can be distinct; macOS aliasing uses the existing protected-file fallback, while the native Linux collector run exercises actual collisions. A blocked-call fixture also now pins the required execution profile explicitly and runs separately from one/four-second timeout cases. Both final cases passed in the focused recheck. No filesystem validation was weakened.

## Browser and Impeccable review

Playwright MCP used only the isolated Synthetic API/Vite fixture on 5096/5196 with its disposable Session and actual pinned OpenSandbox service. It sent `synthetic-sandbox:untracked`, observed Completed / Files committed / Container recycled, opened collapsed lifecycle evidence and copied the command. It then selected the stock .NET profile, sent the omitted-profile `selected` command, switched back to General and read exact `selected-dotnet` from canonical `selected.txt`. Saved Admin policy/setup remained unchanged.

At 1440/1024/768 the drawer measured 640px; at 390/320 it measured full viewport width after Ant Design transitions. The page had no horizontal overflow. The existing Admin Instance Sandbox page also fit all five widths. Mobile controls measured 40px height; long image references/commands wrapped inside their owners. Screenshots were inspected for the narrow command-result, environment and Admin surfaces. Diagnostic Escape restored its trigger; the next Escape restored Session sandbox. An intercepted 503 retained previous profile/execution evidence, and Retry restored the live view.

Controlled response projections separately exercised pending retirement with known exit 7, committed files with pending staging cleanup, unknown commit with remote-only termination, and explicit reset of a previously recycled generation. Pending/unknown cases keep expanded evidence and safe recovery text; unknown does not claim notCommitted. Reset does not claim the selected profile was retained. These are UI fault projections, not claims that provider faults were induced through the browser. Actual termination-failure behavior is exercised by the loopback integration fixture above.

[DESIGN.md](../../.agents/context/DESIGN.md), [design metadata](../../.impeccable/design.json), [Chat brief](../../.impeccable/surfaces/web-src-features-chat-chatapp-tsx.md), [Admin brief](../../.impeccable/surfaces/web-src-features-admin-adminapp-tsx.md), README and canonical architecture/ports/decision/backend/UI/protocol/storage/testing/operations/implementation-plan owners are synchronized. The design hook found no deterministic issues; rendered interaction evidence is recorded separately above.

## Coverage limits and operations

The [small final follow-up](sandbox-final-polish-acceptance-2026-10-15.md) resolves the two subsequent inspection/status observations and supplies intentional Real deployment plus disposable acceptance. The original results and unperformed deployment statement below remain historical for this report's source.

- Real workload runtime: Linux ARM64 on Docker Desktop, hosted by macOS ARM64; Workbench Python/Node, stock .NET SDK 10.0.402 and BusyBox cases used existing images. Windows hosts and native Linux x64 host operation are not newly verified here.
- AMD64: actual strict collector code passed in an emulated Linux x64 container. A real AMD64 stock-SDK OpenSandbox workload was attempted but failed at provisioning. The pinned [Docker image platform policy](https://github.com/opensandbox-group/OpenSandbox/blob/531700ca18a7fa5fab6a2f25d6dc68a76eddab12/server/opensandbox_server/services/docker/container_ops.py#L225) defaults expected platform to the daemon and repulls an incompatible cached architecture. This ARM64 daemon therefore cannot provision that cached AMD64 immutable image under its current policy. No daemon change, retag, provider override or false AMD64 workload pass was introduced. Native AMD64 full-workload verification remains open.
- No suitable Java image was installed; optional Java compatibility was not exercised. No new language image was built just for these tests.
- `/bin/sh`, `/proc`, POSIX probe utilities and execd compatibility remain required. Setup helpers must quiesce; container-only state is intentionally lost when a generation is retired. Quotas/sampling remain safeguards rather than hard writable-layer disk limits. Own-execd loopback and production/multitenant hardening limitations are unchanged.
- No hosted CI was dispatched, rerun or waited for; no full CI suite was run locally. Manual screen-reader/device testing and native browser zoom were not renewed. Builds/tests do not deploy the new runtime into an already-running user application; an intentional owner-managed deployment/restart is still required.
