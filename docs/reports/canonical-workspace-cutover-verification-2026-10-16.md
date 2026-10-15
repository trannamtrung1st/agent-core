# Canonical workspace and direct-mount cutover verification

Verified locally on 2026-10-16 (Asia/Ho_Chi_Minh), on `develop/branch-1` from clean baseline `5e58dc81a598e52c72cff1cedf8c3b3f42cdfee6`. This ledger closes the owner-requested 2026-10-15 canonical workspace proposal for the trusted single-user local deployment. It does not reopen historical milestones or claim production/multitenant isolation. The accompanying [source manifest](canonical-workspace-cutover-source-2026-10-16.json) identifies changed runtime, test and deployment bytes and retired files. The final Git commit containing this ledger identifies the complete candidate.

## Delivered behavior

One ordinary filesystem is authoritative: durable Instance `/home` and private Session `/working`. Tools and containers address the same directories. Exact child mounts enforce writable working and RW/RO/absent home; neither a parent owner directory, database nor credential root is a workload mount. Numeric non-root identity, directory ownership, HOME, modes, links, Git locks and bytes in both directions are verified. The existing mailbox owns cwd; an explicit command cwd affects only that command.

File rows/blobs, file IDs/revisions/SHA preconditions, tree CAS/planning, scratch capture/import/finalization and private commit journals are removed. Path-first HTTP, composer home references, Office edits, downloads and the Admin workspace use present files. Mutable home previews load fresh bounded bytes and expose Reload. Immutable Attachment/Artifact/Resource checks, Drive versions/auth epochs, Run/configuration/Profile/lease revisions, exact approvals and non-replayable effects remain independent.

Writes can be partial and remain after nonzero exit, cancellation, timeout, setup failure, process retirement or reset. Reset preserves both trees. Cleanup uncertainty concerns processes, never an unknown file import. Saved restrictive policy fences dispatch and retires obsolete generations; the UI distinguishes EnforcementPending from confirmed retirement. Retirement uses the observed lease generation so a raced replacement cannot be stopped accidentally.

Home and working have separate sampled usage budgets. Working defaults to 250 MiB, with explicit Profile/Instance ceilings bounded by a host maximum of 16 GiB; home has a separate 250 MiB budget and 50 MiB tool-file bound. Unknown/excess usage stops execution and retains bytes. There is no claim of kernel quota enforcement; requiring unsupported hard enforcement fails closed. Application-tool working writes retain their separate 250 MiB aggregate preflight and 50 MiB per-file bounds; larger configured Profile budgets apply to sandbox command writes. Observation scan limits are not an 8,192-entry filesystem validity ceiling.

## Focused local checks

All counts below are observed results, not a sum of unique cases. Some focused rechecks overlap earlier runs. Backend commands use `dotnet test <project> --no-restore -m:1 /nodeReuse:false /p:UseSharedCompilation=false --filter <expression>`; project names below are under `tests/`.

