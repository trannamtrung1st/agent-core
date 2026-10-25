# Model discovery feedback fixes — 2026-10-26

Scope: the two code defects in the review of `main@a4f24913`. This is a focused code fix, not milestone or deployment acceptance. CI was neither dispatched nor awaited.

## Changes

- Discover now intersects the exact connection-test projection with `canCreateOffering`, the existing tenant-wide `models.manage` projection. Test remains available to a connection-test-only administrator.
- The existing offering PATCH/editor accepts deliberate `restoreAvailability`, default false. An enabled, non-retired offering omitted by discovery can be restored to `Unknown` under current original-human model-management authority and security/offering revisions. The offering revision advances, the existing audit records recovery, and identity/last-seen evidence remain intact. Existing default, connection, credential and capability guards still apply.
- Recovery uses the existing impact confirmation. Reload resets recovery consent while retaining the editable draft. Disable/retire clears recovery consent. Later discovery can block the model again. Existing Run/Session captures are unchanged; old route revisions refuse rather than adopting the update.
- The existing explicit-model browser retry fixture now grants its executing Agent `spaces.read`, matching current Space admission requirements and the onboarding fixture.

## Executed verification

All tests used offline/Synthetic providers and disposable stores. No live provider was called.

| Check | Result and observed behavior |
| --- | --- |
| Infrastructure: discovery, manual recovery, actual Run admission, catalog pins | 8 passed across InMemory/SQLite. Omission blocks inherited defaults and guarded dispatch; ordinary default edits do not clear the block. Stale recovery refuses; explicit recovery restores a current route and new Session selection. Actual admitted Run/Session pins remain unchanged, including SQLite reopen. Rediscovery blocks again and later reappearance restores observation without rewriting identity. Existing claimed-Run credential revocation still refuses dispatch. |
| HTTP model administration | 2 passed. A test-only member can Test, cannot Discover/recover, and sees the matching access projection. Registration/disabled/retired/already-recovered recovery inputs refuse with 400, stale revisions with 409; owner recovery keeps the default identity and restores eligibility. Existing model setup/selection/default/reference checks pass. |
| HTTP context switching and restart | 5 passed. Existing same-Session context continuation remains at most once in both stores; queued continuation survives SQLite host restart. |
| Vitest model administration | 14 passed, including limited Test/Discover access and explicit recovery confirmation. |
| Frontend production build | Passed; existing dependency annotation/chunk-size warnings remain. |
| Playwright browser tests | 3 selected scenarios passed: protected-key onboarding/model-and-Space switching at 1440/768/390/320px; unavailable explicit-model refusal/draft retention/retry; omitted-manual-default recovery with conflict/readback/reconfirmed consent and rediscovery at 1440/390px. |
| Playwright MCP | Actual local Synthetic discovery → blocked manual model → editor opt-in → impact confirmation → available model, then rediscovery → blocked again. No console errors in this flow. Normal protected API requests succeeded; development cleanup produced expected aborted reads. At 390px the drawer measured 390px and document overflow was false. |

Exact focused commands from repository root unless noted:

```sh
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~DynamicModels_discovery|FullyQualifiedName~DynamicModels_manual_default_recovery|FullyQualifiedName~DynamicModels_actual_tenant_admission|FullyQualifiedName~DynamicModels_catalog_captures' --nologo --no-restore -m:1
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter 'FullyQualifiedName~DynamicModels_HTTP' --nologo --no-restore -m:1
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter 'FullyQualifiedName~Model_context_switch_continues_at_most_once|FullyQualifiedName~Queued_context_successor_recovers_once_after_SQLite_restart' --nologo --no-build --no-restore
# web/
pnpm run test --run src/features/access/modelAdministration.test.tsx
pnpm run build
```

Browser CLI used a temporary derivative of `playwright.tenant-models.config.ts` with `webServer: undefined` against this task's known disposable Synthetic API/Vite servers on 5098/5189. It selected `protected-key onboarding`, `unavailable explicit model`, and `omitted manual default`. The latter two were rerun individually after correcting the missing fixture grant and waiting for completed rediscovery before reading its catalog. Both reruns passed. Initial .NET/Vite attempts could not bind loopback under the filesystem sandbox; reruns with local networking allowed passed. Temporary browser configuration was removed.

## Remaining release checks

Matching live OpenSandbox contract-5 SDK/gateway/daemon attestation, mount ownership/read-only enforcement, context-switch retirement, cancellation and revocation against the intended deployment remain unverified. Existing fail-closed compatibility behavior is retained. No legacy fallback was added.

Real-provider structured responses, tool continuation, streaming and error handling for all five adapter kinds remain opt-in and unrun. Anthropic's existing API-version header was not changed without live compatibility evidence. Hosted CI and full-suite/milestone acceptance were outside this requested local fix.
