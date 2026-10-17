# Tenant Sandbox SDK execution verification — 2026-10-18

This successor to `3ff4f005592377eed8b20f091e265025b930f1e1` connects saved exact Sandbox Review/reservation to the existing production SDK adapter, external-effect continuation and immutable effect settlement over an existing owned Ready generation. It does not complete the full proposal. Initial provisioning/review binding and tenant workspace preparation, complete process/reset/cleanup workers, model offering and tenant Sandbox UI remain required, alongside other resource/effect families, Governance/platform, voice/rich Chat, live input arbitration, autonomous origins, final authority/schema consolidation and the prior SQLite preference reliability follow-up. [Active delivery ledger](multitenant-iam-delivery-2026-10-16.md).

## Implementation

`ITenantSandboxCommandExecutor` accepts only a trusted current Runtime frame and saved request ID. The adapter uses the existing Session gate and atomic approved reservation; only the new reservation dispatches. A duplicate reads or settles existing facts without another provider call. The command, profile and parameters come from the saved typed operation, never a new caller payload.

`TenantSandboxExecutionAuthority` rechecks actual Run/Session/executor/requester, qualified current grantor, exact decision and action hash, current source epochs, admitted/current setup/home and exact Instance/persona witnesses. The existing storage watcher repeats these checks during execution. SDK timeout and relative cancellation stay within the saved command/profile ceiling; current claim renewal is checked dynamically without extending the fixed command deadline.

The shared command streaming/finalization implementation now serves both canonical tenant and existing legacy callers. Tenant reads and factual CAS writes explicitly use the owned store. No legacy Profile, global store fallback, second execution engine, scheduler or dispatch table was added. Actual canonical directory identities/cwd are checked before SDK work. Connection and tenant cleanup require exact provider tenant/Session/executor/generation metadata. Confirmed 404 absence settles cleanup; foreign metadata forbids deletion and retains reconciliation state.

A narrow exact technical-evidence read preserves owned receipt, lease and cancellation facts after logical Session deletion, independently of current human/Runtime read authority. Immutable operation receipt, matching still-live Run effect state and safe audit settle under existing store transactions/gates. Cancelled or retired Runs stay retired. Foreground completion requires bounded exit and confirmed quiescence/retirement. A background launch requires a complete error-free acknowledgement and remains a Running process; it is not reported as process completion. Incomplete/error launch streams, lost responses and possibly executed work remain uncertain and cannot replay.

The existing shared-capacity external-effect preparation invokes the executor before mailbox dispatch. Ordinary continuation consumes its saved operation receipt. Model Sandbox authorization offering and output delivery are not enabled by this change; those require the remaining provisioning/lifecycle integration.

## Exercised use cases

The focused use-case run passed **26 tests** (53 s), with each scenario executed through actual owned Definition/Instance/Session/Run/Review services in InMemory and temporary SQLite. A real installed Alibaba.OpenSandbox 1.1.1 client talks to an authenticated disposable loopback Kestrel server. Exact provider responses, compatibility/readiness and the initial Ready generation are controlled fixtures. Canonical directories and UID/GID observations are real disposable filesystem facts. The fixture does not execute a Linux shell or attest an actual Docker bind.

| Scenario | Expected and observed |
| --- | --- |
| Foreground command through external-effect preparation, then continuation | Exact argv/cwd/UID/GID and bounded timeout reach the SDK endpoint once. Streamed output and exit/quiescence evidence persist; operation outcome is Completed and live Run effect is Succeeded. Ordinary continuation consumes that same result. No legacy Profile is assigned. |
| Repeated observation and SQLite reopen | Same execution/result survives; no further provider contacts or command dispatch. Global storage cannot read the owned receipt. |
| Background acknowledged launch | Result remains Running; the completed operation reason is `sandbox_command_started`. The lease retains its active process. |
| Background incomplete or error stream | No successful-launch receipt is fabricated. Owned cleanup is attempted and the operation remains Indeterminate. |
| Foreign environment metadata, executor revocation after reservation, or changed Instance/persona witness during connection | No user command or cleanup DELETE occurs; durable result is Denied with `sandbox_command_not_dispatched`. |
| Executor revocation during the command | Current authority check stops the SDK flow; exact owned cleanup and uncertain factual result persist without repeating the command. |
| Caller cancellation | Cleanup/result settlement uses its separate bounded technical path before cancellation returns. Stored state is Canceled with an Indeterminate possibly executed effect. |
| Run cancellation and logical Session deletion during SDK I/O | Current Runtime reads retire, but exact technical facts, cancellation, owned cleanup and immutable uncertain result persist. Another dispatch remains denied. |
| Lost command response | One request returns a controlled provider failure; the result stays Indeterminate, cleanup is recorded and no retry occurs. Provider body, command, output and synthetic server key stay out of audit. |
| Foreign cleanup metadata | No DELETE targets that provider. Termination stays unconfirmed and `NeedsReconciliation` is returned with recovery guidance. |
| Confirmed provider absence | Exact 404 inspection confirms absence without another DELETE or command. The uncertain command result remains immutable. |

