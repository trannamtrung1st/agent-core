# Canonical tenant Runtime State qualification — 2026-10-21

The canonical human State page, `agent.state.inspect` and trusted passive Runtime facts now observe existing tenant-owned resources through one Application service. This is focused module qualification in the ongoing multi-tenant proposal. It does not accept the whole proposal or broader context/isolation, learning or maintenance execution.

## Ownership and behavior

`TenantRuntimeStateService` accepts an original human login or server-held active Run frame. Sources receive the actual tenant/Instance/executor and independently readable Session, Run and Sandbox-profile sets. Sessions exclude archived/deleted roots; Run buckets additionally intersect readable Sessions. No hidden total or private conversation/provider body is returned. Null/restricted/unavailable means unknown; a readable empty source returns zero.

Both existing stores aggregate actual owned metadata. SQLite groups Session/Run metadata without materializing conversation entries and filters owned Sandbox profile/revision and readable Sessions before a bounded lease projection. Sandbox output/logs are not read. Native Browser observations use existing handles, deduplicate physical contexts and count distinct pages from readable Session views, without binding, reserving, launching or reading profile bytes. Cold unresolved sharing stays unknown. The existing tenant Runtime host and delivery registry contribute distinct hosted Sessions and Sessions with attached recipients.

The two-second fan-out preserves successful contributors when another source times out. Freshness starts before collection and expires after fifteen seconds. Human reads recheck original login, Instance and security after I/O. Model reads retain exact active claim generation, current requester/executor policy and the intersection of admitted/current explicit capability and visibility; a later relaxation cannot expand a running Run. Every model source uses its own operation policy. Review-required sources are withheld without creating or consuming consent. Explicit tool refresh has a one-second per-Run limit and 4096-byte output ceiling; trusted passive facts use the same authority without spending that refresh allowance. The obsolete unowned observation service/hosted-reader registration is removed.

`/t/{tenantId}/admin/agents/{instanceId}/state` uses existing heading/panel/responsive description compositions and direct Ant Design v6 controls. Manual Refresh issues only the canonical GET. Expired observations hide count panels; denied refresh retires protected content. Scope changes abort obsolete reads. Session/Run Activity links retain their independent authorization. No new CSS, theme, UI kit or configuration authority is introduced.

## Focused execution evidence

Logs are retained locally under `/private/tmp/tenant-runtime-state-verification/`. Counts below are distinct cases; corrected reruns are not counted twice. Total: **129 focused cases**, plus **one native Synthetic browser journey**.

| Check | Result and exercised outcome |
| --- | --- |
| API Runtime/registry filter | **20 passed** in 37.2s: both-store readable aggregates, exact reader withholding, wrong ownership, no private bodies/mutation, original-login logout/relogin and read revocation during collection, bounded uncooperative source, owned Sandbox profile/lease filters, actual model tool and passive facts, current/terminal/generation fences, rate limit, review-required Run counts withheld without consent, and admitted CurrentSession unable to expand after a live change to Summary. Includes three runtime registry ownership/capacity cases. |
| API Session/live transport regressions | **5 passed** in 22.1s: canonical authenticated creator/executor plus actual JavaScript MessagePack server/client Voice transports on both stores. This checks affected DI/delivery ownership; it is not physical audio acceptance. |
| Application policy/contracts | **40 passed**: `StateIsolationPolicyTests`, `TenantIndependentTaskContractTests`, `TenantSandboxInspectionContractTests`; existing visibility restrictions, explicit tool policy/projection and independent task/native inspection remain valid. |
| Infrastructure Browser ownership | **45 distinct passed** across the ownership aggregate and corrected Runtime subset. The first aggregate had 43 passing and two failures. Final three Runtime cases passed with local Chromium in 7.9s; the other 42 ownership cases had passed. Persistent/ephemeral contexts, exact readable Session pages, compound ownership, unavailable/cold observations and non-allocation are exercised. |
| Frontend | **19 passed**, two files, 1.79s: State render/refresh, restricted Unknown versus zero, partial source failure, expired hiding/fresh recovery, read denial retirement, old-scope abort/late result suppression and access routes. |
| Native Synthetic browser | **1 passed** in 10.9s at 1440/320/390px: create two Sessions and complete one actual text Run; observe two open Sessions and one terminal Run; refresh without security mutation; expire/hide and refresh/recover; exact reader sees only one Session and unknown Run counts; revoke agent read and refresh to an empty protected view with access guidance. No unexpected page/console/network failures or obsolete owner request occurred. Expected read-cancellation `ERR_ABORTED` and the deliberate 403 are classified separately. |
| Builds | Solution build and final focused project builds: zero warnings/errors. Frontend TypeScript/Vite build passed; existing chunk-size advisory remains. |

