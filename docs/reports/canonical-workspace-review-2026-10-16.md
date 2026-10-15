# Canonical workspace review follow-up

Owner-authorized review/fix/commit/push on `develop/branch-1`, starting from clean commit `5a0464e54922c6575d9d354d4e8fba8ce252c344`. This is a focused consistency/behavior review of the [canonical cutover](canonical-workspace-cutover-verification-2026-10-16.md), not new milestone acceptance or a deployment.

## Findings and corrections

1. **Working listings misreported archived content as writable.** `FileSessionWorkspace` emitted `Writable=true` regardless of Session/Instance archive state, although mutations correctly failed. The root node, direct listings and paged listings now derive writability from the same lifecycle predicate as writes. Two API workflows reproduced the mismatch before the fix. After archiving either the Session or its Instance, paged and direct listings report read-only, writes fail, and retained file bytes remain readable. Unarchive restores writable metadata.
2. **HTTP upload buffering used the aggregate limit instead of the file limit.** Session PUT content accepted an announced length between 50 and 250 MiB and could buffer an unknown-length body to 250 MiB before the filesystem rejected it. It now rejects announced lengths over 50 MiB before reading and stops streamed intake at that same per-file bound. Both home and working routes are tested; rejected destinations remain absent. A generated unknown-length server body proves endpoint reads stop before consuming the complete oversized input, without allocating its whole source payload. The shared quota error now says a file or storage limit was exceeded rather than incorrectly identifying every quota as 250 MiB.
3. **Some operator text still described the retired installation path and ceiling.** README now distinguishes the historical context conversion and file-commit transport from the current fresh baseline. Protocol/Operations describe the configurable host storage ceiling (default 16 GiB), separate file versus aggregate limits, and matching image directory ownership in addition to UID/GID. The obsolete additive-upgrade instruction is explicitly historical. No existing installation was converted or reset.

Three initial API reproductions failed as expected: both archive projections returned true, and an oversized announced upload returned 204. All corrected scenarios pass. A first streamed test counted the in-memory transport's outgoing producer, which can buffer independently; it was replaced by a direct TestServer request body so the counter measures actual server reads. That harness issue was not a product intake failure.

## Executed verification

All backend commands below use `--no-restore -m:1 /nodeReuse:false /p:UseSharedCompilation=false`. These are focused selections, not a full CI suite.

| Command/project and filter | Observed result |
| --- | --- |
| `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj` with `FullyQualifiedName~WorkspaceApiTests\|FullyQualifiedName~StateIsolationWorkspaceTests\|FullyQualifiedName~AgentWorkspaceJourneyTests\|FullyQualifiedName~SessionWorkingCleanupTests\|FullyQualifiedName~SandboxApiTests` | **30 passed**, including archive/unarchive projections, retained bytes, forbidden writes, announced and unknown-length upload limits, policy restrictions, owner isolation, SQLite reopen and sandbox configuration boundaries |
| `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj` with `FullyQualifiedName~FileSessionWorkspaceTests\|FullyQualifiedName~WorkspaceStructureTests\|FullyQualifiedName~CanonicalWorkspaceFilesystemTests\|FullyQualifiedName~UnifiedWorkspacePhysicalLayoutTests\|FullyQualifiedName~CanonicalStartupMigrationTests` | **33 passed**, covering descriptor-relative ordinary files/links/modes, adapter lifecycle, structure, templates, owner layout and non-mutating schema refusal |
| `dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj` with `FullyQualifiedName~WorkspaceArtifactToolTests\|FullyQualifiedName~WorkspaceStructureToolContractTests\|FullyQualifiedName~SessionWorkspaceLifecycleTests\|FullyQualifiedName~StateIsolationPolicyTests\|FullyQualifiedName~CapabilityProjectionTests\|FullyQualifiedName~SandboxFailureSummaryTests` | **84 passed**, covering unchanged tool authorization/effects, lifecycle, policy, capability and failure evidence |
| Changed Markdown link/anchor/fence validation and `git diff --check` | Passed |

Logs are `/tmp/canonical-review-api.log`, `/tmp/canonical-review-infrastructure.log`, `/tmp/canonical-review-application.log` and the initial `/tmp/canonical-review-reproduction.log`; they are temporary local evidence. Tests used disposable Synthetic fixtures and do not require provider credentials.

The review also traced the direct mount resolver/probe, fd-relative file operations, hardlink/symlink boundaries, policy retirement, current-path tools/HTTP and explicit reset helper. No additional actionable issue was identified within this pass. This is a bounded review, not proof of every concurrent host-filesystem schedule.

## Scope and remaining boundaries

The original commit's source manifest, screenshots and native/packaged real-container qualification remain evidence for that original source. This follow-up changes server intake and shared lifecycle projections; the integrated Synthetic API/filesystem checks above verify those changes. It changes no frontend component, mount protocol, process execution or database schema. Real-container/packaged image and browser suites were not rerun for this follow-up, and no new image digest or milestone/hosted acceptance is claimed.

No user database/root reset or application/server restart was performed. CI was not requested or dispatched. Storage remains sampled; larger Profile command budgets do not raise the separate 50 MiB file and 250 MiB aggregate application-tool working bounds. Existing installations still require the explicit backed-up reset and current-contract reverification. Commit and push use the existing branch without a force push.
