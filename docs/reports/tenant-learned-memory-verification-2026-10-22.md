# Canonical tenant learned Memory verification — 2026-10-22

This checkpoint qualifies tenant-owned structured learning, recall, source protection and atomic response outcomes. It supersedes learned-storage/admission/recall-pending statements in earlier checkpoints for the exercised paths only. Exact forgetting/consolidation tools, human Memory/Profile management, broader continuity/Experience/Harness and full proposal acceptance remain open. The original goal stays active; no milestone acceptance is claimed.

## Implemented ownership and behavior

`ITenantLearnedMemoryStore` extends the incumbent IAM/Session/Run owner. Actual stored UserMessage roots and retained tenant User/Account/Principal membership determine human ownership. Session facts additionally bind the actual requester, IdentityUser facts bind that User and Agent, and User facts bind that User within the organization across expressly enabled Agents. Independent manual/scheduled Automation cannot adopt its creator's human memory. No global Profile or Definition fallback supplies values.

Raw `memory.read` and `memory.manage` remain independent. Consuming and origin Agent permissions are checked for promoted facts. Passive Review-required recall/learning withholds without creating or consuming consent. Admitted/current scope policies intersect before each model projection; an admitted exclusion cannot broaden during the Run. Live restrictions refresh optional Evidence while preserving historical tool Evidence and one current task/trusted fact projection.

The mailbox prepares a bounded, current-source learning plan. Successful response persistence revalidates its actual frame, raw permissions, current policy, source facts, revisions and exact private receipt owner inside the same outcome transaction. Response, facts, receipt ownership, tenant revision and body-free audit commit together. A failed response CAS commits none of the learning. InMemory prevalidates under the existing locks; SQLite phases changes inside the same outcome transaction. There is no independent post-response learner or execution owner.

Facts retain immutable values and provenance. Updates supersede an old fact; resolution retains factual status and repeated requests return AlreadyResolved. Model delete proposals produce ApprovalRequired without mutation; exact protected forgetting tools remain pending. Proposal/candidate/owner/prompt budgets bound admission and recall, and established content validation rejects reserved trusted fields, secrets and forbidden undelivered output tails without global-owner lookup.

Run checkpoints retain metadata-only immutable value witnesses before provider dispatch. Current source authority fences later model/effect use, checkpoints, new output/final persistence, factual report inspection/Review/settlement/dispatch and recipient text/audio/resync delivery. SQLite output validation reads metadata rather than private bodies. Superseded/resolved past witnesses retain value identity; deleted sources refuse reuse. Raw permission loss refuses new protected output. Accepted Session-authorized free-text history retains the documented residual disclosure risk; this is not a universal free-text filter.

Private receipt ownership is explicitly persisted but omitted from public DTOs. Human history and prompt receipt Evidence filter by the actual requester and current raw read authority. Old unstamped receipts are withheld rather than adopted. Session deletion tombstones only its local facts in the same lifecycle transaction. Explicitly promoted facts are separate owned copies: provenance grants no source-conversation read authority, and deleting the source conversation does not erase those copies. Learned facts also block Agent decommission and membership identity substitution.

## Persistence

Explicit migration `20261021164125_TenantLearnedMemory` adds scoped facts, actual tenant User/Session/Run foreign keys, unique active subjects and bounded lookup indexes. Frozen local triggers enforce original human or independent root provenance, immutable owner/value links, legal revision/status transitions, tombstone-only removal and retained membership identity. Populated downgrade refuses; empty disposable downgrade is safe. Model parity, malformed/partial schema refusal and explicit upgrade preserving installed Identity/global rows are exercised. No global rows are adopted or deleted and no user database was reset or upgraded. Use the existing backed-up explicit migration procedure for installed SQLite.

## Executed evidence

**351 distinct backend cases passed, zero skips; 95 frontend cases and two native browser cases passed.** Repeated/corrected cases count once. `/private/tmp/tenant-memory-results/distinct-final.json` indexes the latest outcome and TRX for each backend case.

| Cases | Executed workflow and observed result |
| --- | --- |
| 21 new API/model | Both stores: actual tenant publication and human admission learn permitted scopes and recall them on a new Run; independent read/manage/Review/disabled policies withhold without consent. Another requester in the same conversation and the same Account in another tenant cannot receive private fallback values. Live exclusion refreshes follow-ups; admitted exclusion survives later broadening. Current policy changes while a model is held refuse the captured result. Actual SQLite application-host recreation retains values/private receipts and permits fresh recall. Session deletion clears local facts while promoted User facts remain. |
| 41 new store/schema | Both stores: exact owned scopes, no global fallback, response-CAS rollback with no partial learning/audit, current rights/policy/frame/private receipt checks, independent read/manage, immutable supersession/resolution, actual requester isolation and source witness substitution refusal. SQLite trigger cases reject owner/value/revision/parent/provenance mutation. Explicit upgrade/downgrade preserves installed unrelated rows and refuses populated data loss. |
| 118 affected Application | Memory proposal validation/admission, receipts, prompt safety, context ordering/replacement, requester provenance and isolation ceilings pass. |
| 40 affected API | Canonical Profile self-service/Runtime evidence, live MessagePack, real JavaScript MessagePack Voice and exact HTTP cutover inventory remain passing. |
| 82 affected Infrastructure | Profile ownership/schema, startup migration/refusal, background outcomes and compound completion read/acknowledgement paths pass. |
| 49 affected Domain | Envelope JSON/parser/speech and Run contract cases pass, including retained receipt serialization. |
| 95 frontend | Conversation, Session store and Chat status tests pass. |
| 2 native browser | Desktop 1400×950 and narrow 390×844: canonical tenant fixture and visible sign-in/composer; learn a research task, refuse unapproved deletion, explain the prior receipt factually, resolve it, check scoped persisted receipts and unchanged report bytes, reload, verify repeated AlreadyResolved and missing-loop feedback. No page/related-network errors or obsolete route/header use. |

