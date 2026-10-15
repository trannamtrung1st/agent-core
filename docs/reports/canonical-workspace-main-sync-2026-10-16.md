# Canonical workspace integration with current main

Owner-authorized synchronization on `develop/branch-1`, starting from `25273f17bfa4ef25548602fed4ba8df3b5abd44f`. Merged main through `702e3808c3b330c12a6880343c13942b136ec59a` with a merge commit, preserving branch history. This follows the [original cutover](canonical-workspace-cutover-verification-2026-10-16.md) and [review corrections](canonical-workspace-review-2026-10-16.md).

## Retained main changes

All seven incoming commits remain in history: separate visibility defaults/ceilings and Custom selection; one shared State/Memory/Voice configuration draft; Save preview retention across navigation; mobile navigation focus timing; policy-aligned fixtures; settled sandbox recovery assertions; and typed background-task support in the Synthetic Workbench script. Existing canonical files, current-path contracts, upload bounds, private mounts and lifecycle projections remain.

## Conflict decisions

| Conflicted file | Resolution |
| --- | --- |
| `OpenSandboxService.Initialization.cs` | Keep canonical mount authority and explicit UID/GID/HOME command options; incorporate literal shell argument handling from main. |
| `OpenSandboxService.Verification.cs` | Keep three disposable RW/RO/disabled probes, process-only cleanup and exact initialization exit evidence; apply the same literal command wrapper to setup. No scratch capture/import is restored. |
| `OpenSandboxIntegrationTests.cs` | Preserve main's known-effect assertion and bounded output. A created symlink is an ordinary canonical node; reading an escaping target remains forbidden. Retired unsafe-file collection/commit assertions are not applicable. |
| `workbench.spec.ts` | Retain main's explicit settled termination/cancellation assertions, using canonical effects and current verification messages rather than committed/staging states. |
| Backend implementation spec | Keep sampled canonical storage and process-only recovery; document literal private command defaults. |
| Persistence/configuration spec | Keep exact canonical/daemon root mapping and matching non-root identity. Do not restore world-writable scratch or staging configuration. |

Main's `SandboxCommandPermissions` fixes literal shell/argv handling and nested-output cleanup under its earlier cross-UID staging design. Canonical mounts instead require the same verified Core/workload UID/GID. The merged helper uses **umask 077**, not main's 000, consistently for commands and Profile/Instance verification/setup. New nested nodes remain removable by Core under that identity without making them world writable. Its focused tests retain literal quotes, substitutions, exit-code and nested-directory checks and assert private group/other permissions. Explicit command chmod/umask retains ordinary Unix semantics.

The merge also exposed shared test roots: SQLite hosts whose databases were files in the temp directory all selected the same sibling `workspaces`. The canonical layout gate correctly refused populated legacy leftovers. Six in-process recovery factories and the child Kestrel host now use `<database-path>.workspaces`, preserving reopen identity while isolating separate databases. No shared temp tree or user data was deleted to make these tests pass.

## Executed checks

Backend commands used `dotnet test <project> --no-restore -m:1 /nodeReuse:false /p:UseSharedCompilation=false --filter <selection>`.

| Check | Result |
| --- | --- |
| API: WorkspaceApiTests, StateIsolationConfigurationTests, StateIsolationWorkspaceTests, ContinuityBudgetTests, SandboxApiTests, SqliteHostRecoveryTests | **42 passed**. Covers new visibility ceilings, coordinated presets, workspace lifecycle/file bounds, SQLite reopen and actual child-host restart. Initial four shared-root failures and the child-host root mismatch were corrected and rechecked. |
| Application: StateVisibilityCeilingTests, StateIsolationPolicyTests, CapabilityProjectionTests, WorkspaceStructureToolContractTests | **73 passed**. |
| Infrastructure: SandboxCommandPermissionsTests, OpenSandboxScriptTests, CanonicalWorkspaceFilesystemTests, CanonicalStartupMigrationTests, ContextCutoverTests, HostedTriggerToolTests | **27 passed**. |
| Real OpenSandbox: `FullyQualifiedName~Canonical_\|FullyQualifiedName~Verification_executes_initialization\|FullyQualifiedName~Instance_setup_runs_after_profile\|FullyQualifiedName~Retired_tool_never_dispatches\|FullyQualifiedName~Real_workbench_fixes_python` | **23 passed**, 2m32s. Existing private server and qualified matching-identity images; unique disposable roots. Commands/setup/verification, literal effects, Git, shared/private files, cwd, live policy reduction, retained partial writes, cleanup and Workbench output remain functional. |
| Frontend: InstanceIsolationWorkspace, IsolationPresetControl, StateIsolationConfigurationSection, DefinitionStateIsolationPolicy, InstanceSettingsSection, SandboxProfilesSection and appRoute test files | **96 passed** across 7 files. Includes navigation during Save, shared drafts, Custom and visibility behavior. React act advisories remain test-harness warnings. |
| `pnpm --dir web run build` | Passed TypeScript and Vite. Existing chunk-size/SignalR annotation warnings remain. |
| Five conversational workspace Playwright journeys against explicit disposable loopback Synthetic hosts | **5 passed**, 29.1s, using `/tmp/canonical-main-workspace-results` for isolated artifacts. An initial concurrent runner shared Playwright's default output directory and lost a trace file during context close; the rerun used an independent directory. |
| Desktop/mobile Admin route browser scenarios | **2 passed** at 1440/390px, 2.0m, using `/tmp/canonical-main-navigation-results`. The first desktop attempt had the same concurrent trace-directory collision; isolated rerun passed both scenarios. |
| Source conflict/retirement audit, changed Markdown links/fences and design source hashes | Passed after final synchronization. |

Synthetic Playwright MCP created a fresh General Assistant v32 Instance, selected Stateless, navigated State → Memory → Voice and observed one retained draft. Discard restored inherited Memory and Voice values without saving. A second Stateless draft was saved from Voice; after reload, Voice and Session Memory remained disabled and configuration was saved. No page overflow was observed. A stale capability from the previous disposable host initially produced 401 responses; clearing only this disposable browser's storage acquired the new capability, after which the exercised flow had no unexplained error.

Local logs: `/tmp/canonical-main-{api,application,infrastructure,web,web-build,browser,navigation}.log` and `/tmp/canonical-real-next-tests.log`. These are temporary host evidence, not committed secrets. Design context and sidecar source hashes describe the merged shared draft/navigation owners; prior visual/source manifests stay tied to their original candidate.

## Boundaries

No hosted CI, full CI suite, user application/server restart, user data reset or deployment was requested or performed. Existing installations still require explicit backup/reset and current-contract reverification. Storage remains sampled; no new architecture/filesystem/image qualification is inferred from this merge. The existing real server was reused, never duplicated or restarted. Only task-owned Synthetic API/Vite processes were stopped after verification.

The branch is pushed without force. The pull request targets `main` and describes the complete canonical filesystem change plus this integration, including the intentional fresh-install boundary. Creating the PR does not merge it or alter existing milestone acceptance.
