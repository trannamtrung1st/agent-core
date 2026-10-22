# Tenant model manual Automation invocation — 2026-10-22

Scoped local qualification of canonical `automation.run`, joining the [authoring module](tenant-automation-mutation-verification-2026-10-22.md) and the existing Automation/occurrence, approval, Run and receipt owners. The [delivery ledger](multitenant-iam-delivery-2026-10-16.md) owns remaining proposal work. This closes the model manual-run integration gap; it does not accept the full proposal.

## Delivered behavior

The model may admit one inspected active owned Automation under its actual current human request root. Core binds requester, task, parent revision, model/configuration, destination, occurrence and execution identities. The incumbent manual-invoker raw Agent/Automation read/run requirements remain enforced. Independent scheduled tasks cannot invent a human manual requester; their existing authoring and scheduled admission paths retain their separate authority.

`PreparedAutomationInvocation` is an optional exact plan in the incumbent local-operation payload. The model still requests `mutate_automation` with tool `automation.run` and only `automationId`/`expectedRevision`. Current Agent/Automation reads, actual requester, primary executor run authority, secondary Session/Run creation, saved destination rights, operation policy, semantic intent, model/configuration and overlap checks remain independent. An exact grant supplies only the primary run permission. Deny/ambiguous intent refuses; unavailable verification requires exact human review. A required decision creates no occurrence or execution graph early.

The transaction commits the invoking Run effect, unchanged Automation configuration with advanced parent revision, accepted manual occurrence, fresh execution graph, typed body-free receipt, grant settlement, tenant revision and audit together. Schedule selection, accepted automatic-occurrence count, instructions and history remain intact. A saved conversation receives only a Run, preserves its original creator/UserChat origin and requires no new-Session permission. Both stores validate the exact graph and live destination inside the same owning transaction.

Receipt metadata binds occurrence/Session/Run references and continuous parent hashes/revisions. Exact replay returns those factual references without another admission. Known own changes can explain an earlier parent inspection without waiving current reads or unrelated source changes. Result contracts explicitly separate `occurrenceAdmitted:true` from `executionCompleted:false`; accepted work later executes under its fresh actual-human manual root and current tool authority.

Chat and Inbox reuse the existing Ant Design inspector, Before/After display, confirmation and responsive footer. The shared result label is **Occurrence admitted**. A stale private preview becomes unavailable while the minimal decision and factual receipt remain visible. Chat filters current source/creation rights before paging, while excluding occurrence reservations without actual Runs. [Desktop](assets/tenant-automation-invocation-2026-10-22/invocation-desktop.png) and [narrow](assets/tenant-automation-invocation-2026-10-22/invocation-narrow.png) captures show the reviewed final preview. No new layout system, CSS or execution framework is introduced.

## Persistence and recovery

`20261022060000_TenantAutomationInvocations` extends the same approval/receipt JSON shapes without a new column or table. Configuration-only serialization remains unchanged. Its frozen rebuild preserves all 110 incumbent trigger definitions/indexes and adds two guards (112 total): exact original human request root for invocation consent, and exact accepted owned manual occurrence/Run for the corresponding receipt. Historical migrations remain unchanged. Downgrade refuses retained invocation plans or receipts before lossy conversion.

An actual SQLite application-host recreation retains the pending exact plan and action hash, with no early child. The reopened host rejects downgrade while that consent is retained, accepts the exact qualified decision, admits the frozen Run identity once, resumes the original task and executes the manual work. Direct retained receipts separately refuse downgrade without losing the accepted graph or parent facts. Empty 110 → 112 → 110 → 112 round trips preserve all prior guards/indexes, current model consistency and foreign keys. This does not certify installed legacy-data migration or authorize flattening historical migrations containing durable facts.

## Executed checks

