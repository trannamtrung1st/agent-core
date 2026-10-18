# Tenant Browser exact target verification — 2026-10-18

Baseline: `2b9c1b92353ef607b84d5f2932d2b725deda47a7`. This is a provider prerequisite for exact Browser Review/dispatch in the full multi-tenant proposal. It does not enable model Browser effects, persist a new approval kind or claim full Browser/proposal acceptance. The full goal remains active.

## Implemented boundary

The existing provider now requires a matching physical target witness for ready tenant native requests. Preparation reads existing handle facts under the incumbent Session/owner gates without binding, provisioning, navigation, DOM or profile-byte reads. It binds owner, provider lifetime, isolation selection/mode, shared command revision, context/tab/page generation, current location hash, exact pending dialog and typed command hash. Mutable argument collections and upload bytes are copied; upload hashing uses actual bytes without another base64 representation.

Comparison, revision consumption and native execution retain the same incumbent gates. Private navigation/close/general call forwarding avoids reacquiring the hold. A target is consumed before an admitted attempt; changed arguments, simultaneous use or a later replay cannot repeat the SDK work. Preparation waits at most one second, and caller cancellation does not retire the admitted native call holding that gate. Pending native dialogs retain inspect/accept/dismiss controls; replacing a dialog invalidates its earlier target.

This is physical CAS, not an IAM decision, approval, role or one-time operation grant. It contains no raw URL, page/dialog text, command argument or secret value, and cannot be supplied through public HTTP/model fields. Provider restart invalidates old transient witnesses. Durable Browser Review still needs its own exact semantic operation/target, current authority and restart-safe dispatch/effect state; it must obtain and validate a matching live physical target rather than treat this witness as consent. No schema, worker, model offering or new execution owner was introduced.

Owners: [architecture](../03-system-architecture.md#tenant-browser-exact-command-targets), [ports](../04-backend-interfaces.md#tenant-browser-command-target-port), [backend](../12-backend-implementation-spec.md#tenant-browser-target-comparison-and-dispatch), [testing](../16-testing-strategy.md#tenant-browser-exact-target-verification). The [previous ownership/state checkpoint](tenant-browser-ownership-verification-2026-10-18.md) retains its original proof boundary.

## Executed journeys

- Actual disposable Chromium: prepare a cold navigation and observe no context or profile allocation. Missing target and changed URL reject before native work. Race identical saved requests: one succeeds and one returns target changed. Repeating the request remains rejected.
- Navigate to a new document, use the shared persistent context from a sibling, change isolation away and back, release a view or restart the provider. Every former target rejects. A witness for one Session cannot be used by another.
- Hash exact typed arguments and actual upload bytes; mutate source buffers/collections and prove the frozen command remains unchanged while the source hash changes. These are argument invariants, not Artifact authorization or upload acceptance.
- Hold native context initialization through a deterministic fixture gate. Cancel a preparation and allow a separate preparation to hit its one-second bound. Neither closes/completes the first native call; releasing its gate completes normally and fresh preparation recovers.
- Against an existing native context, inject a lost close acknowledgement. The result reports an attempted/unconfirmed failure, old target replay rejects and the close probe ran once. This controlled failure does not establish external business-effect reconciliation.
- Replace one actual pending dialog with another after target consumption but before SDK dialog capture, without an intervening Core command; the final identity check rejects the old dialog target without effect. Trigger the fixture's Confirm through the native command path, observe an outstanding native action, then inspect and dismiss its dialog successfully.
- Retain the scoped state-read, current human/IAM, closed model-effect and legacy profile/isolation regressions. HTTP active-state fixture warmup now prepares its live physical witness from the actual canonical owner; it remains distinct from a reviewed model effect.

## Focused results

Final selections passed **42 distinct cases: 37 Infrastructure and 5 API**, with no failures or skips. Infrastructure took 28.1s and API took 17.8s. The affected projects rebuilt successfully against matching source. Earlier 3/5/6 target-only checks and the focused dialog-race check overlap these final selections and are not double-counted.

```sh
AGENTCORE_CHROMIUM_ISOLATION_TESTS=1 dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --filter 'FullyQualifiedName~TenantBrowserOwnershipTests|FullyQualifiedName~Tenant_Browser_state_|FullyQualifiedName~BrowserStateIsolationTests|FullyQualifiedName~AgentBrowserProfileTests|FullyQualifiedName~Shared_tool_boundary_uses_actual_executor_roles_and_rechecks_revoke_without_borrowing_requester_authority' --disable-build-servers -p:UseSharedCompilation=false -m:1 --logger 'console;verbosity=normal'
AGENTCORE_CHROMIUM_ISOLATION_TESTS=1 dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --filter 'FullyQualifiedName~Tenant_Browser_HTTP_|FullyQualifiedName~Tenant_Sandbox_HTTP_returns_scoped_stored_observations' --disable-build-servers -p:UseSharedCompilation=false -m:1 --logger 'console;verbosity=normal'
```

Logs: `/private/tmp/agent-core-tenant-browser-target-infrastructure-confirmation-20261018.log` and `/private/tmp/agent-core-tenant-browser-target-api-confirmation-20261018.log`. All test handles are terminal; owned native contexts/hosts are disposed and disposable roots cleaned. Changed Markdown link/fragment/fence checks and `git diff --check` passed. Frontend, tooling context and design sidecar remain unchanged. No full local suite or hosted CI was run.

## Scope and limits

All Browser execution uses disposable headless Chromium and the owned loopback fixture. No source/test file changed while build/test processes were live. No user profile/data, Real host, external account/provider, CI, deployment or reset was used. UI/design files and public response shapes are unchanged; no frontend or visual acceptance is claimed. No failed check is counted as passing evidence.

Durable Browser preparation/Review, current execution/source policy, one-time dispatch/effect settlement, model continuation/output, protected Credential filling, rich/capture/download delivery, tenant privacy/reset/decommission and autonomous admission remain required. The broader resource/effect families, Governance/platform, voice/rich Chat, input arbitration, obsolete authority/schema consolidation and historical SQLite preference reliability follow-up remain open. Invalidation is deliberately conservative; it cannot verify the business meaning of arbitrary browser actions.
