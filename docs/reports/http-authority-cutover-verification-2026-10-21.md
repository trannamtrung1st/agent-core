# HTTP authority cutover — 2026-10-21

## Scope

The public HTTP entry layer now registers only the canonical Account/System/tenant API families and explicit purpose-bound external ingress. All 228 obsolete v1/unscoped v2 HTTP method/path pairs from the live `5e4f3a20` baseline are removed, together with their handlers and HTTP owner filter. This is a substantial clean-cutover module, not full proposal acceptance: obsolete Hub/token/internal-store/schema/client authority and remaining functionality/qualification still require their own completion.

The baseline was captured from the actual disposable Synthetic app's `EndpointDataSource`, not inferred solely from source strings. It contained 464 route endpoints: 460 API method/path pairs plus health, two SignalR endpoints and development OpenAPI. Of the API pairs, 232 canonical pairs remain unchanged and 228 are retired. The embedded [route inventory](../../tests/AgentCore.Api.Tests/Fixtures/retired-http-routes.json) records every removed and retained pair with the baseline commit. A future addition cannot silently restore an unscoped private API root.

| Removed family | Pairs | Canonical owner / boundary |
| --- | ---: | --- |
| `/api/v2/admin/...` | 144 | Tenant Definitions/Agents/children, Credentials/bindings, Sandbox profiles, Automations/Events and System governance; current original-human permissions and owning transactions. Old feature-specific orchestration endpoints receive no compatibility aliases. |
| v1/v2 unscoped Session routes | 48 | `/api/v2/tenants/{tenantId}/sessions/...`, tenant Activity/Run/Approval and file owners; cookie-derived human and exact tenant/Session/executor. |
| Unscoped Agent-instance routes | 18 | Tenant Agent, conversation, workspace and connected-account owners. |
| Old email/Drive OAuth/options routes | 7 | Tenant credential OAuth use cases and shared purpose-bound `tenant-google` callback/synthetic consent routes. |
| Host Session routes | 3 | Explicit canonical human Session actions; no client-selected Host/System lifecycle authority. |
| v1 Agent catalogue | 2 | Tenant Chat discovery and authorized immutable template/publication catalogues. |
| Global local-profile routes | 2 | Account self-service and tenant-owned preferences/configuration; no ambient local owner selector. |
| v1 webhook | 1 | Exact tenant/source-bound bearer ingress at `/api/v2/hooks/{tenantId}/{sourceId}`; receipt acceptance grants no execution authority. |
| Owner-token issuance | 1 | Removed. Account authentication is the public human entry point. |
| Unscoped preview options and model catalogue | 2 | Canonical tenant preview options and Chat/editor model projections. |

No redirect, fallback handler, alternate alias or model-generated authority was added. Saved old headers and signed-in platform operator authority cannot revive removed routes. Canonical external webhook/OAuth proofs remain separate from interactive cookie authentication. Health/static/development OpenAPI do not grant resource access.

## Source and test changes

Shared parsing/projection code used by canonical routes moved into explicit `AutomationHttpMapping`, `CredentialHttpMapping`, `OfficePreviewMapping`, `WorkspaceQuery` and `WebhookHttp` owners. Their validation, byte limits, exact trigger schemas and Office projection semantics are preserved. `TrustedLocalCaller` remains for explicit local-development transport policy and the separately unfinished internal cutover; deleting the HTTP owner filter does not turn local network placement into tenant authority. No persistence schema, provider, product UI, token system or visual asset changed.

The [test retirement inventory](../../tests/AgentCore.Api.Tests/Fixtures/retired-http-tests.json) names 301 obsolete HTTP-dependent test declarations, including three declarations whose JavaScript scenarios provisioned through the removed token/Session API. These contracts are deleted rather than skipped or kept behind a compatibility test host. Canonical native-cookie MessagePack fixtures remain. Independent Runtime/provider/persistence/policy tests remain separate: shared Experience Session seeding was extracted, partial Drive fixture owners were retained, and six native Drive recovery methods were preserved by provisioning their fixture through the existing Core service rather than the retired Admin HTTP route. This does not add a production backdoor or establish canonical tenant authorization for those old direct provider tests. The retirement inventory is traceability, not proof that every former behavior has a qualified canonical replacement.

The initial static dependency pass missed a shared helper across Drive partial files; the retained regression run exposed its obsolete HTTP setup as 404. The setup correction preserves the provider recovery assertions rather than removing those failures. Shared-fixture extraction also exposed an Experience helper reference and a controlled-release field whose native consumer needed preservation; their owners were corrected before final build. Remaining direct email-monitoring fixtures now use their existing Core authoring service. Three additional obsolete identity-maintenance HTTP declarations were removed, preserving its six independent cases. Two canonical negative-read expectations now assert removed-route 404 instead of an empty global catalogue. No production authorization rule was weakened to satisfy old test expectations.

## Evidence and limits

The removal test materializes every retired route and issues its original HTTP method with anonymous, valid old-token and signed-in platform operator clients, in both Memory and SQLite. Expected: every request returns 404 with no SPA success document, and the final canonical method/path set exactly equals the retained baseline. It also verifies that an old token alone receives 401 on canonical private families and that no outbound provider HTTP occurred. The representative route-only run passed both cases before adding the canonical-retention assertion; the final grouped run records that stronger check separately.

### Executed local checks

