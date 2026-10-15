# Sandbox final polish and controlled acceptance — October 15, 2026

Baseline: `7fc36ba1e1c6b49dc296e46424d8881fc9100a3c`. This is the requested bounded follow-up to the [generic runtime refactor](generic-linux-sandbox-runtime-verification-2026-10-15.md). Architecture, ownership, initialization rules and trusted-local scope are unchanged.

## Review observations resolved

**P2 — completed-command inspection failures.** [Quiescence](../../src/AgentCore.Infrastructure/Sandbox/OpenSandboxService.Quiescence.cs) now treats an unexpected inspection error as inability to reuse a generation. Only the completed-command [finalization path](../../src/AgentCore.Infrastructure/Sandbox/OpenSandboxService.Finalization.cs), entered after durable main completion and a live authority fence, uses this fallback. It retires the exact owned container, requires authoritative absence, then validates retained staging and the existing Run/Workspace commit requirements. Provider denial keeps staging and blocks import. Actual caller cancellation propagates to cancellation cleanup; Run cancellation is rechecked before publication. Initialization still uses strict inspection and cannot silently recycle/replay setup. Safe logs include Session/generation identity without the exception/body.

**P3 — file recovery versus cleanup.** `SandboxEnvironmentInfo.fileRecoveryRequired` independently projects NeedsResync, including owner HTTP GET and `sandbox.status`. `cleanupPending` retains its Terminating/NeedsReconciliation meaning. The [existing drawer](../../web/src/features/chat/SessionSandboxDrawer.tsx) shows a File recovery row and “Staging retained until file recovery resolves” when unknown effects retain staging. It does not claim “No pending cleanup.” Existing Ant Design Descriptions, spacing, responsive behavior, diagnostics and actions remain the owners.

The internal inspection seam is test-only and has no HTTP/tool configuration surface. No new recovery store, SDK-specific handling or architecture was introduced.

## Focused verification

| Check | Actual result |
| --- | --- |
| `dotnet test tests/AgentCore.Infrastructure.Tests --filter 'FullyQualifiedName~Pending_retirement_preserves\|FullyQualifiedName~Reset_retains_unknown'` | 9 passed: unexpected failure, blocked/confirmed deletion, Run cancellation, caller cancellation, reset and narrowed authority retain correct file outcomes |
| `dotnet test tests/AgentCore.Infrastructure.Tests --filter FullyQualifiedName~OpenSandboxCleanupTests` | 25 passed |
| Opt-in real SDK: `Completed_inspection_failure_retires` and `Generic_residual_processes` | 2 passed in 34 seconds; exact bytes/exit 7 survive inspection error and confirmed retirement; ordinary reuse, detached child retirement and selected-profile continuity remain working |
| `dotnet test tests/AgentCore.Api.Tests --filter FullyQualifiedName~SandboxApiTests` | 16 passed |
| `pnpm run test --run src/features/chat/SessionSandboxDrawer.test.tsx src/services/sandboxes.test.ts` in web/ | 38 passed |
| API build and frontend TypeScript/production build | Passed; backend zero warnings/errors; existing large-chunk advisory remains |
| Synthetic Playwright MCP | Changed recovery state and inspection recovery exercised; five widths fit; Escape restored opener |

Host test commands additionally used `--nologo -v minimal -m:1 /nodeReuse:false /p:UseSharedCompilation=false`; these are runner options, not workload flags. No full CI suite was run.

The deterministic loopback fixture injects inspection failure only after known main completion, first denies provider deletion (403), and confirms staging remains with no canonical import. On a later 204, valid authority commits exact bytes once and cleans staging. Run cancellation during the failed inspection and actual caller-token cancellation instead prevent publication. The unknown-file projection regression verifies `fileRecoveryRequired=true` and `cleanupPending=false` while physical staging remains.

The real SDK fixture executes `printf recovered-exact > inspection.txt; exit 7`, injects an inspection error, confirms Completed / exit 7 / committed with recycled and final termination proofs, then reads exact bytes from a fresh generation. No language-specific cleanup is used.

Synthetic browser checks used the separate API/Vite fixture on 5096/5196. A controlled NeedsResync response retained a Completed exit and unknown file outcome. At 1440/1024/768/390/320px the page width equaled viewport width; the new File recovery row was present and misleading cleanup wording absent. Inspect status restored the real response and Escape returned focus. This is UI fault projection; actual physical retention/termination behavior is established by the loopback/real SDK checks above.

## Intentional Real deployment and disposable acceptance

The managed native Real API/Vite stack was stopped before deployment; the existing authenticated OpenSandbox Compose server was ready. The operator-authorized `scripts/dev-real.sh start` built and started the reviewed candidate on API 5080 / UI 5173 using the existing http-openrouter launch profile, protected provider configuration, SQLite and binary-store paths. It reused the private server and installed images. No historical Session was reset, replayed or adopted to another publication.

Before startup, SQLite's backup API saved a private backup under ignored `local/verification/sandbox-polish-acceptance`; integrity checked `ok`. All **67 historical Session IDs** remain present, and **169 original canonical/binary file hashes** remained unchanged after the real-use workflow. Existing Instances, immutable publications and Sandbox Profiles were preserved. The acceptance candidate's assembly hashes, private backup and owned execution evidence are retained locally; its informational build version identifies baseline 7fc36ba1 rather than pretending the uncommitted candidate already had a commit ID.

A separate disposable Instance uses existing General Assistant v31 and the already authorized/verified `general-workbench` revision 6. The actual configured Real model received one constrained Chat request through the UI:

1. Execute once: `printf sandbox-acceptance-exact > acceptance.txt; sleep 90 >/dev/null 2>&1 & printf created`.
2. After confirmed completion, issue the independent command `cat acceptance.txt`; stop on uncertain results rather than replay.

Observed durable evidence:

| Evidence | Result |
| --- | --- |
| Acceptance Instance | `01a13e85-fba3-75eb-90fa-19ab4a79609b` |
| Acceptance Session | `f16e2af7-8592-4b45-b6d6-784896b66401` |
| Original write execution | `01a13e86-80e3-70ed-9f0c-d1a79598c69e`: Completed, exit 0, committed, generation recycled, remote termination and staging cleanup confirmed |
| Retired generation | `01a13e86-7165-7bff-8b74-b18015c19d88` |
| Independent read execution | `01a13e86-a226-77b9-8de0-d9230a2762bc`: Completed, exit 0, committed; exact stdout `sandbox-acceptance-exact` |
| Fresh selected-profile generation | `01a13e86-9562-7214-b03a-327325c9214c` |
| Run | Completed; exactly the two requested shell dispatches in this acceptance request |

Playwright opened the actual Real drawer and verified the readback, selected profile, Completed exit and Files committed. No install, external-site access, historical Session access or automatic retry was requested. The model provider call is distinct from the key-free Synthetic tests.

## Scope and remaining boundaries

This closes the two observations and provides controlled acceptance of the intentionally deployed generic runtime in the trusted-local Linux ARM64 environment. The prior native AMD64 workload gap remains: this ARM64 daemon's pinned provider policy rejects the incompatible AMD64 cached image. Existing emulated AMD64 collector evidence is unchanged. No suitable Java image was installed, so optional Java compatibility remains untested. Hosted CI was not requested or run. Production/multitenant hardening and hardware/manual accessibility coverage are not newly claimed.
