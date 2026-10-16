# Canonical workspace stabilization — 2026-10-16

This focused pass addresses the review of `8636704a`. Verification uses committed baseline `5c55f125` plus the stabilization files. Concurrent, unrelated Automation work in the shared checkout was preserved and excluded from this change. When those edits temporarily prevented compilation, final backend verification used `/tmp/agent-core-stabilization-source`, exported from that baseline with only these implementation/test changes applied.

## Changes and observed behavior

- Definition fixtures now expect General Assistant v32. Sandbox fixtures retain explicit refusal of retired contracts 1/2 and admit the current contract 3. Missing mount configuration expects `MountIncompatible`; read-only inspection remains configured.
- Registry rotation still invalidates verification and denies the admitted old secret epoch. Added assertions prove that even **after current-epoch reverification**, the old Run pin remains denied while a new pin receives the rotated value, including after reopening the SQLite credential adapter. No production credential validation changed.
- Historical P7 migration-history manipulation was replaced by `CanonicalSchemaRefusalTests`: fresh canonical baseline, deliberate missing/malformed table, incompatible nullability or missing index, and reset-required refusal without repair. Existing fresh-baseline/reopen, byte-preserving legacy refusal and explicit backup/reset coverage also passed. No historical upgrade path was restored.
- Verification and generation provisioning now check actual mounted-root owner and group using the production shell gate after checking command UID/GID. RW and RO `/home` are checked; disabled home is omitted. A mismatched writable root fails at `mount_ownership` before initialization. Real shell tests exercise owned/foreign roots and all three home policies.
- The DOCX browser regression matches the query-bearing home preview endpoint, requires exactly one injected failure, checks safe recovery at 1440/390, retries successfully and compares original download bytes before failure, during failure and after recovery. Product preview code did not change.
- An opt-in benchmark measures both canonical trees with two committed 12,000-file repositories and loose Git objects. The existing 250 ms delay, single-tree sampling implementation, scan bounds, cancellation and over-budget refusal remain unchanged.

## Runtime evidence

| Check | Expected and observed | Result |
| --- | --- | --- |
| Focused Infrastructure cases on macOS ARM64 | Catalog, credential epochs, canonical schema refusal/reopen, shell ownership, mount scoping, command permissions and script contracts; benchmark's one-byte budget rejects the populated tree | 75 passed |
| Same compiled focused cases on Ubuntu 24.04 ARM64 | Same cases through Linux shell, native filesystem/stat layouts and SQLite | 75 passed |
| Real OpenSandbox SDK from native Core tests | Foreign translated mount refused as `MountIncompatible` at `mount_ownership`; initialization sentinel absent; disposable verification directories removed | 1 passed |
| Real OpenSandbox SDK from Linux ARM64 Core tests | Git init/add/commit/branch/clone at both a project directory and `/working`; shared home writes visible across Sessions/Core; working trees private | 1 passed |
| `python3 -m unittest scripts.tests.test_canonical_reset -v` | Dry run preserves bytes; changed sources invalidate confirmation; explicit apply preserves selected DB/WAL/root bytes in backup; links/overlaps/existing backups refused | 3 passed |
| Synthetic `office-cross-feature.spec.ts` | Inert hyperlinks; exactly one controlled error; no private exception rendering; no horizontal overflow at 1440/390; Retry restores preview; downloads byte-identical throughout | 1 passed |

Backend command in the isolated source:

```sh
AGENTCORE_STORAGE_SAMPLING_BENCHMARK=1 dotnet test tests/AgentCore.Infrastructure.Tests \
  --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false \
  --filter 'FullyQualifiedName~AgentDefinitionStoreTests|FullyQualifiedName~OciSandboxAuthorityTests|FullyQualifiedName~OpenSandboxReadinessTests|FullyQualifiedName~CanonicalSchemaRefusalTests|FullyQualifiedName~CanonicalStartupMigrationTests|FullyQualifiedName~PinnedPersonaRevisionMigrationTests|FullyQualifiedName~MountOwnershipProbeTests|FullyQualifiedName~CanonicalSandboxMountResolverTests|FullyQualifiedName~SandboxCommandPermissionsTests|FullyQualifiedName~OpenSandboxScriptTests|FullyQualifiedName~CanonicalStorageSamplingBenchmarks' \
  --logger 'console;verbosity=detailed' --nologo
```

