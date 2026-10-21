# Tenant runtime registry and hosted execution cutover — 2026-10-21

Status: complete source module with focused local qualification; original proposal goal remains active. No full milestone/proposal acceptance is claimed.

## Complete source slice

- Remove `SessionHost` and its AgentRun/occurrence/state/capacity partials, the old dispatcher and profile notifier, owner background occurrence hosted intake and duplicate old Attachment TTL hosted loop.
- Register one canonical `TenantSessionRuntimeHost`; dispatcher/input/foreground/cancellation/receipt controls share it. Extract `AgentCoreOptions` without changing configuration values and preserve the stateless `SessionEventMapper` in its own file. Extract `TenantRuntimeCapacity` for bounded shared Run/Voice lifetimes; a slot has no IAM meaning.
- Add idempotent cached runtime shutdown and wire it into hosted stop. Close admission before drain, cancel generation work, retire Voice and close recipient output. Repeated stop/dispose waits the same task; cancelling one caller's wait does not restart cleanup.
- Run only canonical tenant execution/approval/effect recovery on the hosted Run pass. Run only tenant Automation admission and event processing on the scheduler, with existing phase supervision. Poll tenant email watches only. Keep ownership-checked decommission/Browser/Artifact/Attachment cleanup; stop old unowned workspace/working cleanup. No no-op authority adapter or compatibility registry is introduced.
- Purge exactly the retired browser `agent-core.owner-capability` key at app startup. Storage refusal does not prevent entry. The existing native lifecycle test seeds the obsolete key and now asserts its removal as well as absence of owner headers/requests.

## Verification

| Check | Result / observed outcome |
| --- | --- |
| Full solution compile | `dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`: pass, 0 warnings/errors, 14.61s. This is compilation, not the full CI test suite. |
| New registry/shutdown integration | 4/4 pass in 10s. Actual host registration has one canonical registry and no old dispatch/occurrence/hosted-state default; capacity is bounded and cannot reopen after repeated stop/dispose. Both stores admit an actual Synthetic Run, hold its noncooperative model, stop/drain the registry before model release, then reject late publication while retaining the exact Run. |
| Affected backend regressions | 68/68 pass in 2m35s: actual JavaScript Hub old-token/unattached refusal and canonical execution in JSON/MessagePack and both stores; active cancellation/capacity; persona approval consumption; server/client Voice and actual input/heard/fresh-unmute; SQLite Automation/event recovery; email watch ownership; agent decommission and restart cleanup; diagnostic/phase isolation; shared wire mapper. No skipped cases. |
| Frontend build | `pnpm --dir web run build`: pass; existing large-chunk warning only. |
| Native Chat | 1 pass in 14.3s. Seed obsolete local token, sign in, verify key removal, send/receive/display receipt, reload, rename/archive and complete read-only. No owner header/private unscoped request or page error. |
| Native Voice | 2 pass in 17.6s at 1440/390px. Fake-device capture remains live during output, heard/display credit remain independent, reconnect requires a new Voice click; the browser scenario ends with Voice live and CLI browser/host teardown exits normally. |
| Native Activity | 1 pass in 18.4s at 390px. Enabled/bounded Automation admits and completes through hosted canonical execution; inspect its Run and original Background Session, Continue the exact same Session into Chat, send a follow-up and observe a new completed assistant entry. No page error/obsolete request. |
| Final source/docs | No SessionHost/unowned dispatch/occurrence/TTL-host type remains in production API source or registration. New structural tests inspect the actual assembly/container. Local Markdown targets and standalone staged diff check pass. Design sidecar remains unchanged. |

Focused integration command: `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~TenantRuntimeRegistryTests|FullyQualifiedName~Tenant_runtime_shutdown_retires'`.

Affected regression command uses the same project/flags and this filter:

```text
FullyQualifiedName~Tenant_Hub_real_JavaScript|FullyQualifiedName~Active_tenant_HTTP_cancellation|FullyQualifiedName~Model_prepares_durable_persona_approval|FullyQualifiedName~Tenant_voice_real_JavaScript|FullyQualifiedName~Tenant_voice_actual_human|FullyQualifiedName~Tenant_owned_Automation_occurrence_reopens|FullyQualifiedName~Tenant_event_recovers_captured|FullyQualifiedName~Tenant_email_watch_baselines|FullyQualifiedName~Tenant_Agent_decommission|FullyQualifiedName~DiagnosticLoggingTests|FullyQualifiedName~TriggerSchedulerPhaseIsolationTests|FullyQualifiedName~SessionEventMapperApprovalTests
```

MCP navigation was attempted and returned `Transport closed`. Completed browser evidence uses Playwright CLI, native Synthetic API/Vite and disposable roots on 5096/5186 or 5097/5187. Temporary configs under `/private/tmp/runtime-registry-cutover/` import the committed tenant Chat/Voice configurations and use `--no-build --no-restore` for the newly compiled API. Chat selects `tenant text, rendered receipt`; Activity selects `tenant Activity inspects`; Voice runs both committed cases. The user's 5080/5173 hosts are untouched. Own hosts are stopped after the checks.

The initial sandboxed build repeatedly failed compiler socket setup and exited after five minutes without a compiler diagnostic. The direct compiler build exposed the shared stateless mapper co-located with the retired host; it was extracted unchanged and covered by existing wire tests. The final build and all functional checks pass. No permission/authority gate or production limiter was weakened.

Local logs/traces: `/private/tmp/runtime-registry-cutover/{build.log,build.sample,build-direct.log,build-final.log,runtime-first.log,affected-backend.log,frontend-build.log,native-chat.log,native-voice.log,native-activity.log}` and browser result directories. They are disposable verification evidence. `.impeccable/design.json` remains SHA256 `8bdc3659a25b706454ac8cc0394323c5e0e60ea1ade616756b180efbaecef49b`.

The new host tests inspect the actual DI/host registration, prove shared capacity bounds and idempotent closed admission, and exercise an admitted gated Synthetic Run during shutdown in both stores. Existing canonical cancellation, approval/recovery, event/watch and actual wire scenarios retain current authority/generation checks. Canonical capacity fixtures now use the capacity owner directly.

One old `ReliabilityJourneyTests.Automation_occurrences_keep_their_task_across_chat_history_owner_edits_and_disable` Fact is retired with the removed direct SessionHost/owner workflow; no test is skipped. Its original source is retained locally at `/private/tmp/runtime-registry-cutover/retired-test.cs`. Shared diagnostic logging tests retain their Run/scheduler coverage; the obsolete hosted-intake branch and fixture suppression references are removed.

## Remaining full-goal scope

Owner capability service/store/active schema and obsolete DTOs, all internal Runtime live-approval/default authority branches, canonical rich Skill/reference and locale/model authoring, draft/Drive previews, coordinated State/Runtime/Harness/Experience/maintenance parity, remaining platform guardrails and exhaustive original proposal qualification remain open. Removing an unowned worker does not certify equivalent canonical feature parity. Shared native stores and the generic claimed-Run coordinator still need the final schema/authority inventory. No data reset or live/user data deletion was performed. No CI, full milestone acceptance, physical audio/live-provider or production/multi-instance claim is made.

Shared native Ant Design presentation and design sidecar remain unchanged; the sole frontend implementation change is obsolete-key removal before render. No UI redesign or new design review is claimed.
