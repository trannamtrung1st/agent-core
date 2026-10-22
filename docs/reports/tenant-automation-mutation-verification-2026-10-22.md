# Tenant model Automation authoring verification — 2026-10-22

Scoped local qualification of canonical `automation.create`, `automation.update`, `automation.enable`, `automation.disable` and `automation.delete`. The [delivery ledger](multitenant-iam-delivery-2026-10-16.md) owns remaining proposal work. Model `automation.run` is still required; this report does not accept the complete model Automation family or the full proposal.

## Delivered behavior

The actual claimed tenant Run prepares changes to its own Agent's Automation through the existing authoring, scheduling and IAM owners. Immutable root User entries or the actual independently admitted task supply intent. Relative timing uses the root reference time. No model-selected tenant, executor, requester, root or arbitrary recipient is accepted. Model-created resources carry honest `AgentTask` provenance; their future occurrences start under their own admission and current authority.

Shared normalization preserves partial calendar settings, fixed-interval anchors and omitted same-source Event filter/dispatch/enabled fields. Exact child IDs/revisions, immutable preset origin, explicit destinations, bounded arguments and current admitted/configured capability policy remain enforced. Disable and delete stop future work without deleting history or demanding unrelated future-source admission rights.

Semantic classification checks the complete normalized Before/After against actual intent. It cannot grant IAM. Deny/Ambiguous refuse; unavailable/invalid verification requires exact human review. Existing configuration requiring source read Review stays out of the classifier; the qualified human reviews the saved Before/After. Current raw Agent/Automation/source/destination reads and secondary policy remain independent of primary mutation authority and exceptional grants.

Direct changes and approved changes use the existing local-effect owner. Parent CAS, Run effect, body-free typed receipt, tenant revision, audit and any grant settlement share one transaction. Stores independently repeat source, claim, policy, horizon, capacity and reviewer checks. Exact replay returns the factual prior result, and tool plus canonical arguments bind call identity. A bounded same-root/executor receipt/hash chain explains the task's own later edits to an observed parent without waiving current reads, unrelated edits or Event witnesses.

The existing Chat and Inbox inspector displays actual saved Before/After, literal instructions, timing/sources, requirements, destinations and verification facts. Confirmation authorizes one exact configuration change, never a role or recurring execution grant. Existing Ant Design components own layout and responsive footer behavior. [Desktop](assets/tenant-automation-mutation-2026-10-22/mutation-desktop.png) and [narrow](assets/tenant-automation-mutation-2026-10-22/mutation-narrow.png) captures show final rendered evidence.

## Persistence and integration corrections

`TenantAutomationMutations` extends the incumbent approval payload and operation receipt with typed metadata. It preserves all 106 existing SQLite integrity triggers and indexes and adds four guards (110 total). Empty upgrade/downgrade/re-upgrade, current-model consistency and foreign keys are verified. A populated canonical Runtime fixture refuses downgrade and retains its latest migration, Automation and typed receipt. Historical migrations are unchanged. This is a forward migration, not a certification of installed legacy-data conversion.

The representative Runtime flow exposed missing canonical tool resolution, duplicate legacy preparation/effect fencing and autonomous occurrence descriptions that still referenced human-only semantics. Those boundaries now use the canonical authoring path and shared tenant tool descriptions. Persisted consent also exposed nested Automation reference equality; canonical value equality now permits exact SQLite decisions without weakening source comparisons. Inspector verification caught hidden saved Before values and corrected their availability flag. Both SQLite flushes now share conflict handling inside the same transaction.

## Executed local checks

| Check | Result |
| --- | --- |
| Final .NET solution build | Passed; zero warnings/errors, one MSBuild node, no restore/shared compilation/node reuse. |
| Application authoring/normalization/intent contracts | 61 passed. Includes relative reference-time roundtrip, same-source Event preservation, strict arguments and existing authoring/semantic regressions. |
| Infrastructure migration boundaries | 2 passed; complete incumbent trigger definitions/indexes retained, 106 → 110 → 106 → 110, current model and foreign keys. |
| Real Runtime/API workflows | 46 passed: 28 mutation cases and 18 adjacent Automation read cases across InMemory/SQLite. |
| Shared inspector Vitest | 125 passed. Existing test-environment `NaN` height warning remains. |
| Frontend production build | Passed. Existing bundle-size warning remains. |
| Native Playwright CLI | 4 passed: approve/reject at 1440 and 390 px against final Synthetic binaries. |
| Playwright MCP | Independently submitted a fresh create task, inspected escaped literal instructions, confirmed exact approval, refreshed to Applied and observed the truthful completed reply plus exactly one disabled revision-1 Automation. No console errors; inspected network contained successful operation requests and navigation-aborted reads. |

**234 distinct focused cases plus four browser journeys passed.** Builds and MCP exploration are separate evidence. All tests use Synthetic fixtures and disposable state; no provider requests, user-data reset, hosted CI or full local CI suite were requested or run.

The integrated matrix creates, inspects, renames, enables, disables and completes through the real Runtime; verifies current User/task intent; tests semantic denial, exact Review, primary-only exceptional grant consumption, classifier unavailability, rejection and existing-resource deletion. Changed pending sources and revoked reads block application/output while preserving known completed effects. Scheduled authorship retains a genuinely absent requester. Same-call replay reports its existing receipt; switching tools with identical arguments cannot substitute another mutation. Read-Review tests observe no classifier request before consent. SQLite adapter recreation verifies retained configuration; populated downgrade refusal preserves resource and receipt facts.

Browser approval creates exactly one disabled resource at revision 1 with the literal saved instructions; rejection creates none. Each journey verifies truthful completion, visible footer controls, no document overflow and no captured page/request failures. Final screenshots were reviewed at both widths. MCP exercised the independent actual control flow; the final CLI repetition additionally verifies the corrected Before availability.

Commands used for final qualification:

```sh
dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~TenantAutomationMutationContractTests|FullyQualifiedName~TenantAutomationAuthoringTests|FullyQualifiedName~TriggerCommandAuthorizerTests'
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~TenantAutomationReadMigrationTests|FullyQualifiedName~TenantAutomationMutationMigrationTests'
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~TenantAgentAccessTests.Tenant_model_Automation|FullyQualifiedName~Tenant_independent_Automation_authors|FullyQualifiedName~Tenant_Automation_direct_replay|FullyQualifiedName~Tenant_model_existing_Automation'
# From web/:
NODE_OPTIONS=--no-experimental-webstorage pnpm exec vitest run src/features/access/TenantApprovals.test.tsx
pnpm run build
```

Native browser execution used a disposable configuration for only `e2e/tenant-automation-mutations.spec.ts`, one worker, no retries, API 5096 and Vite 5186. The health response confirmed Synthetic before execution. Existing user ports were untouched.

## Remaining boundaries

Model `automation.run`, Harness/Self-Management, autonomous mandatory-approval defaults, safe approval dependency/schema consolidation, final legacy-authority removal and proposal-wide security/isolation/functional/UI acceptance remain required. Mutation consent was exercised through both stores, not through a complete pending-consent application-host restart in this module. Exhaustive Event/Webhook/preset/destination combinations retain their existing scoped evidence rather than new end-to-end mutation qualification here. The earlier event-review rejection retry stall and boundary inventory's historical broken links remain final-audit work. No milestone or full proposal acceptance is claimed.