| Boundary | Executed selection | Result |
| --- | --- | --- |
| Real files, owners, mount resolution, structure, templates and deletion | Infrastructure: `CanonicalWorkspaceFilesystemTests`, `CanonicalSandboxMountResolverTests`, `AgentWorkspaceStoreTests`, `WorkspaceStructureTests`, `FileSessionWorkspaceTests`, `UnifiedWorkspacePhysicalLayoutTests`, `OpenSandboxCleanupTests`, `AgentWorkspaceDeletionTests`, `DefinitionPublicationResourceWorkspaceTests` | 64 passed; published-template precedence recheck also passed |
| Fresh baseline/reopen and surviving persistence contracts | Infrastructure: `CanonicalStartupMigrationTests`, `P7EnsureCreatedReopenMigrationTests`, `PinnedPersonaRevisionMigrationTests`, `InstanceSkillsStartupTests`, `MemoryStoreContractTests`, `ArtifactStoreContractTests`, `DrivePersistenceTests`, `GmailOAuthStorageTests`, `SandboxStoreTests`, `SystemCredentialTests`, `AgentRunAdmissionStoreTests` | 211 passed |
| Current tool/schema/policy/reference boundaries | Application final filter: `FullyQualifiedName~Workspace\|FullyQualifiedName~StateIsolation\|FullyQualifiedName~RolePermissions\|FullyQualifiedName~Sandbox\|FullyQualifiedName~UserResource\|FullyQualifiedName~UserMessage\|FullyQualifiedName~Composer` | 88 passed |
| Capability projection | Application: `CapabilityProjectionTests` | 36 passed |
| Additional download/prompt/effect and lifecycle consumers | Earlier focused Application filter and workspace materialization/lifecycle rechecks | 45 passed; initial additional consumer sweep 75/78 passed, all three failed fixtures corrected and focused 13-case recheck passed |
| Domain typed references | Domain filter: `FullyQualifiedName~Workspace\|FullyQualifiedName~UserMessageContent\|FullyQualifiedName~Sandbox\|FullyQualifiedName~StateIsolation` | 5 passed; all Domain test sources compiled |
| HTTP/current bytes, Office, sandbox policy, Drive and publication resources | API final filter: `FullyQualifiedName~Workspace\|FullyQualifiedName~Office\|FullyQualifiedName~WebDownload\|FullyQualifiedName~SandboxApiTests\|FullyQualifiedName~DriveJourneyTests\|FullyQualifiedName~AgentInstanceCleanup\|FullyQualifiedName~Managed_instance_session_reads_publication_resource_under_agent` | 74 passed |
| Frontend files/policy/previews | `pnpm --dir web run test --run` with InstanceWorkspaceSection, SessionSandboxDrawer, fileSource, officeSource, StateIsolationConfigurationSection, DefinitionStateIsolationPolicy and sandboxes test files | 105 passed across 7 files |
| Shared Profile/Instance presentation | Same command with InstanceSettingsSection, InstanceSandboxSummary, SandboxProfilesSection and DefinitionSandboxPolicy test files | 32 passed across 4 files |
| Composer/wire/browser state consumers | Same command with messageParts, sessionStore, Composer and composerChoices test files | 94 passed across 4 files |
| Production frontend | `pnpm --dir web run build` | Passed TypeScript and Vite; existing large-chunk/SignalR annotation warnings remain |
| Conversational Synthetic browser use | `CANONICAL_SYNTHETIC_URL=http://127.0.0.1:5388 CANONICAL_SYNTHETIC_API_URL=http://127.0.0.1:5288 pnpm --dir web exec playwright test --config playwright.canonical.config.ts` | 5 passed, twice; explicit loopback/Synthetic health guard before mutations |
| Selected-path reset and launchers | `python3 -m unittest scripts.tests.test_canonical_reset scripts.tests.test_opensandbox_config scripts.tests.test_dev_real` | 36 passed; reset tests include actual apply on private temporary fixtures |
| Helper syntax | `python3 -m py_compile` for reset, OpenSandbox compose/local/config, server bootstrap and compose acceptance helpers; `bash -n scripts/compose-sqlite-volume.sh`; compile its three embedded Python blocks | Passed |

The five conversational journeys exercised relative cwd, nested binary tree copy/rename, downloads and fresh Artifacts, home survival after deleting the source Session, owner isolation/archive denial, and rejected/accepted structural approval. Expected current bytes were observed through the actual Synthetic application. This is runtime evidence beyond compilation or health responses.

Local logs used during verification are `/tmp/canonical-{focused,baseline,application-final,capability,domain,api-final,web,admin-sandbox,composer,browser,topology-final}-tests.log` and `/tmp/canonical-web-build.log`. They are temporary host evidence, not required runtime assets or committed credentials.

## Native and packaged real containers

The existing authenticated `agent-core-sandbox-opensandbox-1` server and Docker Desktop daemon were reused. No second server was started: upstream reconciliation is daemon-wide. Its existing allowlist was used only for unique disposable proof roots. The old name `workspace-mounts` in that already-running server's host path is not a product compatibility fallback. Provider keys were read privately and never printed or committed.

