# Human-task background Browser and Sandbox — 2026-10-20

This checkpoint converts native Browser/Sandbox operations for human-attributed immediate-background tasks and exact approval successors. It reuses the existing owners rather than adding an executor, queue, permission or receipt family. Autonomous origins and the full IAM proposal remain open.

## Implementation

`TenantTaskExecutionContract` generalizes and replaces the file-only shape/discovery helper without a compatibility alias. Existing file owners retain their behavior. Background projection now includes native read-only inspections plus exact Browser/Sandbox command, read and control authorization branches when admitted capabilities permit them. Unrelated settings/credential management, child fanout and direct native mutation tools remain excluded. Protected Browser password fill retains the canonical direct-UserTurn lineage restriction and is excluded from the background schema.

`TenantToolAuthorization` accepts only those converted intents for actual saved background Runs. The native Browser source owner and Sandbox current-policy, direct/reviewed read and control owners repeat the shared task check, including admitted and current `background.start`, independently of projection. Existing original-human lineage, current raw permissions, secondary source reads, target/profile/generation/configuration/state-isolation witnesses, exact decisions, no-replay receipts and technical retirement remain authoritative. The general authorization description now accurately names offered capture/upload/drop and retained coordinate/geolocation restrictions.

The child Session owns native state, source files and published Artifacts. Approval releases its worker; exact recorded successors resume through the existing coordinator and tool checkpoint. Command completion, application success, process completion, cleanup request/confirmation, file publication and final reporting remain distinct facts. Only a terminal non-Continued task successor contributes completion evidence for the original parent; a refusal or uncertain effect never repeats the native action.

## Exercised workflows

Native Chromium cases run the actual Synthetic model, task admission, mailbox, approval decisions, successor workers, native target and report coordinator in Memory and SQLite. They navigate an explicitly allowed local target, inspect current page content either directly or through separate read Review, optionally publish one capture, and finish/report to the original conversation. Rejection/cancellation produces no Browser execution. Revoking native authority or changing the configuration disables decisions while retaining cancellation. Removing `background.start` through a newly published/adopted definition blocks the native target owner itself with `browser_policy_changed`; cancellation settles without a command receipt or report.

Upload/drop cases create a test-owned source through the actual executing child claim and canonical Artifact store. Native input/drop receives the exact 4,096 binary bytes; drop dispatch is observed once. Browser use, child Session reads and Artifact source/publication authority remain independently assigned. Files and Browser contexts belong to the child; the parent has no native context. This source seeding is fixture setup, not a production debug endpoint or a claim that model publication consent was executed by setup.

A pending native Browser request survives actual SQLite application disposal/recreation, retaining the exact saved operation and continuing once. Its destination page lives in an independently owned loopback fixture so host recreation does not change the consented origin. All positive/refused terminal tasks deliver one factual parent report; capability-loss refusal records Skipped without a report.

The Sandbox fixture runs the actual model/approval/successor/SDK/coordinator against an authenticated controlled loopback peer. Twelve background cases cover exact command approval/rejection/cancellation, first-generation provisioning/initialization, uncertain command acknowledgement, direct stored profile/status/process observations, separately reviewed stored observations and rejection, plus Stop/reset request and technical retirement. Stored observations make no SDK contacts; control receipts distinguish accepted cleanup from confirmed termination. Uncertainty fails with the existing factual indeterminate outcome and no replay. Each terminal task, including its failure facts, reports through the original parent completion owner.

## Focused checks

Commands used Synthetic, temporary roots and no inference-provider credentials. Log names below live in `/private/tmp/`.

