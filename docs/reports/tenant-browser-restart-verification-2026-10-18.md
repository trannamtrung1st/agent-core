# Tenant Browser host recreation verification — 2026-10-18

The full proposal remains active. This successor qualifies the converted Browser consent/result path across disposable SQLite application-host recreation. Protected Credential/Artifact delivery, Browser privacy/reset/decommission/autonomous origins and the wider proposal remain open.

## Corrections

Actual recreation exposed a product defect: an exact result was already sealed, but the Run continued charging host downtime before mailbox delivery. Claim expiry could exhaust the remaining budget before the model received that result. Factual settlement now uses the existing checkpoint `AwaitingRuntimeAdmission` marker only after saving the exact completed authorization result in the receipt/security/audit transaction. Domain requires the same tenant approval-resume request/operation/hash, original external-effect generation at sealing and factual terminal disposition. No schema, queue, permission or provider witness is added.

Recovery and capacity deferral preserve the original attempt and paused counters until actual mailbox admission. Native elapsed time is charged once; remaining active execution resumes after current delivery checks. A sealed Indeterminate result can be delivered for explanation with tools disabled, while the Run remains failed/uncertain. An unsealed current external operation cannot obtain this special pause merely through capacity deferral. Cancellation wins over recovery. Ordinary uncertain dispatch still fails without replay.

The initial pending-consent fixture also used different application/store clocks. Production DI already supplied the configured clock; the SQLite test identity registration now does so as well. This was a fixture defect, separate from the product budget bug.

Warm consent initially remained safely blocked after recreation but reported Forbidden because its trusted provider metadata was absent. Current source/raw scope checks now precede safe metadata registration for saved targets, then physical identity comparison reports `browser_target_changed`. Registration does not allocate, navigate, close, restore old handles or change an existing view's mode. Lost warm consent cannot authorize a replacement view and remains cancellable. Shared uncertain-operation terminal copy also uses the generic operation name rather than credential-specific wording; Domain retains its incumbent unknown/no-replay summary.

## Observed workflows

Six native cases use two fully disposed/recreated application/DI hosts, a disposable SQLite database/profile root and one independent local page fixture. A fresh authenticated human session is required in the second host. The injected UTC clock advances six minutes past the old claim; actual worker/native timers remain real.

| Boundary before disposal | Observed after recreation |
| --- | --- |
| Pending cold navigation consent | Current human can approve the same exact stored request; hosted successor executes once and delivers its saved tool result. |
| Completed native navigation, exact result sealed before mailbox delivery | Hosted recovery delivers identical tool text and immutable receipt; the new provider allocates zero contexts and never dispatches again. |
| Actual native navigation followed by controlled lost acknowledgement | Exact saved Indeterminate result reaches the resumed model with Browser/authorization tools absent; Run ends `side-effect-indeterminate`, without another native context or dispatch. |
| Completed saved result, Browser assignment revoked in the new host | Current authority prevents model delivery; Run fails `authority-unavailable`, receipt stays unchanged and native contexts remain zero. |
| Durable InFlight fence abandoned before settlement | Existing worker seals factual Indeterminate; Run fails without model/native replay. The fixture knowing no native call happened does not justify inferring no effect. |
| Pending consent bound to a live warm view lost at disposal | Review names `browser_target_changed`, decision returns conflict, owner cancels and hosted continuation receives no-dispatch facts. No operation receipt, native context or dispatch is created. |

The completed/unknown/abandoned cases deliberately stage the existing pre-mailbox boundary using an internal execution gate and the real effect/store services. Unknown acknowledgement is controlled only after actual Chromium succeeds. The independent fixture remains alive to keep the exact loopback destination stable. Warm navigation is trusted fixture setup, distinct from model-authorized work. These are full application/DI/provider disposal/recreation checks within the test process, not OS power-loss or installed-data upgrade tests.

Both-store recovery cases additionally exercise two claim expiries plus capacity refusal, unchanged attempt/counters/payload/factual origin, exact-operation rejection, unsealed deferral rejection and cancellation. Two seconds of native work reduce the saved 10,000 ms budget to 8,000 ms, with active time 2,100 ms. Multiple six-minute waits leave those values intact. One second after actual admission produces 7,000 ms remaining and 3,100 ms active, with exactly one provider invocation/effect. SQLite assertions compare serialized facts rather than CLR object reference identity.

## Focused local verification