Native Core ran as macOS ARM64 UID/GID `501:20`; packaged Core ran as Linux ARM64 with the same declared identity. The final packaged image was built with:

```sh
docker build --build-arg AGENTCORE_RUNTIME_UID=501 --build-arg AGENTCORE_RUNTIME_GID=20 -t agent-core-canonical-proof:cutover .
```

Qualified immutable fixture images:

| Image | Local content ID | Evidence |
| --- | --- | --- |
| Identity/ownership-aligned Workbench | `sha256:5a180b3bdfb20fd00e0a5745da6e09ffec37581979d6122c1b5d73ee9a6042d3` | Mount-root and nested Git init/add/commit/branch/offline clone, Python/Node, Office output, same-byte RW/RO/disabled verification |
| SDK fixture with matching declared user | `sha256:774d62cea0c48fd71b2dbf26b1ffe78ea50918a085dbaa29708434cae2bfc1a4` | Ordinary SDK commands and verification without workload Python; packaged RW/RO/disabled verification |
| Final packaged Core | `sha256:ae6fe6d7602725fed99ac198b1beff13f04b01a24a4a9dc201f15077d6b9c10d` | Dedicated canonical bind, separate database/credential data, actual setup and recreation retention |

The SDK fixture is an explicitly configured compatible variant, not a claim that the unchanged root-default stock image works. An image/Core identity mismatch correctly failed MountIncompatible under dropped capabilities. A USER-only Workbench variant allowed writes but failed Git at the mount root because the underlying image mountpoint owner differed. The Workbench Dockerfile and launcher now align both default identity and initial directory ownership; Git passed without `safe.directory` bypass or broader capabilities.

Thirty native real-container cases were exercised across the initial group and focused rechecks. The initial run had nine failures; old sync expectations and a missing recovery recycle flag were corrected, and each failed case passed its recheck (including the long TTL case). Final candidate requalification used the Infrastructure filter `FullyQualifiedName~Canonical_\|FullyQualifiedName~Sampled_canonical_storage\|FullyQualifiedName~Verification_executes_initialization\|FullyQualifiedName~Instance_setup_runs_after_profile`: **21 passed in 2m22s**. It covers immediate shared-home/private-working bytes, mount-root Git, cwd parity, live RW→RO→disabled retirement, failed/background writes and reset retention, sampled excess handling, actual setup order and exact setup failure. The broader earlier cases cover SDK build/run, escaped descendants, lost acknowledgement/crash reconciliation, exact receipt/no replay, profile switch/revocation, idle/hard TTL, cancellation, timeout and orphan cleanup. This is not a claim of one all-30-case final run.

The final packaged proof created fresh isolated SQLite and canonical roots, wrote home through Core, checked the exact host file, modified it externally and read the new bytes through Core. Both compatible images passed direct mount verification. `scripts/tests/opensandbox_compose_acceptance.py` passed authenticated readiness, actual Python/Node/Word/Excel setup, unchanged configuration revision, separate image readiness, bounded failed setup with **exit 7**, persisted exact revision evidence, stale verification CAS rejection and owner authorization. Only the proof Core container was removed/recreated; a new owner capability was acquired and the original external file bytes remained. Its result is `/private/tmp/canonical-package-eebc5614da2c44ee909c30cc31f8996f/result.json`; every recorded boolean is true. The proof Core was briefly reopened for browser inspection and then removed; the user's server and application were preserved.

## UI and design evidence

Playwright MCP exercised the actual disposable Synthetic Admin: folder navigation, an external writer followed by live preview Reload, a controlled 503 followed by Retry, and conversational cwd/home use. At 1440/1024/768/390/320 widths, workspace and preview content remained usable without page overflow. Controlled Session drawer responses exercised EnforcementPending, read-only access, retained partial effects, reset confirmation and Escape/focus restoration; they do not represent actual injected provider failures. Actual lifecycle failures were exercised separately by native container tests.

