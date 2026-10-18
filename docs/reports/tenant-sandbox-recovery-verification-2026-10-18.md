# Tenant Sandbox recovery verification — 2026-10-18

This successor to `0f41491fb175fb9c25ffe35df86939e566c3f251` integrates retained tenant generations into the existing SDK worker, background observation and exact provider retirement. The full proposal remains active. Explicit tenant process Stop/reset/profile switching, model offering/output delivery, tenant Sandbox UI, other resource/effect families, Governance/platform, voice/rich Chat, live input arbitration, autonomous origins, final authority/schema consolidation and the earlier SQLite preference reliability follow-up remain open. [Delivery ledger](multitenant-iam-delivery-2026-10-16.md).

## Findings and implementation

The global Sandbox inventory correctly hid tenant leases, so the existing worker could not recover them. `ReadTenantSandboxRecoveryPageAsync` now supplies a bounded technical keyset cursor over actual retained owned roots, including logically deleted Sessions and terminal result settlement. It grants no human inventory or new effect authority. The SDK worker uses its existing Session gates, typed factual CAS and immutable result transactions. Cursor progress is scheduling fairness; it cannot authorize a command or recreate a generation. A bounded tenant pass leaves the existing worker's remaining budget available to other cleanup.

`TenantSandboxGenerationAuthority` permits retaining an idle generation or observing a previously acknowledged background launch after its Run completes. It checks actual current Session/Instance/executor/requester/grantor, exact original Review/result/root, source epochs, current policy, mounted ownership and the fixed deadline. It cannot claim a Run, initialize, launch or replay. Status/log captures are revalidated after provider I/O, including while the process is still running; captured output is discarded when authority changes.

Background SDK `init` now records `launchPending`; only an error-free complete acknowledgement promotes the receipt to `execution`. Recovery cannot mistake init-only evidence for a successful launch. A process's eventual completion/retirement remains distinct from the immutable original launch outcome. Stranded foreground/setup work retires without replay; confirmed saved completion can settle after cleanup, while uncertain work remains Indeterminate. Factual settlement still works after cancellation/deletion without reviving its Run.

Inspection of the actual installed [pinned server image](../../scripts/opensandbox-proof/images.json) confirmed that provisioning runs in a background thread, while list discovers containers only after allocation. A lost Core reply followed by an empty list therefore cannot prove that creation stopped. The existing trusted-server adapter now maintains a negative tenant/Session/executor/generation fence in its existing private persistent provider state volume. It commits retirement before waiting for the existing provisioning call, denies later provisioning and deletes only exact owned containers through the existing Docker provider service. Scope changes, pending work, partial cleanup and foreign metadata cannot produce a retirement proof. Large owned batches make bounded progress across passes. Provider errors retain their original semantics; only fence validation maps to the retirement error. Tombstones survive reopen; they confer no IAM, approval or Core effect authority and add no second execution engine.

Final review also found that provider handles are relative to their configured endpoint/workspace mapping. New owned leases now pin the existing trusted host fingerprint in immutable JSON `ProviderHostScope`; factual CAS cannot add or change it. Effects/cleanup reject a changed or unknown scope before any provider request. In particular, an unrelated 404 cannot falsely confirm retirement on the original host. Retained unbound data stays unresolved rather than being adopted.

Core accepts only a bounded exact-scope versioned retirement proof for a lost provider handle. New tenant provisioning first checks the authenticated capability endpoint, before tenant workspace or SDK create effects. An older server fails safely. The local runner refuses to start/reuse an existing owned runner missing the new mount before changing it. Both runner and Compose mount the adapter read-only and retain the existing provider state volume. No running Real host was restarted.

Review also found that the mount adapter accepted only a 32-character verification owner while tenant verification already emitted `tenantN.attemptN`. It now prepares/attests that exact composite verification identity while continuing to reject composite runtime generations. This corrects the existing tenant-verification boundary without weakening mount/source/identity checks or changing the image contract.

## Exercised use cases

The representative stranded-command flow passed in both stores. The expanded recovery run passed **34 tests** (1 m 16 s), using actual owned Definition/Instance/Session/Run/Review/result services and the installed SDK against an authenticated loopback peer. A subsequent preflight/paging/first-generation run passed **28 tests** (1 min). Counts overlap and precede the final affected regression run recorded below.