Final selections passed **31 Domain, 122 Infrastructure and 56 API cases**: **209 passing, no failures or skips**. The API selection includes all six actual native host-recreation cases. The Infrastructure selection includes the eight new both-store budget/recovery/cancellation cases plus existing Browser read/Review/effect/ownership and shared credential-external-effect regressions. The API selection also covers existing Browser model/read/Review, persona and credential consent/continuation, and SQLite host recreation. Earlier overlapping native and 25-case continuation/budget runs are not added to these totals.

`git diff --check` passed. The new/changed local Markdown links/anchors and balanced fences passed validation across ten changed documents. Canonical architecture, ports, implementation, persistence and testing owners plus README/TODO/plan/delivery status now describe the supported handoff and remaining scope. No extra verification was run after the final selections without a new change or concern.

Final commands use `--no-restore --disable-build-servers -p:UseSharedCompilation=false -m:1 --logger 'console;verbosity=normal'`:

```sh
dotnet test tests/AgentCore.Domain.Tests/AgentCore.Domain.Tests.csproj --no-restore --filter 'FullyQualifiedName~AgentRunContractTests' --disable-build-servers -p:UseSharedCompilation=false -m:1 --logger 'console;verbosity=normal'
AGENTCORE_CHROMIUM_ISOLATION_TESTS=1 dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --filter 'FullyQualifiedName~TenantBrowserApprovalTests|FullyQualifiedName~TenantBrowserOwnershipTests|FullyQualifiedName~Credential_successor_consumes_only_the_exact_checkpoint|FullyQualifiedName~Approved_runtime_credential_check_rotates_protected_grant_once_with_durable_receipt_and_reopen|FullyQualifiedName~Abandoned_credential_dispatch_recovers_after_lease_or_deleted_session_without_provider_replay|FullyQualifiedName~Concurrent_exact_credential_check_callers_share_only_the_saved_receipt_and_never_provider_authority|FullyQualifiedName~Caller_cancellation_settles_an_uncertain_dispatch_before_returning_and_ignores_late_provider_success|FullyQualifiedName~Shared_tool_boundary_uses_actual_executor' --disable-build-servers -p:UseSharedCompilation=false -m:1 --logger 'console;verbosity=normal'
AGENTCORE_CHROMIUM_ISOLATION_TESTS=1 dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --filter 'FullyQualifiedName~Tenant_Browser_SQLite_recreation_|FullyQualifiedName~Tenant_Browser_model_|FullyQualifiedName~Tenant_Browser_effect_HTTP_|FullyQualifiedName~Tenant_Browser_Review_HTTP_|FullyQualifiedName~Model_prepares_durable_persona_approval_|FullyQualifiedName~Model_approval_and_exact_checkpoint_survive_SQLite_host_recreation_|FullyQualifiedName~Model_credential_check_waits_for_exact_human_consent_' --disable-build-servers -p:UseSharedCompilation=false -m:1 --logger 'console;verbosity=normal'
```

Final logs: `/private/tmp/agent-core-browser-restart-final-domain-20261018.log`, `/private/tmp/agent-core-browser-restart-final-infrastructure-20261018.log` and `/private/tmp/agent-core-browser-restart-final-api-20261018.log`. Representative passing logs: `/private/tmp/agent-core-browser-restart-final-hosts-20261018.log` (six native cases), `/private/tmp/agent-core-browser-restart-budget-final-20261018.log` (25 continuation/budget cases).

Initial failing logs preserve the downtime-budget and test-clock findings in `/private/tmp/agent-core-browser-restart-initial-20261018.log`; the sealed-unknown constructor rejection in `/private/tmp/agent-core-browser-restart-boundaries-20261018.log`; and the warm-target Forbidden result in `/private/tmp/agent-core-browser-restart-hosts-20261018.log`. Intermediate nullable, object-reference-equality and unknown-summary assertion corrections are test repairs, not additional runtime defects. No source/test edits occurred while build/test/EF processes were live.

## Limits and status

No installed data/profile reset or migration, Real-host restart, external provider/account spend, full CI suite, hosted CI, deployment or milestone acceptance occurred. The abandoned crash point and lost reply are controlled; no live-provider/model semantics, machine power-loss or protected-resource flows are claimed. Existing UI components/layout/tokens are unchanged, so frontend suites/MCP were not repeated. Stale review recovery uses the existing shared inspector behavior; this report verifies its backend contract and hosted continuation, not a fresh rendered UI pass.

The wider proposal still includes protected Credential filling, captures/downloads/Artifact delivery, Browser lifecycle/privacy/autonomous work, other resource/effect families, Governance/platform, voice/rich Chat/live input arbitration, final obsolete authority/schema consolidation and the historical SQLite preference reliability follow-up. Earlier reports remain historical evidence for their original boundaries.