The Linux run uses `dotnet vstest` with the same test filter and compiled output inside cached image `sha256:a1fa5e1b876e5f690ef9677123a9a6be89c23f0bd0ab7b5801eb47b7d9190ccc`, user `501:20`, capabilities dropped, no-new-privileges and a read-only source mount. The ordinary focused run has networking disabled. The separately enabled SDK regression uses `AGENTCORE_OPENSANDBOX_TESTS=1`, `AGENTCORE_OSB_EXPECT_FOREIGN_MOUNT_ROOT=1`, the existing protected local authentication and disposable allowed owner paths. Its first Linux setup attempt used a non-loopback HTTP origin and was refused by the existing endpoint guard; rerunning in the server's private loopback namespace reached verification without changing that guard or server configuration. Linux-created roots passed ownership verification, so the deliberately negative expectation was inapplicable; the positive Git journey is checked separately.

Browser command, from `web/`, used free separate ports and disposable data:

```sh
CI=1 PLAYWRIGHT_FAITHFUL_MANUAL=1 PLAYWRIGHT_API_PORT=5098 \
PLAYWRIGHT_WEB_PORT=5188 PLAYWRIGHT_FIXTURE_PORT=5198 \
PLAYWRIGHT_SQLITE_PATH=/tmp/agent-core-office-stabilization/core.db \
Persistence__WorkspaceRoot=/tmp/agent-core-office-stabilization/workspaces \
Persistence__AttachmentRoot=/tmp/agent-core-office-stabilization/attachments \
Persistence__ArtifactRoot=/tmp/agent-core-office-stabilization/artifacts \
Persistence__CredentialProtectionKeyRoot=/tmp/agent-core-office-stabilization/keys \
pnpm exec playwright test e2e/office-cross-feature.spec.ts --project=synthetic --reporter=line
```

This is Playwright CLI evidence. Playwright MCP launched, but its subsequent navigation reached the disposable host after CLI teardown and returned connection refused; no MCP interaction pass is claimed. Existing API/Vite processes and user data were left intact.

## Sampling measurements

Each root contains 12,000 tracked files plus Git metadata, approximately 24,500 entries and 8.4 MB per root. Results are elapsed milliseconds, ten recorded samples after the first; p95 is the nearest-rank value. Git/file caches were not flushed. These are filesystem-only measurements, excluding Session/Run store and SDK latency.

| Platform | Check | First recorded | Warm median | p95 |
| --- | --- | ---: | ---: | ---: |
| macOS 27.0.1 ARM64 | `CheckUsage`, both trees | 195.91 | 194.29 | 204.88 |
| macOS 27.0.1 ARM64 | Root resolve + `CheckUsage` | 197.65 | 195.18 | 203.74 |
| Ubuntu 24.04 ARM64, container filesystem | `CheckUsage`, both trees | 110.92 | 88.44 | 111.10 |
| Ubuntu 24.04 ARM64, container filesystem | Root resolve + `CheckUsage` | 79.46 | 74.40 | 88.29 |

The scan cost is material and depends on filesystem/cache conditions. The watchdog awaits each scan before its next 250 ms delay, so scans within one watcher do not overlap and actual intervals include scan time. These observations do not justify a cross-host cadence change or imply hard quotas. Both-root bounds and failure-closed enforcement remain in place.

## Remaining qualification boundary

The reported Docker Desktop mismatch is reproduced: a directory created with UID/GID `501:20` is reported as `0:0` at the workload mount root, although child writes succeed. Git rejects a repository initialized at that root as dubiously owned. A scoped ownership-only experiment on the disposable directory did not repair the translated root. The new probe correctly refuses that incompatible translated root; it does **not** make that particular mapping Git-compatible. Separately, Linux-created owner roots on the tested topology passed the same probe and the real root/project Git commit, branch and clone journey. No blanket `safe.directory`, world-writable mode, added workload privileges, automatic ownership mutation or new storage architecture was introduced.

Focused positive real sandbox/Git qualification passed with Linux ARM64 Core and the pinned identity-compatible Workbench. Other Linux topologies and hosted Linux AMD64 remain unqualified; these focused cases do not establish full sandbox acceptance. The owner explicitly selected focused local checks only; no hosted workflow, full local CI suite, deployment or milestone acceptance was performed or renewed. Historical CI outcomes remain historical.
