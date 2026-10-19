# Tenant Browser lifecycle retirement qualification — 2026-10-20

This slice connects canonical tenant Session lifecycle and Agent archive to technical retirement of already owned native Browser resources. It closes the missing production retirement path and adds retry to the existing resource cleanup sweep. It does not implement destructive profile reset/decommission, tenant privacy administration or full IAM proposal acceptance.

## Ownership and behavior

`TenantBrowserRetirementService` reads the actual scoped tenant, Agent/executor and Session records. Deleted/missing saved Sessions, archived/non-Active Session lifecycle, Ending/Ended state, unavailable tenant/Agent identity and Agent archive are retirement facts. All returned root identities must match the native sealed owner. Technical cleanup never fabricates a human, assumes user Browser-use permission or creates an operation grant.

Session/Agent lifecycle services reconcile only their affected owners after committing the existing protected metadata transaction. Cleanup failure cannot roll back that saved transition. The existing `WorkspaceCleanupHostedService` supervises a startup/five-minute pass over up to 100 existing native owners, with cursor wrap for retry. This does not introduce a new cleanup worker, native launch, filesystem scan, state table or effect ledger. A five-second TimeProvider budget bounds each pass/reconciliation. A timed-out owner advances the retry cursor so later owners and the other cleanup families can proceed; it remains eligible on wrap. A restart destroys the old process's contexts; retained profile directories are not opened merely to clean them.

Native retirement takes existing owner/Session/view gates without waiting for active native work. Persistent root and view can share a semaphore; it is acquired once. A shared view closes only its owned pages while siblings remain. The final view closes the actual context. Both page and context closure require SDK close evidence. Busy or unconfirmed closure stays `cleanup_pending`; the existing independently authorized Browser state observation reports partial availability, one pending cleanup, known sharing mode and no invented live counts.

The native port repeats canonical lifecycle inside its gates. A restore can cancel an unstarted retirement. Once closure is initiated, its process-local reference permits only that exact old context to finish retirement; it blocks reuse until actual closure and cannot redirect cleanup into a replacement context. Existing owner/isolation bindings and persistent profile bytes remain. Ephemeral state disappears with its context. Artifact/Attachment/workspace deletion remains with its separate canonical lifecycle owner.

