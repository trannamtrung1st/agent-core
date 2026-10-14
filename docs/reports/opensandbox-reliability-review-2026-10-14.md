# OpenSandbox reliability review — 2026-10-14

## Scope and source

Reviewed and enhanced the existing integration on main, initially synchronized with `origin/main` at `e267736a`. Concurrent Office changes and their subsequent main commit were preserved. This is focused local verification of the working tree, not a new milestone acceptance or hosted CI claim. No integration replacement, infrastructure redesign, user database reset or unrelated Docker cleanup occurred.

## Fixes

- **Active TTL:** renewal happens after hydration and immediately before initialization execution. It reserves the permitted command deadline plus 60 seconds for synchronization, bounded by the pinned maximum lifetime. Work that cannot fit is rejected before dispatch with reset guidance. Successful completion restarts idle expiry; background receipts retain a deadline enforced by supervision. Profile resource ceilings remain intact.
- **Commit recovery:** the existing staged complete-tree replacement now has a private, flushed journal named by the Core commit ID outside logical `/working`. Prepared journal plus stage/backup topology distinguishes interruption before and after the swap. Receipt `committing` precedes import; terminal receipt persistence precedes clearing the active lease. Recovery never reimports an older tree or replays a command. Missing historical or unreadable evidence remains `unknown`, never definite non-commit. Ownership, fingerprint CAS, quota, file/path validation and the final authority fence remain in place.
- **Profile verification:** disposable offline verification uses the configured resource limits, checks non-root Python, executes the configured initialization, and checks quiescence and bounded scratch output. Failures expose only safe phase, exit, timeout or cleanup messages. `SandboxProfileVerifications` is an additive table keyed by immutable profile ID/revision; verification no longer creates a configuration revision. Admin's existing timestamp/message fields project this separate observation.
- **Background finalization:** tool status and HTTP GET inspect durable receipts and commit evidence only. Periodic supervision and explicit stop own completion/import. Import fences Session/Instance/profile authority, the originating Run's cancellation/failure and deadline, including revocation while staging. Completed parent Runs remain eligible; a live claim is not required merely to finalize an authorized background process.
- **Recovery UI:** the existing Ant Design forms/drawer distinguish confirmed committed files from unknown commit outcomes, show actionable setup failure and retained-file expiry guidance, and rename the expanded action to Verify profile. Elapsed Ready environments display Expired. Focus returns to the current header trigger even when Escape interrupts drawer opening or the viewport changes. Existing stale-selection and read-error safeguards are retained.

## Executed checks

All provider work was Synthetic; real local Docker/OpenSandbox execution used private credentials without printing the server key. The already running server was reused. Infrastructure fixtures and browser data were disposable.

| Check | Exact command / invocation | Result |
| --- | --- | --- |
| Final Infrastructure and real Docker matrix | `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --filter 'FullyQualifiedName~OpenSandboxIntegrationTests\|FullyQualifiedName~WorkspaceStructureTests\|FullyQualifiedName~SandboxStoreTests\|FullyQualifiedName~OpenSandboxCleanupTests' --nologo -m:1 --logger 'trx;LogFileName=opensandbox-reliability-final.trx'`, with `AGENTCORE_OPENSANDBOX_TESTS=1` and private runner URL/key/image environment | **49 passed, 0 skipped** in 4m15s, including **16 real OpenSandbox integration tests** |
| API and Office integration | `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --filter 'FullyQualifiedName~SandboxApiTests\|FullyQualifiedName~OfficeJourneyTests' --nologo -m:1` | **7 passed** |
| Tool authority/executor | `dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-restore --filter 'FullyQualifiedName~ToolRegistryPolicyTests\|FullyQualifiedName~SessionToolExecutorTests' --nologo -m:1` | **21 passed** |
| Frontend state/service tests | From `web`: `pnpm run test --run src/features/chat/SessionSandboxDrawer.test.tsx src/features/admin/SandboxProfilesSection.test.tsx src/services/sandboxes.test.ts` | **22 passed** |
| Frontend build | From `web`: `pnpm run build` | **Passed**, supported Node 22 |
| Real Workbench browser, initial phase | `python3 /private/tmp/agent-core-sandbox-reliability-ui.py before`, invoking `node web/node_modules/@playwright/test/cli.js test --config playwright.opensandbox.config.ts` with private runner settings and disposable API/Vite on 6480/6473 | **1 passed**; final settled-state capture pass 32.2s |
| Real Workbench browser, Core recreation phase | Owned test API restart, then `python3 /private/tmp/agent-core-sandbox-reliability-ui.py after` | **1 passed**, approximately 1.2m; shared server was not restarted |
| Playwright MCP | Actual Admin script edit → Save profile → Verify profile; visible exit-7 failure; 390px inspection; sandbox drawer open/close and focus inspection | Expected safe failure observation, no sentinel in returned message, no horizontal overflow, successful verification requests and no errors during the verification flow |
| Source hygiene | `git diff --check` | **Passed** |

