# Tenant Sandbox first-generation provisioning verification — 2026-10-18

This successor to `ac37b64160d8a018e64766b3be5f4681583eff21` connects an exact approved command to first-generation preparation through the existing production SDK adapter. It does not complete the proposal. Full process/reset/cleanup workers, model offering/output delivery, tenant Sandbox UI and other resource/effect families, Governance/platform, voice/rich Chat, live input arbitration, autonomous origins, final authority/schema consolidation and the earlier SQLite preference reliability follow-up remain active. [Delivery ledger](multitenant-iam-delivery-2026-10-16.md).

## Implementation

The approved reservation now atomically creates an owned Provisioning generation, its one execution receipt, active/last pointers, Run InFlight fence and safe audit. A new generation requires the exact saved initialization snapshot and previous lease/generation witness. Existing Ready generations keep their original path. Duplicate reservations cannot create, initialize or dispatch again.

`FileSessionWorkspace` implements a narrow trusted tenant preparation port using the same canonical filesystem and provisioning gate. It rechecks the actual claimed approved operation around I/O and reads only the Run's admitted immutable Template resource hashes. It never consults current global publications or a live host template for tenant preparation. Duplicate template destination paths fail explicitly. Partial copies converge only on identical ordinary bytes; differing retained user bytes are preserved. The `.provisioned` marker stays outside the mounted working tree.

The shared SDK adapter creates the exact tenant/Session/executor/generation environment, denies network egress, binds the canonical roots, checks provider ownership and trusted mount provenance, performs the existing bidirectional/Git readiness probe, records the process baseline and executes exact Profile-then-Instance initialization under one combined profile budget. A provisioning watcher and boundary checks revalidate current requester/executor/grantor, source/secret epochs, parent revisions, admitted/current policy and Run claim throughout setup. Private source authority is rechecked before secret release and again after the registry probe immediately before create.

`TryStartApprovedSandboxCommandAsync` is a specialized owning-store transaction with current authority and exact receipt/lease/Run/security witnesses. Only successful initialized mount facts can start the command. It sets immutable `CommandStartedAt` and the exact saved command deadline once while moving Provisioning to Executing. Generic factual writes cannot mint this transition. Receipt JSON fields extend the existing owned tables without a migration or rewriting old payloads. No legacy Profile, global ownership fallback, second executor, queue or dispatch table was added.

Failure retains bounded initialization/receipt facts and possible filesystem effects. Exact returned provider metadata is checked before use and deletion. Unknown create outcomes and foreign metadata retain reconciliation fences; no retry reruns setup or a command. Setup and authority failures now use the existing truthful mount/provisioning/source-authority categories, and failed initialization retains its explicit reset-required cause. Full durable orphan discovery and lifecycle workers remain separate work.

## Verification

The first integrated successful use case passed in both stores, followed by 12 setup/authority failure cases. The expanded focused run passed **24 tests** (51 s), adding ordered Profile/Instance initialization and concurrent generation/start fences. After final start guards were tightened, the affected owned-agent/Sandbox/workspace/persistence regression passed **417 tests** (5 m 49 s). These counts overlap.

The focused Synthetic HTTP/Runtime regression passed **12 tests** (47 s), including actual host composition, scoped stored Sandbox observations, exact command Review/decision, sparse Instance setup and original-conversation/persona/SQLite continuation. API and Infrastructure test projects built successfully.

The final SDK/provisioning/cleanup set passed **65 cases**, with four new private-registry cases initially rejected by the canonical layout guard because the test fixture placed protection keys under its workspace root. The fixture now keeps keys in a separate temporary root; the focused rerun passed all **4 cases** (13 s). All **69 selected cases** therefore have passing final evidence across those runs, including **28 new provisioning/start cases**. No product guard was relaxed and no application source changed after the 65-case run.

| Exercised flow | Expected and observed result |
| --- | --- |
| First generation with pinned template and two reviewed scripts | Immutable admitted bytes seeded once; Profile then Instance script precede one exact command; SDK ownership/mount metadata match; command deadline starts after setup; final receipt/Run effect is Completed and SQLite reopens unchanged. |
| Either initialization script fails or authority is revoked during setup/create | No approved command is sent; bounded setup/effect facts remain; exact returned owned handle is retired; consent never becomes a claimed successful effect. |
| Mount refusal or foreign created metadata | No initialization/command dispatch; mount failure keeps its truthful category; foreign ownership forbids DELETE and retains reconciliation. |
| Lost create response | Unknown provider outcome remains NeedsReconciliation; repeating the saved operation sends no new create/init/command request. |
| Occupied template destination | Differing retained bytes are preserved; no provider create or command is sent. |
| Concurrent reservation and start | One new generation/receipt/audit winner; no start before initialization or linked provider; factual CAS cannot set command start/deadline; one start winner; changed parent or revoked executor blocks start. |
| Private registry release and post-probe rotation | Actual protected synthetic secret reaches the loopback registry/SDK only while authorized; rotation during HEAD prevents every SDK create. Stored facts, results and audit exclude original/rotated secrets and private provider bodies. |
| Existing Ready SDK and shared cleanup | Existing completion, background acknowledgement, uncertainty, revocation/deletion/cancellation, foreign/absent provider and cleanup behavior remain covered. |

Changed local Markdown links/fences and `git diff --check` passed. Verification is focused local evidence; no milestone or full-proposal acceptance is claimed.

Commands (native .NET, no hosted/provider calls):

```sh
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~TenantDefinitionIntegrationTests|FullyQualifiedName~OpenSandboxCleanupTests|FullyQualifiedName~FileSessionWorkspaceTests|FullyQualifiedName~DefinitionPublicationResourceWorkspaceTests|FullyQualifiedName~CanonicalSandboxMountResolverTests|FullyQualifiedName~SandboxFilesystemPreparationTests|FullyQualifiedName~SandboxStoreTests|FullyQualifiedName~TenantSandboxRuntimeMigrationTests|FullyQualifiedName~OciSandboxAuthorityTests' -v minimal
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~Tenant_Sandbox_|FullyQualifiedName~Owned_instance_sandbox_HTTP|FullyQualifiedName~Model_prepares_durable_persona_approval|FullyQualifiedName~Model_approval_and_exact_checkpoint_survive_SQLite|FullyQualifiedName~Original_conversation_receives_saved_wait' -v minimal
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~Tenant_Sandbox_first_generation|FullyQualifiedName~Tenant_Sandbox_new_generation_and_command_start|FullyQualifiedName~Tenant_Sandbox_private_source_is_rechecked|FullyQualifiedName~Tenant_Sandbox_approved_command_crosses_real_SDK|FullyQualifiedName~Tenant_Sandbox_SDK_keeps|FullyQualifiedName~OpenSandboxCleanupTests' -v minimal
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~Tenant_Sandbox_private_source_is_rechecked' -v minimal
```

## Unrun boundaries

The controlled authenticated loopback peer exercises the installed Alibaba.OpenSandbox 1.1.1 protocol with actual owning services/stores and real disposable filesystem paths. It emulates mount probe writes and shell results. It does not execute a Linux shell, attest Docker bind topology, exercise live registry credentials or establish production tenant isolation. No user data was reset or Real host restarted. Full process observation/stop, reset/profile switching, restart/orphan cleanup workers, model offering/output delivery and tenant UI remain unaccepted. Hosted CI and full local CI were not requested or run.
