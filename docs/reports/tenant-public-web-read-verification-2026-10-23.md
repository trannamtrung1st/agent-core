# Canonical tenant public-web reads — 2026-10-23

This successor restores preserved `web.search` and `web.fetch` behavior through the current tenant execution boundary. The prior [public-download module](tenant-public-download-verification-2026-10-23.md) remains separately qualified. Generic `http.request` exact-operation approval, generic Composer Resource references, remaining internal/schema reconciliation and proposal-wide security/UI acceptance remain open.

## Implementation

`TenantPublicWebReadService` owns both stateless read tools using the existing normalized search/fetch ports. Current capability/configuration eligibility is separate from IAM. The executing Agent independently needs `agents.read` and `sessions.read` on its actual owned sources. Human requester roles cannot replace missing executor authority. The existing 77 permission classifications and default floors are unchanged; public reads create no operation grant or new approval kind. Explicit source-read policy remains mandatory. Authorized scheduled/manual Automation research has no unnecessary default review.

The service validates actual tenant/Agent/Session/Run/claim ownership, source lifecycle, current capability, raw permissions and source policy before I/O and after completion. Native DNS/redirect/credential-header protection remains with the existing public-network adapter. A bounded deadline discards a late noncooperative result; exceptions return safe metadata without provider error bodies. Queries accept 1–600 characters and result limits 1–10. Fetch accepts one public HTTP(S) URL without embedded credentials, caller headers or authority fields. Unknown/duplicate arguments fail before I/O. Search URLs and result fields are bounded; fetched text is clipped to 256 KiB UTF-8 and fitted to the actual remaining output budget, with truthful truncation.

Completed observations retain an argument-bound hash and explicit untrusted marker in the existing tool-call checkpoint. Reentry rechecks current authority without refetch. Both stores also validate retained sources inside the existing atomic response/outcome transaction and through report source reads. A role revoked during generation therefore prevents answer persistence and uses the existing protected-output refusal path. The new source rules add no table, migration, execution engine or permission snapshot.

Unowned, hash-changed and body-bearing error observations remain inert. Core error receipts accept only known metadata, including hash-bound recovery counters validated by their existing `InvalidToolCallRecovery` owner. Its existing instruction selection was factored without changing generated instructions. Arbitrary legacy error messages or nested recovery payloads cannot become model context.

## Verification

All data/hosts are disposable Synthetic fixtures. No hosted inference, live Brave search, external official-site request, user database reset, Real-host restart or CI was performed.

| Group | Result |
| --- | --- |
| Serial solution build | Passed; 0 warnings/errors, 48.56 s |
| Chat and actual scheduled/manual Automation HTTP matrix | 46 passed |
| Actual SQLite retry/host reopen matrix | 6 passed |
| Canonical lazy management discovery, execution and next-Run reset | 2 passed |
| Public source/isolation/deadline/retention cases | 20 passed |
| Unchanged native public fetch/socket/factory adapter cases | 34 passed |
| Public contracts, capability projection, registry policy, approval floors and invalid-call recovery | 214 passed |

There are **322 distinct focused cases**, with zero failures/skips in their final qualifying runs. Repeated correction/regression runs are not added to that count. The earlier 54-case HTTP run passed 48 cases but timed out in six new recovery fixtures; all six were subsequently corrected and passed. The 46 execution cases were rerun successfully against final strict error validation. The two canonical management cases passed with the new per-Run reset assertions before the later public-error-only correction.

The HTTP scenarios create actual owned Agents/Sessions and independent executor roles, then submit model calls through canonical admission. Both stores exercise search→fetch, ordinary reads, missing raw permission, explicit read policy, revocation during native I/O, revocation during model generation, malformed/caller-authority arguments, provider failure, unavailable search configuration and bounded results. Tests verify native invocation counts, untrusted/argument-bound receipts, absent unauthorized source text in subsequent model input/history, absence of native exception text and zero outbound provider HTTP.

