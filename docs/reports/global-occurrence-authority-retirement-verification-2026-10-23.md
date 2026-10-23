# Global occurrence authority retirement — 2026-10-23

This follows `5963fd2d` and advances the [IAM delivery ledger](multitenant-iam-delivery-2026-10-16.md). Global Automation/event admission, scheduling, routing and reporting actors are removed. Canonical tenant execution retains the existing shared coordinator and Session mailbox. This is scoped cutover evidence; remaining internal/schema reconciliation and final proposal-wide security, isolation, functional and rendered UI acceptance stay open.

## Removed authority and retained owners

- Removed `AutomationService` / `IAutomationService`, global `TriggerScheduler`, admission guard, policy recovery/reconciliation, occurrence router/directory/mailbox, `DurableOrderEventIngress`, global webhook ingress/credential service, `BackgroundOccurrenceIntake` and `BackgroundCompletionReporter`, including their registrations and lifecycle callbacks. Canonical Agent archive/restore no longer invokes a global Profile-based policy callback.
- Removed direct Runtime occurrence reservation/begin/abandon entry points, mailbox messages, queues and unadmitted model-pin fallback. Current bound Run model/reasoning pins remain authoritative, including a deliberately null reasoning effort.
- Preserved `AgentRunCoordinator` and `IAgentRunDispatcher`: `TenantRunExecution` constructs the shared coordinator with `TenantAgentRunStore` and `ITenantRunDispatcher`. Only the obsolete completion-reporter dependency/method is removed. Current initial reports use `TenantBackgroundCompletionService`.
- `TriggerSchedulerHostedService` already calls only canonical tenant due/event phases. `TenantAutomationService`, `TenantEventProcessor`, canonical webhook ingress and their owning transactions remain. Extracted `OccurrenceCompatibility` has an exactly unchanged body; existing ingress result enum/record retain their names and values. Physical store limits remain 100 rows / 30-second prepared lease in pure `AutomationRules` constants. Native persistence/receipt data is retained.
- Removed the three global registration/scheduler/due-lag instruments after their only callers were retired. Current scheduler-pass/Run telemetry, tenant audit and durable receipts remain. Corrected stale docs identifying the retired `SessionHost` as the current owner: the actual registry is `TenantSessionRuntimeHost`.

No schema, consent hash, approval classification, installed-data adoption or deletion changes are introduced. Inert Harness preparation bytes remain hash-bound and require coordinated retirement.

## Reproduced runtime handoff defect

The first grouped API run passed 219 of 221 cases. Manual existing-conversation Automation and its revoke-before-output variant timed out in InMemory. A deterministic internal fixture barrier then held the completed human Run's runtime immediately before registry release and dispatched a fresh Automation Run into that same Session. **Both stores reproduced rejection of the claimed new Run** (`expected accepted count 1; actual 0`). Frozen time made the coordinator's five-second deferral permanent in the fixture; ordinary execution could incur unnecessary delay.

`TenantSessionRuntimeHost` now waits outside its registry lock for a persisted terminal predecessor with the same complete owner and Session to release. It retains the new claim, then reloads that claim, current authority and runtime snapshot before admission. Active predecessors still refuse dispatch. The earlier same-Run retry-generation handoff remains. Cancellation is honored; no extra claim, provider attempt, queue or effect replay is introduced.

Both new terminal-predecessor cases, both original failing modes and both existing retry-generation cases pass. The deterministic tests assert first-attempt completion, two actual model calls, retained conversation/history and no extra Session/Run graph. Noncooperative cancellation and shutdown still release capacity and reject late publication.

## Fixture reconciliation

**46 obsolete method bodies across seven fixture paths** are retired, rather than counted as passing current qualification. Original bodies remain recoverable from `5963fd2d`; exact preimage hashes/method names are in `/private/tmp/global-occurrence-retirement-tests-20261023.json` and the baseline manifest with the same prefix.

| Original fixture | Bodies retired | Current boundary |
| --- | ---: | --- |
| `TriggerDurablePolicyTests` | 2 | Canonical source-policy/owned admission; new actual Agent archive/restore cases preserve saved Automation bytes and admit once after restoration. |
| `TriggerOccurrenceRoutingTests` | 19 | Canonical due/manual/event-to-worker graphs, current source/model/configuration pins, independent authority and existing-conversation consent/recovery. No compatibility live-occurrence router remains. |
| `TriggerInstancePolicyReconciliationTests` | 2 | Canonical configuration/admission policy owners; archive/restore is exercised through authenticated HTTP in both stores. |
| `OrderPlacedAdmissionTests` | 7 | Canonical authenticated webhook management/ingress and tenant event worker. Pure bounded/untrusted envelope validation remains. |
| `AgentRunAdmissionStoreTests` | 3 | Removed global intake wrappers; native atomic receipt/replay/reopen tests remain. |
| `TriggerStoreContractTests` | 3 | Removed global service wrappers; capacity, paging/owner isolation, newest ordering, reopen and retained history use literal native records. |
| `TriggerSchedulerTests` | 10 | Original native claim-expiry/CAS test remains in `TriggerOccurrenceStoreTests`; new tenant weekly Schedule case verifies 09:00 London across DST (167 elapsed hours to the next slot). |