| Check | Result / evidence |
| --- | --- |
| Representative native Browser | `AGENTCORE_CHROMIUM_ISOLATION_TESTS=1 dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~Tenant_background_native_Browser_uses_exact_child&DisplayName~approve'`: 2 passed, `tenant-background-native-browser-representative.log` |
| Browser refusal/read Review/capture | Original remaining group: 8 passed and one restart-fixture failure, `tenant-background-native-browser-group.log` |
| SQLite pending native restart | `--filter FullyQualifiedName~Tenant_background_native_Browser_pending_consent`: 1 passed after fixing the target fixture, `tenant-background-native-browser-restart-fixed.log` |
| Revoked native access | 2 passed in `tenant-background-native-browser-stale.log`; its two changed-configuration cases initially used a nonexistent fixture route |
| Changed configuration | `--filter 'FullyQualifiedName~Tenant_background_native_Browser_uses_exact_child&DisplayName~changed'`: 2 passed after using canonical persona PUT, `tenant-background-native-browser-changed-fixed.log` |
| Current background capability | Same case filter with `DisplayName~capability`: 2 passed, `tenant-background-native-browser-capability.log` |
| Native original-byte upload/drop | Same case filter with upload/drop display names: 4 passed, `tenant-background-native-browser-transfer.log` |
| Representative background Sandbox | `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --no-build --nologo --filter 'FullyQualifiedName~Tenant_background_native_Sandbox_&DisplayName~approve'`: 1 passed after explicit initial background projection, `tenant-background-native-sandbox-representative-fixed.log` |
| Remaining background Sandbox | Same case filter with `DisplayName!~approve`: 11 passed, `tenant-background-native-sandbox-group.log` |
| Affected incumbent Sandbox/core background | `--filter 'FullyQualifiedName~Tenant_Sandbox_model_request_waits_for_exact_Review_then_shared_worker_delivers_bounded_result_once\|FullyQualifiedName~Tenant_background_model_starts_detached_owned_work'`: 15 passed, `tenant-background-native-foreground-runtime-regression.log` |
| Projection/contracts | Application filter `TenantBackgroundFileObservationTests`, `CapabilityProjectionTests`, `TenantCompletionReadContractTests`: 43 passed and 2 stale projection-assertion failures initially. Corrected focused projection: 3 passed with one overlap, qualifying 45 distinct cases. `tenant-background-native-contracts.log`, `tenant-background-native-projection-fixed.log` |
| Native owner store regression | 280 passed, 3 native Theory methods skipped pending explicit opt-in; `tenant-background-native-store-regression.log`. The run completed in 6m46s; its managed stack confirmed fixture creation while active |
| Opt-in native store cases | `AGENTCORE_CHROMIUM_ISOLATION_TESTS=1` with the three previously skipped capture/upload/download method filters: 8 passed (theories expand by store/drop choice), `tenant-background-native-store-native.log` |
| Final build | `dotnet build AgentCore.sln --no-restore --nologo`: zero warnings/errors, `tenant-background-native-final-build.log` |

These checks qualify **381 distinct focused cases**: 21 new Browser workflows, 12 new Sandbox workflows, 15 incumbent HTTP/runtime cases, 45 application contracts/projection cases and 288 native owner/store cases. The initial three skipped native Theory methods expanded to eight passing cases under explicit opt-in; no native skip remains unqualified within this selected group. Passing groups were retained after fixture-only corrections. Relative-link validation resolved 1,947 local targets and 264 fragments across 17 changed Markdown files, with no errors or unbalanced fences; `git diff --check` passed. All owned test processes completed, and the interrupted duplicate ended.

The earlier Browser remaining-group log initially appeared incomplete at process observation; a redundant rerun was started, then interrupted after the original final log showed eight passing cases and one exact failure. That interrupted duplicate contributes no acceptance count.

The initial new test build corrected the Artifact page cursor/count assertion and the opt-in native Theory attribute. Sandbox initial background admission needed explicit initial capability projection in its fixture. Older projection assertions also omitted the already-converted completion/wait tools; they now describe the actual approved task tool family. The SQLite Browser restart initially recreated the page fixture at a different ephemeral origin, causing the correct `target_denied`; a separately owned stable target qualified unchanged consent. Two stale-configuration fixtures initially called a nonexistent PATCH route; the canonical persona PUT corrected that setup. These corrections did not relax production authorization.

## Evidence limits and remaining goal

No frontend presentation changed. Native Chromium integration is backend/runtime evidence, not a new frontend Playwright MCP or visual-polish claim; the existing shared inspector finish records remain scoped to their earlier changes. Sandbox protocol outcomes are controlled peer facts, not actual Docker shell/mount execution. The Docker daemon version was read successfully (29.9.1) and its container list was empty; no Sandbox service was started or existing deployment changed in this checkpoint. Live Docker background execution remains unqualified.

Autonomous origins/occurrences, remaining interaction and protected-operation/System/audit families, final obsolete-authority/schema consolidation and requirement-by-requirement proposal acceptance remain open. No full local/hosted CI, live inference provider, Real host, user-data reset, commit, PR, push or deployment is claimed. The original full goal remains active.
