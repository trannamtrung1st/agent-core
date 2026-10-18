# Tenant model Sandbox reviewed controls verification — 2026-10-18

## Scope and status

This proposal successor adds model-requested process Stop and environment reset to the existing exact durable Review flow. It extends the canonical lease/execution and approved-operation owners. No new scheduler, queue, control table, shell executor or UI kit is added.

The full multi-tenant IAM/approval proposal remains active. This evidence does not accept the entire proposal or a milestone. Typed Sandbox read-review, tenant Sandbox configuration/workspace UI, other resource/effect families, Governance/platform, voice/rich Chat, autonomous origins, final obsolete-authority/schema consolidation and the earlier SQLite preference reliability follow-up remain open.

## Implemented boundary

- The model proposes only `stop_sandbox_process` with an exact Core execution ID or `reset_sandbox_environment` for its actual owned Session. Core derives generation, old profile, observed revisions, target hash, permission and Review. Caller authority/witness fields and mixed payloads reject.
- Exact saved/current control capability and independent executor Session read/manage, agent Sandbox read/manage and old-profile read are required. Current original requester and qualified human reviewer remain independently checked. Nondelegatable `sandbox.manage` cannot be supplied by an exceptional grant. A stronger independence policy with no qualified reviewer produces safe denial without a waiting request.
- Control eligibility does not depend on Settings self-management, `sandbox.execute`, source enablement, registry credentials or image readiness. A disabled old source remains eligible for exact cleanup when control authority exists; later capabilities cannot expand the source Run.
- The target hash binds owned Session/Instance/executor, generation, immutable profile, active-or-latest execution, selection request and existing cleanup/Stop intent. Increasing polling/output revisions do not invalidate consent; replacement execution/generation or competing cleanup/selection intent does.
- Accepted consent atomically writes the existing Stop marker or reset lease, immutable operation receipt, Run effect, security advance and safe audit. The deciding human is retained as consent authority; the audit actor is the actual executing agent. Original-human login is never impersonated. Rejection/cancellation has no cleanup effect, and duplicates reuse the exact receipt.
- Completed means the control request was recorded. The model result explicitly says `controlRequestAccepted`, `terminationConfirmed:false` and `filesRetained:true`. The existing technical worker later interrupts once or retires the owned generation; it never creates another environment, replays setup or replays the command. Original command evidence/results remain unchanged.
- Migration `20261018021925_TenantSandboxControlReview` extends the existing approval CHECK constraint and reuses owned-profile FKs/indexes with a 128 KiB control payload bound. Older approval JSON is not rewritten.

## Exercised outcomes

The first representative reset preparation → human Review → atomic intent → duplicate/reopen case passed in both stores. Review left the environment Ready and untouched; acceptance recorded Terminating/reset once. A factual lease revision during the wait preserved the same reviewed target.

The expanded focused store run passed **20/20** in 49 seconds, including Stop progress during the human wait, rejection/cancellation, executor permission loss, competing reset, absent saved control capability, disabled-source cleanup without command permission, missing secondary scopes, independent-review denial, SQLite CHECK/FK enforcement, old-payload migration and reopen. The initial Stop fixture attempted to mutate immutable command-start evidence; it was corrected while retaining that production fence.

The affected approval/continuation, human control worker and profile-selection regression run passed **86/86** in 2m46s. A final manual transaction review then found a missing repeated SQLite decision fence and an incorrectly inherited local-configuration audit permission. Both were corrected. The final focused control/store run passed **22/22** in 49 seconds, adding direct-store changed-target rejection with safe cancellation, exact `sandbox.manage` audit context, semantic root dedupe across factual progress and generation-sensitive correlation. The SQLite decision test invokes the owning transaction directly rather than relying on service checks.

The actual hosted model/Runtime suite passed **9/9** in 31 seconds. Production coordination/checkpoint/approval/continuation owners remain enabled, with one Runtime capacity slot. The Synthetic recording model requests exact Stop/reset, waits for human HTTP Review, resumes with the saved consent result and completes. Only afterward, an explicit pass through the existing SDK worker confirms owned retirement. Repeated reconciliation produces no additional provider contact. No user command, initialization or create occurs for either control. Existing command approval/rejection/cancellation, uncertainty, fresh provisioning, background and stored inspection cases remain covered by the same fixture.