Backend commands from repository root (all tests use `--no-build --no-restore --nologo`):

```sh
dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Tenant_learned_memory'
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Tenant_learned_memory'
dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~MemoryProposalAdmissionTests|FullyQualifiedName~MemoryReceiptProjectionTests|FullyQualifiedName~SessionMemoryPromptTests|FullyQualifiedName~ContextAssemblerTests|FullyQualifiedName~PromptContextBuilderTests|FullyQualifiedName~TenantRequesterContextTests|FullyQualifiedName~StateIsolationPolicyTests'
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Tenant_Run_Profile|FullyQualifiedName~Tenant_User_Profile|FullyQualifiedName~Tenant_live_MessagePack_JavaScript|FullyQualifiedName~Tenant_voice_real_JavaScript_MessagePack|FullyQualifiedName~Tenant_HTTP_cutover'
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~TenantUserProfileTests|FullyQualifiedName~CanonicalStartupMigrationTests|FullyQualifiedName~CanonicalSchemaRefusalTests|FullyQualifiedName~Tenant_background_runtime_commits|FullyQualifiedName~Tenant_completion_read_compound|FullyQualifiedName~Tenant_completion_read_current_authority|FullyQualifiedName~Tenant_completion_inbox_acknowledgment'
dotnet test tests/AgentCore.Domain.Tests/AgentCore.Domain.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~ResponseEnvelopeJsonTests|FullyQualifiedName~ResponseEnvelopeParserTests|FullyQualifiedName~ResponseEnvelopeSpeechTests|FullyQualifiedName~AgentRunContractTests'
```

From `web`:

```sh
env NODE_OPTIONS=--no-experimental-webstorage pnpm exec vitest run src/features/chat/Conversation.test.tsx src/state/sessionStore.test.ts src/features/chat/ChatMessageStatus.test.tsx
pnpm run build
pnpm exec playwright test --config /private/tmp/tenant-memory-native/playwright.config.mjs
```

The temporary browser config selects the committed `memory-semantics.spec.ts` against this task's already-running Synthetic InMemory backend/Vite on disposable ports 5096/5186, with the explicit allowed origin. The maintained `web/playwright.tenant-chat.config.ts` also includes this canonical Memory scenario. Configured Playwright MCP navigation was attempted and returned **Transport closed**; native Playwright CLI is the runtime fallback. Both browser cases passed in 18.2s. A separate manual four-turn native journey also passed; desktop capture is `/private/tmp/tenant-memory-native/desktop-closed.png`. Test hosts were stopped; user ports 5080/5173 were untouched.

Grouped solution build passed with zero warnings/errors in 24.72s; the final captured solution build also passed with zero warnings/errors in 1.47s (`solution-build-final.log`). Frontend tests passed in 2.99s and tsc/Vite build in 5.04s, with existing SignalR PURE annotation/chunk-size advisories. No full CI suite, hosted Actions or live provider was invoked.

## Corrections and evidence accounting

Initial grouped TRXs retain failures; only later exact-case passing results supersede them in the distinct index. Two store fixture cases needed the peer's independent `sessions.create` grant before creating a new Session; production read/write guards were retained. One older Profile downgrade fixture assumed its migration was latest; it now locates the exact migration predecessor and explicitly reapplies empty successor migrations before continued use. The two owner cases and one schema case passed their focused reruns. No case was skipped or guard assertion removed.

New API fixtures initially carried an obsolete unregistered pseudo-capability; publication correctly refused it, and the fixture now publishes registered capabilities with the actual string-enum policy representation. The native fixture's initial missing 5186 allowed origin correctly refused Hub attachment; only its disposable host configuration changed. The E2E repeat-resolution assertion originally matched the prior response too early; it now waits for the next assistant response before checking its receipt. Both final browser cases passed.

## Remaining proposal work and limits

Exact learned-memory forgetting/consolidation/maintenance tools with durable consent, human Memory/Profile UI and broader tenant/User/Instance/Session continuity search remain required. Experience/Harness/maintenance execution, full Definition publication/adoption qualification, rich Skill/reference authoring/discovery/admission/private-body consent, mutable conversation locale/model, draft/Drive previews, remaining internal Runtime/live Approval/default authority and obsolete service/store/schema retirement, scalable working-cleanup proof continuation, platform guardrails and the exhaustive original 17-section requirements/routes/stores/effects/security/design/commands/invariants/deliverables audit remain open.

No product UI source changed; frontend changes port the canonical E2E fixture and runner. Product/Design context and sidecar remain unchanged for this module; original-proposal final context/sidecar synchronization is still required. Physical audio, live vendors, other operating systems and multi-instance behavior were not qualified. The full original goal remains active.