The first generation fixture initially tried to assign a provider ID during reservation and was corrected to record it as a later fact, preserving the existing invariant. The foreign-cleanup scenario exposed a missing environment-state projection; the shared result now returns reconciliation evidence. Review also tightened exact parent witnesses and rejected incomplete/failed background launch acknowledgements. Initial build-only property/import errors were corrected before the passing use-case run.

```sh
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --filter 'FullyQualifiedName~Tenant_Sandbox_approved_command_crosses_real_SDK|FullyQualifiedName~Tenant_Sandbox_SDK_keeps' --nologo
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~TenantDefinitionIntegrationTests.Tenant_Sandbox_|FullyQualifiedName~Tenant_sandbox_runtime|FullyQualifiedName~TenantOperationAuthorizationTests|FullyQualifiedName~TenantPreferenceOperationTests|FullyQualifiedName~TenantCredentialIntegrationTests|FullyQualifiedName~OpenSandboxCleanupTests|FullyQualifiedName~SandboxStoreTests|FullyQualifiedName~OpenSandboxScriptTests' --nologo
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --filter 'FullyQualifiedName~Tenant_Sandbox_command_approval_HTTP|FullyQualifiedName~Canonical_durable_approval_HTTP|FullyQualifiedName~Credential_approval_HTTP|FullyQualifiedName~Original_conversation_receives_saved_wait|FullyQualifiedName~Tenant_Sandbox_HTTP|FullyQualifiedName~Model_prepares_durable_persona_approval|FullyQualifiedName~Model_approval_and_exact_checkpoint_survive|FullyQualifiedName~Canonical_tenant_worker_completes|FullyQualifiedName~Tenant_Runtime_uses_current_adopted_policy' --nologo
```

## Migration recovery and final focused checks

The first shared regression ran **545 tests**: 544 passed and an older credential migration fixture failed with a missing newer Sandbox column. EF had correctly completed safe newer Down steps before the older Credential migration refused a lossy downgrade; the fixture then incorrectly used the current EF model against that intermediate schema. The correction reads the retained credential payload directly and confirms it is unchanged, confirms the protected older migration remains applied, then forward-migrates before using the current model. No product migration or user data was changed.

The focused recovery run passed **31 tests** (58 s): all 26 SDK cases, the repaired credential migration case, existing expired-Ready rejection and both-store expired-Terminated review preparation. Expired active/Ready generations still refuse execution; a terminated generation may prepare a new initialization Review without dispatch. The full new-generation provisioning path remains pending.

The final shared Infrastructure regression passed **547 tests** (4 m 8 s), including all SDK cases, the corrected credential migration/recovery case, new terminated-generation preparation cases and existing operation authorization/preferences, Credential continuations, Sandbox stores/scripts and legacy cleanup. No failures or skips remained in this focused set.

The focused Synthetic HTTP/Runtime regression passed **20 tests** (59 s), including actual service composition, durable approval/credential/original-conversation flows, stored Sandbox projection, persona/SQLite continuation, worker completion and current-policy/pinned-payload behavior. Infrastructure and API test projects built successfully. Added local Markdown links/fences and `git diff --check` passed. Counts refer to separate overlapping focused runs rather than full CI.

## Remaining boundaries

Initial generation creation, ordered initialization under exact review, registry secret release at that boundary, tenant workspace preparation, full background process observation/stop, reset, profile switching and restart cleanup/recovery workers still require integration. A Ready fixture is not proof of those paths. The model has no new Sandbox authorization offering or command-output continuation contract yet, and no new tenant Sandbox UI is accepted.

No frontend source or presentation changed, so no new browser or visual acceptance is claimed. No Docker daemon/container, external registry/provider credentials, hosted CI, full local CI suite, user database migration/reset or Real-host restart was used. All provider traffic in these checks is loopback. No new migration was generated. Arbitrary shell review approves exact bytes within the bounded environment; it does not prove the semantic success of privileged shell work. The full goal remains active.