Scheduled occurrences are admitted through the actual due-scheduler/service path after FakeTime advancement; manual occurrences use the actual authenticated Automation run endpoint. Tests assert the saved origin and actual absence/presence of the original human requester. Each source type can search and fetch under its own read authority without a default approval. Explicit policy or missing executor permission stops native I/O. No Profile authority is supplied.

The reopen scenarios complete one actual public read, trigger a controlled transient model failure and retain a deferred Run. They dispose the first SQLite host, reopen it and compare the exact checkpoint payload. Allowed recovery supplies the retained observation to the model after one total native transfer. Revoked roles and new source policy refuse reentry before any model request or refetch. The fixture explicitly dispatches the worker and advances its FakeTime retry timers; the earlier timeout did not establish a product failure.

Infrastructure scenarios check foreign tenant ownership, wrong Session/generation before native I/O, bounded output, current retained-source authority without refetch, and noncooperative source deadlines. FakeTime advances beyond the deadline before the native provider is released; only safe timeout metadata returns. Additional cases refuse unowned bodies, wrong hashes, data-bearing errors, arbitrary error messages and nested recovery payloads. Existing adapter cases retain their native public-network boundaries.

## Corrections and fixture cutover

The first build needed the lifecycle namespace. Initial Automation cases used a runtime-state fixture that deliberately replaces built-in capabilities with an inspection-only definition. A test-only opt-in now uses the actual General Assistant definition; existing fixture defaults stay intact.

The expanded HTTP matrix exposed a real final-output gap: reentry checks alone did not prevent persistence after role revocation during generation. Both native stores now check the source within their existing response/outcome transaction, and the existing refusal mechanism saves no answer. Invalid-call testing also exposed Core recovery metadata being mistaken for a source observation; it now has closed, validated handling rather than accepting arbitrary error bodies.

Two old application runtime cases expected unowned workspace capability loading/execution, which the tenant cutover intentionally removed. That method and its private model helper were retired. Current canonical management read cases independently prove lazy discovery, one persisted load, actual execution, absence of the loaded schema at the next Run's first request and a fresh load count of one. Pure discovery/schema/dedupe tests remain; the non-tenant pure count now checks its exact four tools, and the tenant inventory includes the three implemented public-web tools. No unscoped executor was restored.

## Commands

From the repository root:

```sh
dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Tenant_public_web_reads|FullyQualifiedName~Tenant_Automation_public_research'
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Tenant_public_web_retry'
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Tenant_public_web_reads|FullyQualifiedName~Tenant_Automation_public_research|FullyQualifiedName~Tenant_public_web_retry|FullyQualifiedName~Canonical_management_reads_use_lazy&DisplayName~capabilities'
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Tenant_public_web_|FullyQualifiedName~PublicWebFetcherTests|FullyQualifiedName~SocketsPublicWebTransportTests|FullyQualifiedName~PublicWebSocketsHttpFactoryTests'
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Tenant_public_web_'
dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~TenantPublicWebReadContractTests|FullyQualifiedName~CapabilityProjectionTests|FullyQualifiedName~ToolRegistryPolicyTests|FullyQualifiedName~PermissionApprovalFloorTests|FullyQualifiedName~InvalidToolCallRecoveryTests'
```

The combined HTTP command records the earlier 48 passes and six fixture timeouts described above; the first two commands are the final passing execution/reopen qualification. The adapter cases passed in the earlier 50-case infrastructure group; the final source-only group includes four further negative cases, for 54 distinct infrastructure cases.

## Limits

This is backend runtime/integration evidence. No frontend code changed, so no new frontend build or Playwright run was required for this module. Prior rendered approval evidence retains its original scope. Public completion/report source hooks are implemented, but public-specific report-delivery combinations and the complete final UI/security matrix are not claimed by these cases. Live public/provider behavior and generic HTTP exact approval remain unqualified. The full IAM goal is still active.