Commands (all .NET builds are serial; no source edits while a build runs):

```sh
dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Tenant_runtime_state|FullyQualifiedName~TenantRuntimeRegistryTests'
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Tenant_voice_real_JavaScript_MessagePack|FullyQualifiedName~Canonical_Session_HTTP_pins_authenticated_creator|FullyQualifiedName~TenantAgentAccessTests.Tenant_state_isolation'
dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~StateIsolationPolicyTests|FullyQualifiedName~TenantIndependentTaskContractTests|FullyQualifiedName~TenantSandboxInspectionContractTests'
AGENTCORE_CHROMIUM_ISOLATION_TESTS=1 dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --nologo --filter FullyQualifiedName~TenantBrowserOwnershipTests
AGENTCORE_CHROMIUM_ISOLATION_TESTS=1 dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --nologo --filter FullyQualifiedName~Tenant_runtime_browser
cd web
NODE_OPTIONS=--no-experimental-webstorage npx vitest run src/features/access/TenantAgentState.test.tsx src/features/access/accessRoute.test.ts
NODE_OPTIONS=--no-experimental-webstorage npm run build
TENANT_CHAT_API_PORT=5096 TENANT_CHAT_WEB_PORT=5186 npx playwright test --config playwright.tenant-runtime-state.config.ts
```

The recorded browser run used a temporary copy of the native runner with API `--no-build --no-restore` after the passing build, to avoid rebuilding under the test host. The permanent runner is retained; the temporary copy was removed. The runner owns disposable Synthetic host/storage roots and stops its hosts. User ports 5080/5173 were not used. Playwright MCP navigation was attempted against the running task host and returned **Transport closed**; native Playwright CLI supplied functional/browser evidence.

## Failures corrected

Initial API fixtures used HTTP and were corrected to HTTPS to obtain the actual CSRF flow. Initial browser fixture revocation supplied the assignment revision in a DELETE body instead of the required query; the fixture was corrected. One model fixture awaited a TaskCompletionSource instead of its Task. Initial frontend fixture inference rejected an unavailable resource; its resource collection now uses the shared wire type. These were verification setup/type failures.

Native Browser verification found a product aggregation defect: deduplicating physical contexts also discarded pages in later readable Session views sharing that context. The observer now deduplicates physical contexts and page handles separately and excludes closed handles without mutating native state. The final persistent case expects one context/two readable pages, the exact Session case expects one context/one page, and closing the sibling page removes it from both aggregate and exact Session observations. A follow-up test initially dereferenced an IPage as a binding; it was corrected to use the exact fixture page handle before rebuilding. Another initial assertion incorrectly expected zero while the Browser host was unavailable; the corrected case requires unknown/null, and separately verifies disabled-host owner mismatch remains denied. One attempted focused filter matched no tests; it was replaced by the method-name filter above and is not counted as verification.

## Bounded presentation review

The fresh finish reviewer returned **ship** with no material fixes for the supplied visible regions. [Review](assets/tenant-runtime-state-2026-10-21/finish-review.md), [incumbent design comparison](assets/tenant-runtime-state-2026-10-21/design-comparison.md), [desktop](assets/tenant-runtime-state-2026-10-21/desktop.png), [320px top](assets/tenant-runtime-state-2026-10-21/mobile.png), [restricted Runs](assets/tenant-runtime-state-2026-10-21/restricted-runs-320.png), [revoked read](assets/tenant-runtime-state-2026-10-21/revoked-390.png). These capture internal app scroller regions, not all content. Browser/Sandbox lower panels, full-width matrix, keyboard/hover/focus and measured contrast remain outside visual acceptance. The one scoped detector returned `[]`. The documenter added the State surface brief and comparison; PRODUCT/DESIGN/sidecar were preserved. Pre-existing stale sidecar/orphan brief mentions were reported without repair.

## Remaining qualification

No full local CI, GitHub Actions, remote push, live model/provider, physical microphone/audio, live OpenSandbox, multi-instance deployment, exhaustive installed-data conversion or whole-proposal acceptance was requested or run. This module does not prove SQLite host-recreation persistence for an observation (observations are ephemeral), every Run/lifecycle bucket transition, every post-I/O model policy race, all retired owner-positive context scenarios, cleanup effects across successive Runs, Experience generation or maintenance execution. The eleven retired State/context requirements remain inventoried in the [owner retirement report](owner-credential-retirement-verification-2026-10-21.md); canonical equivalents require their own execution evidence. Rich Skill/reference authoring, mutable locale/model, draft/Drive preview parity, remaining internal authority/schema consolidation and the final requirement-by-requirement proposal audit remain open.