Canonical owners: [ports](../04-backend-interfaces.md#tenant-browser-technical-retirement), [implementation](../12-backend-implementation-spec.md#tenant-browser-retirement-implementation), [wire facts](../14-api-and-realtime-protocol.md#browser-lifecycle-cleanup-facts), [retained state](../15-persistence-and-configuration.md#tenant-browser-retirement-state), [testing](../16-testing-strategy.md#tenant-browser-retirement-checks), [UX facts](../19-multi-tenant-iam-ux.md#browser-lifecycle-retirement-facts), [operations](../17-observability-and-operations.md).

## Native use cases

Native Chromium cases use disposable local profiles and exact server-owned bindings. They observed:

- Retiring one ephemeral or shared-persistent view closes its page and removes its context projection, preserves another Session's page and another tenant's page, rejects stale command/unscoped reuse, and leaves sealed ownership intact.
- After retiring the final persistent view, a fresh exact-owner native reopen reads the original private localStorage value. An ephemeral reopen reads no retained value.
- A changed lifecycle callback inside the native gate leaves the page open. Forged tenant ownership is rejected before invoking the callback.
- Native work paused at an internal barrier stays alive while cleanup reports pending. Restoring before any close starts clears that pending intent; the original native call can finish. A later genuine retirement closes it.
- A completed close Task without an actual context close event remains pending and blocks fresh target preparation. On retry after restore, only the old context closes; a subsequently opened replacement stays alive under an active lifecycle.

The first smallest native run found three persistent-mode failures from reacquiring the same root/view semaphore. That product bug was fixed. Per-page close was also hardened to require `IsClosed`, with a bounded deadline and pending status for unknown outcomes. The corrected affected ownership suite passed all 31 cases.

## HTTP and store use cases

The actual HTTP fixture uses canonical authenticated owner requests, current security/Session/Instance witnesses, real memory or temporary SQLite stores and local native Chromium. It warms only test-owned Browser contexts through trusted fixture setup; no product debug endpoint or external website is used.

The matrix covers Session archive, pause, complete, cancel and delete; Agent archive; retry after an unconfirmed close; and retry through the same service used by the resource sweep. Each runs in memory and SQLite with ephemeral and persistent Browser modes. Expected observations are actual native page/context closure for the changed owner, live sibling/other-Agent pages, zero live context readback where the Session remains readable, and fresh exact-owner native reopen after eligible restore/resume. Agent archive retires both owned Sessions and preserves the other Agent's page. A deletion acknowledgement removes the saved Session while retiring its native page.

Unknown page/context close leaves metadata committed and the page open, with independently read `cleanup_pending`/count/sharing facts. Clearing the injected failure then restoring or running `RecoverAsync` completes the old close. Repeated recovery does not close active restored resources. Existing canonical Session/Agent authority and Browser active-count cases run alongside this matrix.

The initial HTTP run passed 26 cases and failed four Agent-archive fixture calls because it sent a string for the existing numeric lifecycle enum. The fixture now uses the incumbent numeric request contract; no product wire behavior changed. The corrected 36-case matrix passed, including the existing authority/read regressions. The final budgeted lifecycle matrix also passed all 36 selected lifecycle cases; its filter includes the method name’s recovery term, so it covers the entire lifecycle matrix and four new timeout cases.

## Focused commands

All commands ran from repository root with `AGENTCORE_CHROMIUM_ISOLATION_TESTS=1`; no provider keys, hosted model, user database or live account is involved.

- `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter FullyQualifiedName~TenantBrowserOwnershipTests --nologo` — **31 passed**, no skips. Seven new retirement cases plus incumbent physical ownership, target, file-origin and Credential fences. Final sharing/identity projections are also checked through the HTTP matrix.
- `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter 'FullyQualifiedName~Tenant_Browser_lifecycle_HTTP|FullyQualifiedName~Tenant_Browser_HTTP_active_counts|FullyQualifiedName~Canonical_Session_HTTP_pins|FullyQualifiedName~Creation_scoped_inventory_and_archive_restore' --nologo` — **36 passed**, no skips (32 lifecycle/recovery cases and four incumbent authority/observation cases).

- `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter 'FullyQualifiedName~Tenant_Browser_lifecycle_HTTP&(DisplayName~budget|DisplayName~sweep|DisplayName~recover)' --nologo` — **36 passed**, no skips on the final budgeted service, including four stalled-close timeout cases. Because the method name contains “recovers”, this filter selects the complete lifecycle matrix; it excludes the four separate incumbent authority/observation cases.
- `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter FullyQualifiedName~Tenant_Browser_cleanup_budget_allows_other_owned_retirements_to_progress --nologo` — **2 passed**, no skips. With both Sessions archived through their actual Agent lifecycle API, one native page stalls. The first recovery pass yields zero completions at its budget; the next pass advances to close its sibling while the stalled page and unrelated Agent remain live. A final retry closes only the original pending context.

Repeated cases count once: **73 distinct passing cases** (31 Infrastructure and 42 API). Whitespace checks passed; **1,257 relative links across eleven changed Markdown files** resolve without missing targets/fragments.

## Scope limits and remaining work

This is backend/native/HTTP qualification. No frontend component or layout changed; no new Playwright MCP visual gate is claimed. The five-minute scheduler is the existing hosted orchestration; the retry tests directly invoke its configured retirement service rather than waiting five wall-clock minutes. No Real provider, Docker, physical device, hosted CI or full-suite/milestone acceptance is claimed.

Permanent native work that has not settled remains explicitly pending rather than being forcibly closed by ordinary lifecycle cleanup. Destructive reset/profile deletion, privacy/system administration, autonomous Browser origins, other resource/effect/cleanup families, voice/rich Chat/live input arbitration, autonomous/background/event/transport integration, platform guardrails, general audit/reconciliation/notifications, final isolation/UX acceptance and obsolete-authority/schema cutover remain open. The full original goal remains active.
