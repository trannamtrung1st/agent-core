# Tenant Sandbox command approval verification — 2026-10-18

This successor to `88e272dbde32136ebddb5fbf23033f2e63aafab7` adds exact typed Sandbox command preparation/review, scoped approval inspection and atomic one-time reservation over a controlled pre-existing Ready generation. It advances an execution prerequisite. Initial provisioning/review binding, actual tenant SDK command dispatch and outcome settlement, mounts/process/reset/cleanup and the tenant Sandbox UI remain open. Other resource/effect families, Governance/platform, voice/rich Chat, live input arbitration, autonomous origins, final authority/schema consolidation and the earlier SQLite preference reliability follow-up remain in the [active delivery ledger](multitenant-iam-delivery-2026-10-16.md). The full proposal is not complete.

## Implemented boundary

- Core prepares a typed exact command from the actual claimed Run, admitted profile and current Session/profile/source/generation/home witnesses. Canonical JSON rejects unmapped, duplicate/noncanonical and invalid command fields. Sensitive nondelegatable `sandbox.execute` produces Review only. The existing durable wait releases the claim; root suppression and decision/wakeup owners are reused.
- Human inspection includes the exact command, cwd, timeout/background, profile/image, home access, CPU/memory/storage and saved initialization scripts. Independent underlying execution, profile and Session read scopes filter before count/paging. Protected source epochs and provider/mount identities stay off the HTTP projection. Approval remains distinct from effect completion.
- The approved reservation uses only saved parameters. Both existing store adapters atomically check current executor/requester/grantor authority, policy, source epochs, exact request/decision/hash, Run continuation and Ready generation. One existing receipt, Run InFlight fence, lease pointers and safe audit commit together. Duplicate/concurrent consumers observe that receipt without acquiring another fence. Raw metadata/global writers cannot manufacture approved receipt markers. No role or exceptional grant is created, no command is dispatched and no completed effect receipt is fabricated.
- Migration `20261017220217_TenantSandboxCommandApprovals` binds owned exact profile and approval operation references through indexed fields/FKs and strict JSON checks, retaining older approval payload bytes. Bounded escaped Sandbox payloads use a separate 1 MiB ceiling; older operation limits are retained. Both stores preserve immutable approval markers during factual updates.
- The shared Ant Design inspector names the new fields and units, renders a single exact command parameter list rather than meaningless before/after rows, and keeps scripts escaped and wrapped. Chat uses the same operation title. Shell, token bridge, responsive Descriptions, footer, confirmation and feedback owners are reused; no CSS, theme or token changes were needed.

## Local verification

The final focused Infrastructure regression passed **283 tests** (3 m 14 s), covering the new approval cases plus existing operation authorization/preferences, owned Sandbox metadata, migration and store regressions. The final focused HTTP regression passed **18 tests** (50 s), covering the new typed command projection, durable approval decisions, credential checks, original-conversation recovery, protected stored Sandbox observations, model persona approvals and SQLite checkpoint recreation. Counts are separate focused runs; they are not full CI or milestone acceptance.

```sh
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --filter 'FullyQualifiedName~Tenant_Sandbox_exact_approval|FullyQualifiedName~Tenant_Sandbox_approved_concurrent|FullyQualifiedName~Tenant_Sandbox_rejection|FullyQualifiedName~Tenant_Sandbox_review_filters|FullyQualifiedName~Tenant_Sandbox_approval_SQLite|FullyQualifiedName~Tenant_Sandbox_approval_migration|FullyQualifiedName~Tenant_Sandbox_private_source|FullyQualifiedName~Tenant_Sandbox_expired_generation|FullyQualifiedName~TenantOperationAuthorizationTests|FullyQualifiedName~TenantPreferenceOperationTests|FullyQualifiedName~Tenant_sandbox_runtime|FullyQualifiedName~TenantSandboxRuntimeMigrationTests|FullyQualifiedName~SandboxStoreTests' --nologo
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --filter 'FullyQualifiedName~Tenant_Sandbox_command_approval_HTTP|FullyQualifiedName~Canonical_durable_approval_HTTP|FullyQualifiedName~Credential_approval_HTTP|FullyQualifiedName~Original_conversation_receives_saved_wait|FullyQualifiedName~Tenant_Sandbox_HTTP|FullyQualifiedName~Model_prepares_durable_persona_approval|FullyQualifiedName~Model_approval_and_exact_checkpoint_survive' --nologo
cd web
pnpm exec vitest run src/features/access/TenantApprovals.test.tsx
pnpm run build
```