The archive/restore, DST and terminal-predecessor additions provide six new actual tenant cases, two per scenario. Historical native record tests prove storage semantics independently of tenant execution authority.

## Verification

All runs use Synthetic adapters and disposable stores. Build command: `dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`. Tests use their existing project files with `dotnet test <project> --no-build --no-restore --nologo --filter '<filter>'`.

| Focused local group | Observed result |
| --- | --- |
| Domain Automation/trigger/receipt contracts | **12 passed**, 45 ms. |
| Application coordinator, runtime, authoring/defaults and identity fixtures | **82 passed**, 13 s. |
| Native occurrence/store contracts | **8 passed**, 37 s. |
| Canonical Automation/source/event/configuration/consent and worker group | **219 passed / 2 failed initially**, 12 m 49 s; both failures are superseded by the reproduced handoff fix and passing checks below. |
| Deterministic reproduction plus weekly DST | **2 expected failures / 2 passed**, 23 s; both failures independently reproduce the old handoff defect. |
| Handoff fix, original failing modes and same-Run retry | **8 passed**, 39 s. |
| Remaining existing-conversation boundaries and runtime registry | **49 passed**, 4 m 49 s. |
| Noncooperative cancellation and shutdown | **4 passed**, 58 s. |

The qualifying union is **333 distinct focused cases passed**, counting overlaps once and replacing the two original failures with their corrected results. Initial archive/restore checks overlap the main group. No qualifying test was skipped. Final serial build passed with zero warnings/errors in 1 m 03.26 s. After unused telemetry and fixture cleanup, six scheduler diagnostics/readiness cases passed in 1 s and the retained native occurrence case passed in 8 s; these seven cases overlap the qualifying union. Changed-document local file links, fences, source retirement/extraction inventory and whitespace checks pass.

Reproducible filters:

```text
Domain: FullyQualifiedName~AutomationTests|FullyQualifiedName~TriggerContractTests|FullyQualifiedName~LiveOccurrenceReceiptTests
Application: FullyQualifiedName~AgentRunCoordinatorTests|FullyQualifiedName~OrderPlacedAdmissionTests|FullyQualifiedName~ScheduleDraftAdmissionTests|FullyQualifiedName~SessionRealtimeLifecycleTests|FullyQualifiedName~SessionRuntimeCompletionTests|FullyQualifiedName~AutomationActivationDefaultTests|FullyQualifiedName~TenantAutomationAuthoringTests|FullyQualifiedName~AgentInstanceTests|FullyQualifiedName~ManagedInstanceP7DRegressionTests
Infrastructure: FullyQualifiedName~TriggerStoreContractTests|FullyQualifiedName~TriggerOccurrenceStoreTests|FullyQualifiedName~Occurrence_replay_commits_one_child_and_receipt_and_reopen_returns_the_original
API initial: FullyQualifiedName~Tenant_automations_use_current_human_scope_|FullyQualifiedName~Fresh_tenant_occurrence_pins_|FullyQualifiedName~Existing_conversation_Automation_keeps_|FullyQualifiedName~Automation_admission_consent_|FullyQualifiedName~Tenant_automation_authoring_preserves_due_|FullyQualifiedName~Tenant_webhook_management_and_authenticated_|FullyQualifiedName~Independent_Automation_loads_private_Skills_|FullyQualifiedName~TenantRuntimeRegistryTests|FullyQualifiedName~DiagnosticLoggingTests|FullyQualifiedName~TriggerSchedulerPhaseIsolationTests|FullyQualifiedName~SchedulerReadinessTests
API corrected/new: FullyQualifiedName~Existing_conversation_Automation_keeps_|FullyQualifiedName~Tenant_completion_retry_drains_previous_generation|FullyQualifiedName~Active_tenant_HTTP_cancellation_closes_saved_response|FullyQualifiedName~Tenant_runtime_shutdown_retires_a_noncooperative_generation
API DST: FullyQualifiedName~Tenant_automations_use_current_human_scope_&DisplayName~dst-weekly
```

Logs use `/private/tmp/global-occurrence-retirement-`: `application-20261023.log`, `infrastructure-20261023.log`, `domain-20261023.log`, `api-20261023.log`, `repro-20261023.log`, `handoff-fixed-20261023.log`, `conversation-final-20261023.log` and `lifetime-final-20261023.log`, `build-final-20261023.log`, `diagnostics-final-20261023.log` and `native-final-20261023.log`. The first build found one obsolete unused credential mapper; removing it resolved that compile error. No frontend/product surface changed; rendered UI acceptance, live providers and optional CI were not run for this backend module. Remaining Sandbox/Run, credential/browser/configuration/Admin authority roots and the final proposal-wide acceptance inventory remain required.
