# Canonical tenant completion recovery — 2026-10-23

This follows `e9b3485c` and addresses the explicit busy-parent/completion-recovery gap in the [authority retirement report](experience-automation-authority-retirement-verification-2026-10-23.md). It remains a scoped integration slice within the active [IAM delivery ledger](multitenant-iam-delivery-2026-10-16.md).

## Exercised boundary

`TenantCredentialAccessTests.CompletionRecovery` publishes a bounded tenant Definition through current HTTP, creates its tenant Agent principal and explicit independent IAM assignment, authors an Automation and admits a manual occurrence through current HTTP. It uses the real tenant worker, Session Runtime, checkpoint/effect owners, workspace, Artifact and completion service. It does not restore any global Profile authoring/dispatcher or build a caller-selected execution frame. The scripted provider emits controlled failures; all protected effects execute through production owners.

The twelve recovery modes run on both supported stores: complete, prose, wrong-tool, transport, semantic, incomplete-tool, authentication, reserve, oversized, oversized-twice, oversized-transport and oversized-transport-twice. The work writes one exact owned file and publishes one Artifact before finalization. Assertions require one write/publication, retained downloadable bytes, no replay file, exact failure/attempt diagnostics, stable Run/response identity, instruction-free interaction checkpoints and the bounded named `work.complete` recovery contract. Oversized correction remains one-shot. Transport retries cannot run before their persisted deadline.

Every SQLite recovery case recreates the actual application host between the initial terminal/retry observation and the final check. In-memory cases repeat the normal durable worker read rather than claiming persistence across a process restart. The fixture clock moves saved deadlines without firing real timers, preserving the prior deterministic deadline-reserve test mechanism; this is not a wall-clock timeout/throughput qualification.

Four busy-parent cases cover successful/failed work on both stores. A model barrier holds an actual foreground reply while independent child work reaches a durable terminal state. Completion stays pending without creating a report or ending the foreground Run. After releasing the reply, SQLite reopens with the completion still pending. Ordinary completion admission/worker then delivers exactly once, preserving independent root lineage, foreground history and the existing Artifact, without replaying either model work or effects. Core formats the report without a provider call.

The old unused model/barrier helpers move out of the global fixture. Its unrelated historical authoring tests remain separate migration debt; this module does not qualify them.

## Runtime handoff defect and correction

The initial corrected 28-case matrix passed, but an earlier transport retry had stalled after claim. Inspection found that the same Run's previous Runtime can remain registered while its durable failure already permits the next attempt. A collision returned unaccepted; the generic coordinator cannot safely release an ordinary checkpoint-bearing claim, so it remained Running without an admitted Runtime until lease recovery.

A held-provider-cleanup reproduction failed on SQLite with a second-attempt claim and completed dispatch while the old attempt was still retiring; its in-memory case passed, so it was not claimed as a deterministic two-store reproduction. The final regression instead uses an internal callback immediately before actual Runtime registry release, holding the precise owner on both stores. It asserts that the next attempt is claimed but dispatch remains pending until the barrier releases. The callback introduces no public transport or authority path.

`TenantSessionRuntimeHost` now waits for the older attempt's `Stopped` task when Run identity matches, claim generations differ and the new attempt count is greater. Both collision sites use that same private helper. It waits outside the registry lock with caller cancellation, then goes through normal dispatch again to reload claim, authority, snapshot and current admission guards. Same-attempt and different-Run admission keep their existing path. No new retry command, Domain state, scheduler, permission or schema is added.

## Local verification

- Final serial solution build: passed, zero warnings/errors, 14.49 s.
- Precise two-store Runtime-retirement barrier: 2 passed, zero failed/skipped, 12 s.
- Final focused regression group: **113 passed**, zero failed/skipped, **5 m 36 s**. This includes 30 new completion cases (24 recovery + 4 busy-parent + 2 retirement-barrier), 39 configured report-consent/destination cases, 20 Event Experience continuation cases, 19 request-isolation cases including actual MessagePack reconnect/child-report accounting, 3 registry/capacity cases and 2 noncooperative shutdown cases. Prior representative runs overlap this group and are not added to its count.

Initial fixture qualification was repaired in groups: the first interrupted attempt waited at an obsolete Automation-context assumption; the complete first corrected 24-case run had 4 passes / 20 failures from whole-Run serialization invoking the global Profile getter, reading metadata instead of conversation history, and advancing beyond the current 180-second budget. Current checks compare an explicit retry witness, read the canonical conversation store and enter the reserve at 165 seconds. An intermediate grouped run was interrupted after an ordinary transport retry timeout; bounded diagnostics were added, then all 28 recovery/busy-parent cases passed in 1 m 58 s. Earlier failures are not counted as qualification. The final regression group also rechecks configured reporting consent/destinations, Event Experience continuation, request isolation, registry and shutdown against the actual host fix.

Commands use the serial build below and `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter '<selection>'`:

```text
Build: dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false
Barrier: FullyQualifiedName~Tenant_completion_retry_drains_previous_generation
Final group: FullyQualifiedName~Tenant_completion_recovery_preserves_artifact|FullyQualifiedName~Tenant_completion_waits_for_busy_parent|FullyQualifiedName~Tenant_completion_retry_drains_previous_generation|FullyQualifiedName~Tenant_event_Experience|FullyQualifiedName~Tenant_runtime_shutdown|FullyQualifiedName~Tenant_request_isolation|FullyQualifiedName~TenantRuntimeRegistryTests|FullyQualifiedName~Configured_Automation_completion_preserves
```

Logs use `/private/tmp/tenant-completion-recovery-` with `build-final-20261023.log`, `first-r2-20261023.log`, `module-20261023.log`, `diagnostics-20261023.log`, `race-before-r2-20261023.log`, `race-fixed-20261023.log`, `retirement-barrier-20261023.log` and `final-regression-20261023.log`. The first DisplayName attempt unexpectedly enumerated/executed all 24 parameterized recovery cases; its result is reported as that full group, not as an exact two-case check. No count relies on discovery alone.

This addresses the retirement report's explicit canonical completion-recovery/busy-parent gap within the executed combinations. It does not reconcile every historical global fixture or complete the proposal. Other internal/lifecycle roots, final source/tool authority inventory, safe retained preparation/schema consolidation and proposal-wide security, isolation, function and rendered UI acceptance remain required. No frontend code changes. No new browser/UI acceptance, CI, hosted provider, user-data reset, Real restart, push or deployment is claimed.