One bounded polish pass found transient narrow-drawer clipping during viewport changes. The shared drawer now uses `min(640px, 100vw)` with a 100vw wrapper cap. The single confirmation pass measured drawer widths 640/640/640/390/320 and confirmed keyboard recovery. No unexplained console error remained; the intentional 503 and websocket messages during the task-owned host restart were accounted for.

The final packaged UI was inspected without mocked routes. It displayed the real successful RW/RO/disabled mount observation and the deliberately failed setup's bounded `bounded-failure` stderr with exit 7. Profile/Instance descriptions distinguish private working storage, shared home policy, disposable verification roots and per-Session mount checks. The existing Ant Design v6 visual system and unified State & Isolation presets remain.

- [Actual packaged direct-mount observation](assets/canonical-workspace-cutover/packaged-direct-mount-verification.png)
- [Actual setup stderr and exit 7](assets/canonical-workspace-cutover/packaged-setup-exit-7.png)
- [Live home preview at 320px](assets/canonical-workspace-cutover/live-preview-320.png)
- [Controlled reset confirmation at 320px](assets/canonical-workspace-cutover/sandbox-confirm-320.png)

Canonical docs, README, `.agents/context/DESIGN.md` and `.impeccable/design.json` are synchronized with final source. The sidecar records hashes for inspected UI owners. Historical reports and immutable published agent versions remain evidence for their original source; General Assistant v32 provides current-file guidance.

## Fresh-install boundary and retirement inventory

The former migration directory contained 174 files: 173 historical migration sources are deleted and its model snapshot is regenerated. The only executable baseline is `20261015154322_CanonicalFilesystemBaseline` with its designer and snapshot. Seventeen obsolete migration-chain test methods were retired; fresh/reopen and non-mutating refusal tests replace unsupported schema-adoption expectations. Current persistence/Run/credential/Artifact contracts remain covered. No fake migration stamping or compatibility reader remains.

Legacy/untracked/unknown/incomplete database fixtures fail before migration; refusal preserves exact database bytes. Populated unmarked or incompatible workspace roots fail `data_reset_required`. The explicit reset helper requires selected absolute paths, a dry-run set fingerprint and stopped-host confirmation, backs up by same-filesystem rename, and keeps a manifest on partial failure. Temporary-fixture apply tests preserved database/WAL/dotfile bytes and modes. It was **not run on user data**.

The active-source audit finds none of the retired capture/commit/sync/fingerprint/row implementations or old item-ID routes. `AgentWorkspaceItems` remains only as an explicit startup refusal sentinel. Removed expectedRevision/SHA/tree properties remain only in rejection/negative tests; configuration revisions, exact-action hashes, immutable content hashes and Drive versions are separate retained concepts. The SQLite recreation helper now uses current-path routes/pages and exact readback instead of file IDs or tree hashes. Its whole unrelated persistence/CI scenario was not run; changed embedded code was syntax checked and the affected path/recreation behavior ran in the packaged proof.

## Completion and limits

The requested local cutover is implemented and locally qualified. No unresolved functional failure remains in the executed affected checks. No user database/root reset, user application deployment/restart, GitHub Actions dispatch, push or PR was performed. Existing installations require the deliberate [backup/reset procedure](../17-observability-and-operations.md#canonical-workspace-backup-and-reset), a matching declared identity/topology, fresh owners/profiles and contract-3 reverification before using the new build.

Storage enforcement remains sampled; concurrent writers and interrupted effects have ordinary filesystem semantics. Remote Git requires explicitly authorized egress and was not exercised; offline clone passed. Other architectures/filesystems, customer images, private remote Git credentials, public/multitenant hardening and hard filesystem quotas are not qualified by this local run. The complete old `opensandbox-e2e/workbench.spec.ts` server-recreation scenario was updated but not rerun as a whole; actual native lifecycle tests, five Synthetic browser journeys, packaged recreation and real packaged verification inspection provide the scoped evidence above. Historical milestone/hosted acceptance remains unchanged.