The temporary wrapper starts the normal native API/Vite with isolated SQLite, Workspace, Artifact, attachment and key roots. It does not alter the supported production/local runner. For a clean exclusive acceptance environment, the documented `scripts/opensandbox-local.py accept --ui` remains the standard command; its full server lifecycle was deliberately not invoked against the shared running server.

## Scenario evidence

- Three concurrent disposable environments each executed permitted **65-second** work with **60-second idle TTL**: foreground command, tracked background command and initialization. Each finished with exit 0, committed exact expected bytes, and stayed inside maximum lifetime. A 60-second maximum lifetime rejected a 120-second command before dispatch, with no operation receipt or forbidden file.
- Fault injection before swap, after backup rename, after canonical replacement and after journal completion recovered respectively `notCommitted`, `notCommitted`, `committed`, `committed`. Reapplying the same commit ID was rejected. Ordinary later writes did not change historical commit evidence.
- A real command's canonical replacement succeeded while terminal receipt saves were injected to fail. After reopen, reconciliation retained `committed`, reported command uncertainty, preserved both `once.txt` and a newer canonical `later.txt`, and returned the prior operation rather than dispatching again.
- Verification rejected exit 7, unsafe `.env` output and exit 9 with private stderr sentinel, and accepted a valid initialization. No Session lease was created by verification. InMemory/SQLite observations survived revision changes/reopen without modifying the original configuration.
- Inspecting a finished background process left the receipt Running, the lease revision unchanged and canonical files untouched. Supervision then committed it. Canceling its Run during the next import's staging fence rejected the late file and retained `notCommitted`.
- Browser verification first saved a failing script and observed `passed:false` with the same saved revision, then corrected and verified it. Python's deliberately failing test was fixed, Node's test passed, and XLSX/DOCX were generated, validated, downloaded and previewed. After recreating Core, generation and once-only initialization were retained and download hashes were unchanged. Output appeared before a >30-second command ended; cancel/reset retained canonical files; an owned background Session produced both Office artifacts and was deleted normally.
- Actual layout checks covered 1440/1024/768/390/320px. Recovery presentation fixtures separately covered setup failure, ended environment, unknown commit and confirmed commit with uncertain command completion at 1440/390px. Settled drawer geometry, no page overflow, Escape and focus return were asserted. Fixtures demonstrate presentation; filesystem fault tests above provide actual crash evidence. Two settled representative screenshots were visually inspected.

Initial build verification caught a nullable initialization variable after moving renewal; it was corrected. Browser testing found that canceled opening motion may omit AntD's completion callback; an explicit current-trigger ref plus post-focus-lock restoration fixed the reproducible failure. Final reruns passed. Existing jsdom textarea-height, SignalR annotation and large-chunk build warnings remain; they did not appear as functional errors in the passing browser scenarios.

## Evidence and cleanup

Backend evidence: `tests/AgentCore.Infrastructure.Tests/TestResults/opensandbox-reliability-final.trx` (gitignored). Browser screenshots/traces and the MCP initialization-failure screenshot are retained under `local/opensandbox-reliability-review/before`, `after` and `initialization-verification-mobile.png` (gitignored). Test API/Vite logs and the temporary wrapper's record are under its private temporary data root. No server API key is included in this report or committed source.

Five remaining Sessions from repeated browser attempts were deleted only from the verified disposable Synthetic host. Its API/Vite processes were stopped. The shared OpenSandbox server, its private settings, user storage and unrelated Docker resources were retained.

## Remaining limits and demo readiness

**The existing coding/Office demo remains ready**, with real command, artifact, preview, cancellation, recovery and Core recreation evidence above. The new observation table requires the normal additive startup migration.

The agreed trusted single-user local Docker model remains unchanged: own execd loopback is reachable, and the OCI writable layer has no disk quota; canonical scratch ceilings remain enforced. The journal covers process interruption, not verified power-loss/host-filesystem corruption durability. Evidence is retained with the Session filesystem until its deletion. Historical receipts without evidence may remain unknown. Background status/logs may lag the supervisor's normal 15-second pass. Shared-server recreation and the separate upstream cgroup/network proof were not rerun in this review; their existing acceptance evidence is retained. Hosted CI, full unrelated suites, physical-device testing and production-grade isolation are outside this completed focused scope.
