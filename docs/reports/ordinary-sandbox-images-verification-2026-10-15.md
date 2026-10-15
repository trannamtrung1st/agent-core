# Ordinary Linux sandbox image support — 2026-10-15

The owner approved ordinary-image support after the stock .NET SDK image failed the former Python/non-root/pre-created-scratch checks. This is the requested OCI onboarding successor, not milestone acceptance. Hosted CI was not requested.

## Cause and resulting behavior

Read-only inspection of the selected installed MCR SDK digest confirmed default UID 0, no `python` command and no `/working`. The existing private server was authenticated and healthy. The original rejection followed the previous adapter contract; the generic readiness error did not explain it.

Ordinary Linux images now use their default user, including container root, under the existing dropped-capability/no-new-privileges policy. Workload Python and an image-owned `/working` are no longer required. The platform mounts a private per-generation staging directory at `/working`; Core validates exact files and commits them through the existing canonical Workspace gate, fingerprint, live authorization and crash journal. `/home` retains durable Instance ownership and explicit tools/copy; canonical storage and secrets are never mounted.

Host-side POSIX checks reject symlinks, hard links, FIFO/socket/special files, path/case/Unicode collisions, oversized trees and changing bytes. PID/start-time checks use shell builtins and `/proc`, with no `python`, `ps`, `cat` or `jq` dependency. Cleanup removes staging only after confirmed workload absence, including lost-create reconciliation and expired/missing generations. Quotas constrain canonical import; they do not enforce live staging or writable-layer disk quotas.

The updated Compose/native handoff configures a dedicated staging root and exact server host-path allowlist. Packaged-app configuration maps Core-local and Docker-host roots explicitly. No startup workload build or image replacement occurs. Verification contract 2 and lease `mountContractVersion=1` preserve old JSON/history but require profile reverification and explicit legacy generation reset. No database schema migration/reset is needed.

## Observed real SDK journeys

Private server authentication was read through the existing project helper and passed only in child environments, never printed. Only one server was used. Before updating its mount configuration, its active sandbox count was confirmed zero; Compose retained metadata/authentication volumes and the existing daemon.

- Stock `mcr.microsoft.com/dotnet/sdk@sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa`: both image-only and full verification passed without Python. Commands ran `dotnet --version`, wrote/read/updated files under mounted `/working`, and Core Workspace readback matched exact committed bytes.
- The same regression created a console project, restored from an explicitly local empty package source and ran it with shared compilation/build-server reuse disabled. Expected/observed: Completed, exit 0, `Hello, World!`, and committed `app/Program.cs` readback. No package download or model provider was used.
- The existing installed Workbench also verified, preserving optional Python workload support.
- Six focused real SDK scenarios passed: ordinary .NET; Profile/Instance initialization ordering, output and failed-setup noncommit; link/untracked-child rejection; exact scratch/reopen/background stop/replay fencing; quota/concurrent canonical-write rejection; crash-after-swap/lost-receipt recovery preserving newer files.
- Native mount-file tests also ran inside the offline Linux ARM64 SDK container: 9 passed, including regular files, symlinks/directory links, hard links, FIFO, collisions, quota and explicit host-path mapping. The native Core host is macOS ARM64. x64 metadata layouts are implemented but were not executed here.

## Observed browser journey

Playwright MCP used disposable Synthetic Core at `127.0.0.1:5280`, Vite at `127.0.0.1:5373`, and the same private server. `/health` confirmed Synthetic. Existing user data/profiles were not edited.

Add prebuilt image → `.NET ordinary image`, `mcr.microsoft.com/dotnet/sdk:10.0`, no credential → Save: HTTP 200, pinned digest, NeedsVerification. Check image readiness: HTTP 200, passed, no initialization execution. Verify profile: HTTP 200, passed with shell/mounted-scratch/file-validation message, Eligible, revision unchanged at 1. Reload retained Verified · eligible and the digest. Browser console errors: zero; related API requests all succeeded.

## Focused commands and results

- `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --filter 'FullyQualifiedName~SandboxMountFilesTests|FullyQualifiedName~SandboxImageReadinessTests|FullyQualifiedName~OciSandboxAuthorityTests|FullyQualifiedName~RegistryAuthorizationProbeTests|FullyQualifiedName~OpenSandboxCleanupTests' --nologo --verbosity quiet -m:1 -nodeReuse:false -p:UseSharedCompilation=false`: **100 passed**. Includes the prior-contract guard and exact-metadata verification lost-create cleanup. Existing opt-in public-registry cases remain inert without their separate opt-in.
- `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --filter 'FullyQualifiedName~SandboxApiTests|FullyQualifiedName~SelfManagementOperationTests' --nologo --verbosity quiet -m:1 -nodeReuse:false -p:UseSharedCompilation=false`: 23 passed.
- `pnpm run test --run src/features/admin/SandboxProfilesSection.test.tsx src/services/sandboxes.test.ts --testTimeout=60000 --no-file-parallelism`: 28 passed. Existing Ant Design/JSDOM height warning is non-failing.
- `python3 -m unittest scripts/tests/test_opensandbox_config.py -q`: 9 passed.
- `pnpm run build`: passed with existing bundle-size/dependency annotation warnings.
- `docker compose --env-file local/opensandbox-compose/agent-core-sandbox/compose.env -f docker-compose.yml -f docker-compose.sandbox.yml -f docker-compose.app-sandbox.yml config --quiet`: passed without starting the app containers or printing private configuration.
- `git diff --check`: passed.

