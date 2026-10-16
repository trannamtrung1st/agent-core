# Automatic Sandbox filesystem compatibility — 2026-10-16

Requested focused enhancement from main `6c0dc00a`; historical milestone acceptance is unchanged. CI was not requested or run.

## Original failure and correction

Reproduced on macOS 27.0.1 / ARM64, Docker Desktop engine 29.8.2: the non-root workload was `501:20`, while the bind root reported `0:0`, mode `0700`. The original `test -O /working && test -G /working` failed at `mount_ownership`, despite successful write/rename and immediate Core readback. Treating translated Desktop root metadata as authoritative Linux ownership caused the false rejection.

Core now prepares exact managed roots through no-follow descriptors, checks effective host ownership, and makes one idempotent 0700 mode repair where authorized. It preserves contents, descendant modes, Git metadata and file modification times. It does not recursively chmod/chown or touch foreign owners. Newly created directories receive exact private modes independently of platform ABI/umask.

The existing trusted server selects its deployment’s non-root numeric USER before execd starts, preserving the selected immutable image, drop-ALL capabilities and no-new-privileges. This supports tested differing image defaults without rebuilding or silently switching images. Original image ID/USER are recorded in container labels. A private authenticated endpoint attests the actual image/generation, execution user, exact daemon bind sources and RO policy. Core independently verifies bidirectional bytes, read/write/rename/delete, private modes, symlinks/hardlinks, cross-root refusal and Git init/add/commit/branch/clone in disposable paths.

Native Linux ownership remains strict. Only positively identified Docker Desktop with translated `0:0` roots can pass the exception, after the other checks. Observed translation enables exact command-local Git trust for mounted roots and owned discovered repositories. There is no wildcard/global safe-directory mutation. Repository discovery is bounded to 8,192 entries and depth 128; exhaustion leaves undiscovered repositories under Git’s normal protection rather than widening trust.

Profile verification contract **4** invalidates earlier observations. Generation authority also records the contract; older JSON defaults to 0 and requires confirmed retirement/reprovisioning. Workspace API contract **3** and canonical files are unchanged. Preparation precedes initialization/user dispatch; no automatic reprovisioning, image switch or uncertain command replay occurs. Incompatibility reports safe stage/diagnostic guidance and warns that an identical reset is not a remedy.

## Executed evidence

- Host focused .NET filesystem/security/authority/receipt/cleanup checks: **95 passed**.
- Linux ARM64 SDK container: **30** filesystem/preparation/strict ownership/security checks passed using the compiled fixture, read-only source mount, non-root USER, no network and dropped capabilities. Direct VSTest bypassed an SDK first-run `NuGet-Migrations` mutex failure. This is Linux filesystem evidence, not native Linux-daemon OpenSandbox acceptance.
- Sandbox HTTP integration: **16 passed**; Python trusted-adapter/launcher regressions: **17 passed**.
- Sandbox Admin Vitest: **15 passed**. Production frontend build passed with existing Rollup annotation/chunk-size warnings; Vitest emitted an existing jsdom NaN-height warning.
- Playwright MCP against isolated Synthetic API/Vite: opened Add prebuilt image, expanded initialization settings, checked automatic-preparation help and absent UID/GID fields, then canceled without creating a profile. At 1440/768/390 widths, dialog fit and document had no horizontal overflow; browser console had no errors. Existing Ant Design form/disclosure/status owners were preserved.
- Authenticated-server boundary: unauthenticated mount attestation returned **401**.

Real OpenSandbox on Docker Desktop ARM64 passed eight focused cases: translated-root Profile verification across RW/RO/disabled Home; offline Python digest project; mismatched daemon backing-path refusal; shared Home/private Session working/Git; restrictive live Home policy retirement; unchanged stock .NET SDK image verification/build/run; exact receipt/reconnect/reset/cancellation without replay; and owned orphan reconciliation. The final digest rerun also proves that a generation with an older compatibility contract is retired before new dispatch while files remain intact.

The Python case creates only disposable `/working/binance-digest`, runs `python3 -m unittest discover -s tests` (one deterministic test, OK), then `python3 -m binance_digest --format markdown -o reports/digest.md`. The report is immediately read through Core’s workspace adapter and tool executor. Git init/add/commit/branch/status work directly at `/working`, `/home`, and the nested project, including subsequent commands. Reset creates another generation while retaining the report/repositories; duplicate receipt readback leaves a single `x` write. This is an offline compatibility fixture, not a Binance network/data acceptance claim.

The mismatch case preserves preexisting `retained=original`, records `mountFailure`, confirms owned-generation cleanup, and observes neither initialization nor user-command effects. Correct mapping plus explicit reset permits an independent later command; no earlier command is replayed.

## Platforms, deployment and remaining limits

The verified images are the installed Workbench (`sha256:5a180b3bdfb20fd00e0a5745da6e09ffec37581979d6122c1b5d73ee9a6042d3`, declared `501:20`) and the installed stock `mcr.microsoft.com/dotnet/sdk:10.0` source `sha256:0d99ebf1ccad15037b5c98c9fe8979d6b8dbac23812feae2277a0b277b5c5ffa` (root default). No third-party image was rebuilt/replaced. The Workbench Dockerfile account alignment change was reviewed but its heavyweight image was not rebuilt for these tests.

Native Linux-daemon OpenSandbox, packaged Core under the new adapter, hosted AMD64 and other filesystems/images remain unqualified. Hosted/full CI was not run. Images lacking shell/proc/Git/stat/utilities, requiring privileged startup or inaccessible image-local directories, foreign-owned roots, unknown mount topologies and strict kernel quotas remain unsupported. Arbitrary-image support and production multi-tenant isolation are not claimed.

The confirmed-idle project-owned trusted server was intentionally recreated/reloaded for acceptance with authentication/metadata/storage retained. Existing Core applications and user project/Session files were preserved; tests used separate GUID namespaces and disposable SQLite/workspaces. The known user Core host was not restarted. Deployment requires matching API/UI/server and contract-4 Profile reverification; existing canonical data requires no reset. Only processes started for this task are stopped after UI checks.

## Focused commands

```sh
python3 -m unittest discover -s scripts/tests -p 'test_*sandbox*.py'
dotnet test tests/AgentCore.Api.Tests --no-restore --filter FullyQualifiedName~SandboxApiTests -p:UseSharedCompilation=false
pnpm --dir web exec vitest run src/features/admin/SandboxProfilesSection.test.tsx
pnpm --dir web build
```

The 95-test Infrastructure filter combines `SandboxFilesystemPreparationTests`, `MountOwnershipProbeTests`, `CanonicalSandboxMountResolverTests`, `OciSandboxAuthorityTests`, `CanonicalWorkspaceFilesystemTests`, `SandboxCommandPermissionsTests`, `SandboxStoreTests` and `OpenSandboxCleanupTests` with `FullyQualifiedName~...` OR terms, `--no-restore -p:UseSharedCompilation=false`. A final focused cleanup lookup regression passed separately. Real SDK fixtures use `AGENTCORE_OPENSANDBOX_TESTS=1`, the existing private server/key, its dedicated allowlisted mount root and installed immutable image IDs; no provider key or network data access is involved. Local documentation checks verified 721 relative file links and balanced code fences; `git diff --check` passed.