| Check | Observed result |
| --- | --- |
| .NET solution and final affected API/test-project builds | Passed; zero warnings/errors, one MSBuild node, no restore/shared compilation/node reuse. |
| Application authoring/contract/semantic regressions | 61 passed. |
| Three affected Automation migration boundaries | 3 passed; all incumbent guard definitions/indexes preserved, current model and foreign keys verified. |
| Runtime/API qualification | 69 distinct cases qualified across grouped runs: 23 manual-invocation cases and 46 adjacent authoring/read cases. |
| Final affected API branch run | 16 passed: all exact-consent/result branches and direct/reviewed saved-conversation execution in both stores. |
| Shared inspector Vitest | 126 passed; the manual-admission confirmation case also passed again after final result-label changes. |
| Frontend production build | Passed; existing bundle-size warning remains. |
| Native Playwright CLI | Final 4 passed: approve/reject at 1440 and 390 px. |
| Playwright MCP | Fresh exact manual request, literal saved task and separate-execution disclosure inspected, exact approval confirmed, Occurrence admitted and truthful reply observed; revision 2/unchanged instructions and one new owned work Session verified. No console errors or failed operation requests. |

**259 distinct focused cases plus four browser journeys passed.** Build and MCP evidence are separate. The 69-case API run initially passed 65 and exposed four invalid saved-conversation fixture requests. The final 16-case affected run fixes those fixtures and verifies all corrected result/history branches; it is not reported as a second complete 69-case run. Earlier representative execution also caught an incorrect expected occurrence routing revision, corrected before grouped qualification. Browser execution exposed the missing Chat creation visibility and stale-private-preview refresh conflict; both were fixed and the four journeys rerun successfully. Rejected requests retain a rejected decision with no invented effect receipt. Session metadata intentionally omits history; the corrected fixtures verify actual conversation output through the history API.

The integrated cases inspect → admit → factual replay → complete the invoking task → execute the manual task. They verify current human intent, primary-only exceptional grant consumption, exact Review, classifier-unavailable Review, rejection, changed pending parent, revoked secondary authority, missing creation authority, semantic denial and absence of manual-run offering to independently scheduled authoring. Saved-conversation cases execute and verify actual HTTP history while preserving creator/origin without Session creation permission. Pending-consent host recreation and populated downgrade guards exercise durable recovery rather than only adapter re-instantiation.

Each browser approval leaves one active Automation at revision 2 with unchanged instructions and exactly one Background work Session. Each rejection retains revision 1 and creates no work. Journeys check the separate-execution confirmation, truthful terminal reply, factual admission label, escaped literal markup, reachable footer, no document overflow, and no captured page/request/server failures. Final desktop/narrow captures were visually reviewed.

Final commands:

```sh
dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet build tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~TenantAutomationMutationContractTests|FullyQualifiedName~TenantAutomationAuthoringTests|FullyQualifiedName~TriggerCommandAuthorizerTests'
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~TenantAutomationReadMigrationTests|FullyQualifiedName~TenantAutomationMutationMigrationTests|FullyQualifiedName~TenantAutomationInvocationMigrationTests'
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Tenant_model_manual_Automation|FullyQualifiedName~TenantAgentAccessTests.Tenant_model_Automation|FullyQualifiedName~Tenant_independent_Automation_authors|FullyQualifiedName~Tenant_Automation_direct_replay|FullyQualifiedName~Tenant_model_existing_Automation'
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Tenant_model_manual_Automation_invocation_uses|FullyQualifiedName~Tenant_model_manual_Automation_consent'
# From web/:
NODE_OPTIONS=--no-experimental-webstorage pnpm exec vitest run src/features/access/TenantApprovals.test.tsx
pnpm run build
```

Native browser execution used only `e2e/tenant-automation-invocations.spec.ts`, one worker, no retries, owned API 5096/Vite 5186, isolated disposable data/cache and confirmed Synthetic health. Existing user ports were untouched. No provider requests, user-data reset, hosted CI or full local CI suite were requested or run.

## Remaining goal

Harness/Self-Management integration, mandatory autonomous-approval defaults, safe dependency/documentation/schema consolidation, final legacy-authority retirement and proposal-wide security/isolation/functional/UI acceptance remain required. Event/Webhook/preset and all policy/revocation combinations retain their separate scoped evidence rather than exhaustive new manual-run qualification here. The earlier event-review rejection retry stall and historical boundary-inventory missing links remain final-audit work. No milestone or full proposal acceptance is claimed.