The final inspector tests passed **8 tests** (9 s). The TypeScript/Vite build passed after removing an unsupported test-only `exact` query option; existing Rollup annotation/chunk-size advisories remain. Initial focused backend use-case/boundary/schema runs also passed after fixing registry secret revision-zero validation; the final 283-test regression includes that correction. Migration generation succeeded; its advisory notes the installed EF CLI 10.0.5 versus runtime 10.0.12. No user database was migrated. Added local Markdown links, fenced blocks and `git diff --check` passed. The isolated browser/API processes were stopped after verification.

## Exercised outcomes

| Scenario | Expected and observed |
| --- | --- |
| Actual owned Run, controlled Ready generation, exact typed prepare/approve/resume | Durable wait and independent human decision; one saved operation with no assigned role/grant; one atomic approved reservation. SQLite reopen retains it. |
| Four concurrent consumers, then repeated consumption | Exactly one `Reserved=true`; every successful observation identifies the same receipt. Raw metadata cannot be promoted to approved execution. No completed effect exists. |
| Rejection or changed generation | No approved reservation/receipt. A stale pending review cannot approve; explicit cancellation remains possible. |
| Exact viewer scopes and revoked approver | Independent profile, Session and execution scopes govern inventory counts/read; no `agents.update` permission is needed. Later grantor revocation denies reservation. |
| Private source rotation | Initial secret revision zero is valid. Rotation after preparation invalidates saved review/approval. Cancellation remains possible; command and secret sentinels stay out of audit. |
| Expired Ready environment | Preparation denies before saving a review or releasing the claim. |
| Relational constraints and migration | Null/foreign profile or incomplete/mismatched review receipt references fail. A prior persona wait and exact JSON bytes survive disposable down/up migration. |
| Authenticated HTTP | Protected typed projection, no-store and exact decision body work; approved effect remains `unrecorded` with null `operationResult`; anonymous and foreign-tenant access deny; no execution receipt is created by consent. |

## Browser evidence and limits

Playwright MCP ran against a new disposable InMemory Synthetic host on `127.0.0.1:5098` with Vite on `5188`, no Docker or provider calls. Public HTTP created an owned organization/Instance and enabled Assisted settings. Visible Chat controls submitted `synthetic approval review`, opened its shared inspector, rejected the actual preference request through confirmation and observed the recorded rejection. Escape restored the opener; another message completed with `Hello from synthetic.`

The new Sandbox command renderer used an intercepted **GET projection fixture**, independently of the actual typed HTTP integration test above. It did not create or execute a Sandbox command through the browser. The fixture exercised long image/command text, fractional CPU, integer limits, false flags, null script and literal script/image markup. Labels/units and the unrecorded effect state were correct; no markup executed. A one-shot injected 503 retained inspection and recovered on explicit Refresh. The only console error was that intentional 503; network inspection also showed ordinary aborted stale reads during route/inspector changes. Actual rejection POST and subsequent reads succeeded.

After layout/transition completion, viewport/page/content widths were bounded at **1440/768/390/320px**; the large drawer was 736px on wide viewports and viewport-bound on narrow ones. The body retained the existing 16px section gap and responsive label layout. Initial immediate resize measurements were discarded because they captured the in-progress layout/transition; settled measurements and captures replace them. Captures: [desktop](assets/tenant-sandbox-command-approval/1440.png), [narrow](assets/tenant-sandbox-command-approval/390.png). These are controlled command projection evidence, not a full tenant Sandbox UI journey.

No actual tenant container, SDK command, mount, process, reset, cleanup or command effect settlement was exercised. No live provider/registry secrets, user data reset, Real-host restart, hosted CI or full local CI suite occurred. Arbitrary shell review approves exact bytes within a bounded environment; it does not guarantee the semantics of a privileged shell command. Production transport/settlement and remaining tenant proposal obligations stay active.