| Flow | Expected and observed |
| --- | --- |
| Stranded reserved command; new service/reopened SQLite | Exact owned provider retired, original receipt/result settled, no command/init replay. |
| No dispatch versus completed-but-unsettled | No-dispatch result is Denied; confirmed saved completion settles Completed; terminal result remains stable. |
| Foreground completion awaiting cleanup | Exact retirement confirms quiescence without dispatching anything and preserves the saved exit/result. |
| Acknowledged background after Run completes | Running process remains observable within its deadline; actual status/log response can complete it and return the generation to Ready. |
| Init-only background evidence | No launch acknowledgement inferred; the stranded generation retires and records uncertainty. |
| Executor/source revocation, deadline, foreign metadata | Owned process retires when required; foreign provider is never deleted; original launch outcome remains immutable. |
| Lost actual SDK create reply | Pending/foreign retirement proof keeps NeedsReconciliation; matching proof retires the generation. No repeated create/init/command. |
| Deleted Session | Technical recovery finds retained ownership, retires its provider and settles facts; human/current Runtime reads remain unavailable. |
| Unsupported old server | No tenant working directory, SDK create or initialization; exact action records no-dispatch denial. |
| Technical paging | Stable bounded canonical keyset iteration over actual retained owned roots; no exposure through the global catalog. |
| Host/mapping change | Factual scope mutation is rejected; no request is sent to the changed provider, even when its controlled response would be 404; reconciliation remains pending. |
| Revocation during status I/O | Captured status/log output is discarded and the owned background generation retires without rewriting its original launch outcome. |

The deterministic Python provider-fence/mount/config set passed **25 tests**. Real temporary SQLite and explicit thread barriers cover retirement before entry, while provisioning runs, after create, durable reopen, scope collision, foreign cleanup and bounded/partial cleanup. The pinned-image ASGI proof passed with the real installed FastAPI/Pydantic, authentication middleware and Docker provider hook against a controlled Docker peer. It rejects unauthenticated/extra-field requests, enforces capability/retirement scope and refuses a late create. The disposable proof container had networking disabled, a read-only root, no daemon socket, no host ports and no user data. [Reproducible command](../../scripts/tests/README.sandbox-retirement.md).

Sandbox Compose `config --quiet` passed with synthetic paths and no project env file; no service started and no key was read. The affected Infrastructure regression passed **462 tests** (7 m 25 s) and Synthetic HTTP/Runtime passed **12 tests** (48 s). After host binding and final retained-root/provider-fence corrections, the focused recovery/provisioning run passed **58 tests** (2 m 3 s). Final host-bound Sandbox regression passed **277 tests** (4 m 59 s), explicitly excluding opt-in live `OpenSandboxIntegrationTests`; final Synthetic HTTP/Runtime regression passed **12 tests** (50 s). Infrastructure and API projects built successfully. Changed Markdown local links/fences and `git diff --check` passed. Counts overlap.

## Commands

```sh
python3 -m unittest scripts.tests.test_sandbox_retirement scripts.tests.test_sandbox_compatibility scripts.tests.test_opensandbox_config -q
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~Tenant_Sandbox_worker_|FullyQualifiedName~Tenant_Sandbox_lost_create_reply_requires_exact_provider_fence' -v minimal
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~Tenant_Sandbox_old_server_without_retirement|FullyQualifiedName~Tenant_Sandbox_technical_recovery_cursor|FullyQualifiedName~Tenant_Sandbox_first_generation|FullyQualifiedName~Tenant_Sandbox_lost_create_reply_requires_exact_provider_fence' -v minimal
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~Sandbox&FullyQualifiedName!~OpenSandboxIntegrationTests' -v minimal
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~Tenant_Sandbox_|FullyQualifiedName~Owned_instance_sandbox_HTTP|FullyQualifiedName~Model_prepares_durable_persona_approval|FullyQualifiedName~Model_approval_and_exact_checkpoint_survive_SQLite|FullyQualifiedName~Original_conversation_receives_saved_wait' -v minimal
```

## Remaining boundaries

The SDK peer controls provider/mount/status/log responses; its filesystem/store facts are real disposable data. The provider HTTP proof uses the actual pinned image's classes/authentication, with Docker operations controlled. Neither proves an actual tenant Docker bind/initialization/daemon-outage qualification or production isolation. Updating an existing running trusted server requires its explicit state-preserving recreation; that was not performed. Tenant verification lost-create/orphan fencing (its disposable verification family is distinct from Session generation retirement), explicit process Stop/reset/profile switching and richer tenant process projections, model offering/output delivery and UI remain open. Hosted CI, full local CI and frontend/browser checks were not requested or run; this change contains no frontend source. No user database/reset/provider spend or Real host restart occurred. No full-proposal or milestone acceptance is claimed.