This uses controlled Synthetic verification/readiness and an actual loopback SDK protocol peer; unrelated periodic cleanup is removed from that fixture. It proves integrated application and controlled protocol behavior, not live Docker interruption or arbitrary shell semantics.

The final affected HTTP/hosted Sandbox run passed **18/18** in 41 seconds against the corrected transaction/audit code, retaining human reset/Stop, verification and Instance boundaries alongside the model cases.

The focused contract/policy/checkpoint run passed **67/67** in 739 ms. It covers strict control arguments, exact checkpoint intent, disjoint payloads, semantic fingerprints and contextual offering without command configuration or Settings. The historical exact-set projection assertion was extended to include the newly implemented authorization route; bare control tools remain absent.

The shared frontend approval/update checks passed **13/13** in 16.64 seconds. Production TypeScript/Vite build passed in 6.51 seconds; existing SignalR annotation/chunk-size notices remain. A fixture-only excess TypeScript property was removed before the passing build.

## Browser evidence

Playwright MCP exercised the real product UI against a disposable Synthetic/InMemory app on ports 5098/5188 with isolated temporary storage. Authentication and tenant shell were real. Approval responses used a controlled HTTP adapter; backend decision/worker correctness is independently established by the hosted/store fixtures above. Browser evidence does not claim a real model-created tenant Sandbox screen.

At 1440×900 and 390×900, the existing Ant Design Drawer/Descriptions wrapped long profile/image/target values without page/body/field overflow. The footer stayed visible, and computed section gaps remained 16px. No new spacing rules or component wrappers were needed.

Through visible controls, Approve opened the exact confirmation with pending-cleanup copy. An intentional 409 disabled submission; explicit Refresh details restored it. A second exact witnessed POST recorded consent and displayed “Request recorded,” without claiming “Applied” or confirmed termination. Reset projection separately showed selection clearing, pending retirement and canonical-file retention. Console inspection found only the intentional 409 resource error, with no application exception. Network inspection showed superseded initial GETs aborted and replacement reads returning 200; decision POSTs were the scripted 409 followed by 200.

Disposable screenshots:

- `/private/tmp/agent-core-control-ui-MTc2nl/control-review-1440.png`
- `/private/tmp/agent-core-control-ui-MTc2nl/control-review-390.png`
- `/private/tmp/agent-core-control-ui-MTc2nl/reset-recorded-390.png`

## Focused commands

```sh
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~Tenant_Sandbox_model_control|FullyQualifiedName~Tenant_Sandbox_approval_migration|FullyQualifiedName~Tenant_Sandbox_approval_SQLite' -v minimal
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~Tenant_Sandbox_|FullyQualifiedName~Owned_instance_sandbox_HTTP|FullyQualifiedName~Tenant_verification_HTTP|FullyQualifiedName~Owned_instance_sandbox_verification_HTTP' -v minimal
dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~TenantSandboxControlContractTests|FullyQualifiedName~TenantApprovalToolContractTests|FullyQualifiedName~TenantSandboxAuthorizationContractTests|FullyQualifiedName~ToolRegistryPolicyTests|FullyQualifiedName~CapabilityProjectionTests|FullyQualifiedName~AgentRunToolCallCheckpointTests' -v minimal
cd web
pnpm exec vitest run src/features/access/TenantApprovals.test.tsx src/features/access/useApprovalUpdates.test.tsx
pnpm run build
```

Changed-document validation passed 884 local links and 185 fragment references across ten Markdown files, with balanced fences. Staged whitespace validation passed.

CI, live-provider spend, live Docker qualification and user-data migration/reset were not requested or performed. Existing Real hosts were not restarted. The design-sidecar drift reported by Impeccable remains separate from these unchanged Ant Design presentation owners.