| Check | Observed final evidence |
| --- | --- |
| API test project build, exact final source | Passed with zero warnings/errors in 13.27 s; `/private/tmp/http-cutover-final-qualification-build.log`. |
| Canonical HTTP/wire regression group | 116 cases ran: 115 passed; one former empty-catalogue expectation failed on the required 404, then passed in the final correction group. `/private/tmp/http-cutover-canonical-regression.log`. |
| Preserved direct Core/provider/policy group | Initial run: 41 unaffected cases passed; 21 Drive cases failed at the shared obsolete HTTP setup. `/private/tmp/http-cutover-retained-regression.log`. Those provider cases were retained and rechecked after changing fixture setup. |
| Final corrections and restored fixtures | **50 passed**, zero failed/skipped: Drive 27, email monitoring 12, identity maintenance 6, complete route removal/retention 2, corrected Credential lifecycle 1, Sandbox catalogue 2. `/private/tmp/http-cutover-final-corrections.log`. The two route cases repeat the canonical regression cases; counts are not disjoint. |
| Native tenant Voice/Text browser | **2 passed** at 1440/390 px in 52.7 s; `/private/tmp/http-cutover-tenant-voice-browser.log`. Actual fake-device capture/playback, durable hearing, mute/fresh unmute and explicit fresh Voice after reconnect still work without legacy HTTP requests or page errors. |
| Source/docs/inventories | No root endpoint handlers or `OwnerCapabilityFilter` remain in API source. Inventory uniqueness/counts, added local links, balanced fences and `git diff --check` pass. Existing design sidecar hash is unchanged: `8bdc3659a25b706454ac8cc0394323c5e0e60ea1ade616756b180efbaecef49b`. |

The canonical workflows exercised actual cookie/CSRF sign-in, distinct memberships, logout/password/revocation fences, exact Credential and binding permissions, owned Instance children, byte-exact Attachment/Artifact/workspace and Office reads, webhook receipt authority and fresh-root Automation execution through the actual worker. Both persistence profiles are exercised by the selected data-driven fixtures. Real JavaScript MessagePack uses canonical cookie/origin/attachment routes for live text and server/client Voice; the removed owner-token JavaScript harness is not counted as current coverage. Native direct tests preserve provider uncertainty/idempotency, exact batch outcomes, replay fences, source scope, retention policy and host ceilings. Passing those direct tests does not convert their internal fixture context into tenant authority.

Commands use the repository root unless stated otherwise:

```sh
dotnet build tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --nologo --disable-build-servers -m:1 -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Tenant_HTTP_cutover|FullyQualifiedName~IdentityAndTenantAccessTests|FullyQualifiedName~Owned_HTTP_credential|FullyQualifiedName~Exact_HTTP_credential|FullyQualifiedName~Typed_HTTP_profiles|FullyQualifiedName~Binding_HTTP|FullyQualifiedName~Owned_instance_skills|FullyQualifiedName~Owned_instance_inherited|FullyQualifiedName~Tenant_Artifact_HTTP|FullyQualifiedName~Tenant_attachment_HTTP|FullyQualifiedName~Tenant_workspace_HTTP_reads|FullyQualifiedName~Tenant_webhook_management|FullyQualifiedName~Tenant_automations_use_current|FullyQualifiedName~Tenant_live_MessagePack_JavaScript|FullyQualifiedName~Tenant_voice_real_JavaScript|FullyQualifiedName~Canonical_Session_HTTP' --logger 'console;verbosity=normal'
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~DriveJourneyTests|FullyQualifiedName~FilePreviewPolicyTests|FullyQualifiedName~HealthAndSessionLifecycleTests|FullyQualifiedName~OwnerBoundaryTests|FullyQualifiedName~StateIsolationConfigurationTests|FullyQualifiedName~ContinuityBoundaryTests|FullyQualifiedName~StateIsolationDefinitionInheritanceTests' --logger 'console;verbosity=normal'
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~DriveJourneyTests|FullyQualifiedName~EmailMonitoringReliabilityTests|FullyQualifiedName~IdentityMaintenanceJourneyTests|FullyQualifiedName~Owned_HTTP_credential|FullyQualifiedName~Sandbox_catalog_HTTP|FullyQualifiedName~Tenant_HTTP_cutover' --logger 'console;verbosity=normal'
```

From `web/`:

```sh
CI=1 NODE_OPTIONS=--no-experimental-webstorage TENANT_CHAT_API_PORT=5097 TENANT_CHAT_WEB_PORT=5187 pnpm exec playwright test --config playwright.tenant-voice.config.ts
```

The browser uses isolated Synthetic hosts; `CI=1` prevents server reuse and does not invoke hosted CI. No frontend source changed, so no new visual-world review, detector run, design-system refresh or frontend build is claimed. Prior Voice visual qualification remains historical at its own scope. The final correction run supersedes the identified obsolete expectations/setup failures; unaffected passing workflows were not rerun merely to inflate a green total.

All hosts/data are disposable Synthetic fixtures. No hosted provider, live OAuth, production reset, optional CI/Actions or full local CI run is implied. Actual canonical route retention and observed scenarios do not establish exhaustive every-route IAM/effect coverage, packaged-SPA fallback behavior, remaining feature parity, obsolete Hub/client/schema removal or full proposal/milestone acceptance. The original goal remains active.