Real SDK test opt-ins are `AGENTCORE_OPENSANDBOX_TESTS=1`, the existing private URL/key/image environment, `AGENTCORE_OSB_PROOF_MOUNT_ROOT` and `AGENTCORE_OSB_ORDINARY_IMAGE`. The real SDK command was `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --filter '<focused filter>' --nologo --logger 'console;verbosity=normal' -m:1 -nodeReuse:false`, with protected values supplied only through the existing helper into the child environment. The first OR filter covered `Ordinary_dotnet_image_verifies`, `Real_sdk_rejects_links_and_untracked_children`, `Real_profile_quota_and_concurrent`, `Crash_after_swap`, `Real_sdk_persists_exact_scratch`, and `Instance_setup_runs_after_profile`: **6 passed**. The enhanced ordinary-image test with offline console compilation was then rerun independently: **1 passed**. The final OR filter covered `Real_workbench_fixes_python_tests_runs_node_and_creates_office_bytes` and `Verification_executes_initialization_rejects_broken_scripts`: **2 passed**.

Linux qualification used the installed stock SDK image with no network and a read-only bind of the built test output: `dotnet vstest /tests/AgentCore.Infrastructure.Tests.dll --TestCaseFilter:FullyQualifiedName~SandboxMountFilesTests --logger:console;verbosity=normal`: **9 passed**. This was repeated after the final descriptor/bounded-scan implementation, also **9 passed**. A final `SandboxApiTests` rerun after the storage-scope projection change passed **15 tests**; the earlier 23-test API/self-management run remains recorded above.

The disposable Synthetic Core/Vite processes were stopped and their unique temporary data directory removed. Ports 5280/5373 were confirmed free. Final cleanup check confirmed **0 remote sandbox environments** and **0 staging generation directories**. The existing owned server remains available, with metadata/authentication retained.

## Rollout and boundaries

Restart the native app through the updated launcher so it loads the new private mount-root handoff, then reverify saved profiles and reset legacy Session environments. The real user app and stored profiles were not restarted or silently rewritten by this work. The owned infrastructure mount configuration was updated with no active workloads, preserving its existing metadata and authentication.

Packaged-app topology was configuration-validated; full packaged-app execution, x64 native execution, remote-host filesystem mapping, production isolation and writable-layer/live-stage quota enforcement remain outside the observed evidence. No hosted CI or milestone acceptance was performed.

## Final consistency and recovery review

The follow-up review corrected three concrete issues before commit:

- Mount roots are normalized before rejecting filesystem roots. Both Core-local and Docker-host roots now reject `/`, `/tmp/..`, `/tmp/../` and relative paths before filesystem creation; host volume paths are normalized too.
- A provider DELETE 404 now goes through the same guarded local cleanup as a successful DELETE. Excessive retained workload nesting leaves a durable Terminating receipt and safe pending-cleanup error, and a later reset succeeds after local repair. Previously, the local exception could escape the 404 catch handler. Integration fixtures exercise both 204 and 404 failure/recovery paths.
- Initialization help and the earlier technology-decision paragraph now consistently describe the image default user.

Repeated focused checks passed: Infrastructure **106/106**, API/self-management **23/23**, frontend **28/28**, configuration scripts **9/9**, Linux ARM64 mount-file checks **13/13**, Compose configuration and whitespace validation. The frontend run retained the existing non-failing JSDOM height warning. The stock .NET real SDK test passed again, including no Python, mounted writes, canonical readback, offline console compilation/execution and the optional Workbench compatibility regression.

A fresh disposable Synthetic Playwright MCP journey saved `mcr.microsoft.com/dotnet/sdk:10.0`, observed digest pinning, passed Check image readiness and Verify profile, and retained Verified · eligible after reload. Advanced settings exposed the corrected initialization helper. Saving public `ubuntu:24.04` without credentials returned HTTP 200 and `docker.io/library/ubuntu@sha256:534baea6a22c03a63003dbc8dbe78fe34bc0d7e595d9a9dc9834884ff530eb55`; this establishes public Docker Hub save-time resolution, not an Ubuntu workload execution claim. All related API requests returned 200; browser console errors were zero. Only the disposable hosts/data were removed; final remote environment and staging-generation counts were both zero. Existing user app/data remained intact. x64 execution and hosted CI remain unrun.
