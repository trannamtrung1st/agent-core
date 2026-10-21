# Tenant User Profile ownership verification — 2026-10-21

Implemented on main from `3dac5f4c`. This qualifies the canonical Profile source and human self-service backend. Runtime/model Profile evidence, learned Memory/continuity, Profile UI, final obsolete authority/schema removal and the full proposal remain open. No milestone or full-goal acceptance is claimed.

## Implemented boundary

`TenantUserProfile` binds actual `(TenantId, User PrincipalId, AccountId)` membership. Profiles are private to that User in that organization; the same Account has independent tenant sources. No Profile ID is selected by a caller, no global/local Profile is adopted, and neither Session creator attribution nor a SystemIdentity supplies ownership. A missing source returns an empty immutable revision-zero view without seeding a row, audit or security change.

The original authenticated human can set/clear only `preferredName`, `language`, `locale` and `timeZone`, with UserSet provenance and UTC millisecond timestamps. Explicit “friend” is valid human data, rather than a historical demo seed. Typed snapshots are immutable. The self-service boundary gives no agent read/write or other-member administration permission.

GET/PATCH `/api/v2/tenants/{tenantId}/me/profile` uses the existing cookie/tenant/CSRF/no-store boundary. Requests reject owner/query selectors, unknown/duplicate members, forged typed source objects, invalid values and oversized Content-Length. Responses omit Account/authentication references. Writes compare tenant security and Profile revisions, repeat original login/current membership at the owning gate/transaction, and commit one body-free audit with both revisions. Reads/acknowledgements recheck source/authentication witnesses after I/O; retired login or changed state withholds captured data. A lost acknowledgement recovers through factual GET, without automatic write replay.

`20261021144350_TenantUserProfiles` adds a canonical source table with indexed composite membership FK, revision/concurrency and bounded typed JSON. Frozen migration-local guards enforce revision-one insert, consecutive monotonic updates, exact immutable owner/source shape and retained parent User identity. InMemory and SQLite security writes refuse owner detachment/reparenting; status-based suspension/removal remains supported. A populated downgrade refuses to discard private canonical state; an empty disposable downgrade removes the new parent guard/table safely. Old rows/identity/authentication survive an explicit disposable upgrade without adoption. No user database was reset or upgraded.

## Executed evidence

**61 distinct focused cases passed, zero skips**. Repeated/corrected runs count each case once; final outcomes are recorded in `/private/tmp/tenant-user-profile-results/distinct-final.json`.

| Cases | Actual scenario and observed outcome |
| --- | --- |
| 5 new HTTP journeys | Both stores: authenticated read, set four values, explicit clear, original CSRF, no-store, anonymous obsolete-header refusal, stale PATCH conflict and factual readback. Other-member reads contain no private owner data; selectors/foreign targets refuse. Thirteen malformed/duplicate/forged/invalid payloads plus oversized Content-Length per store leave Profile/security/audit unchanged. Actual SQLite application-host recreation preserves owned values and identity. |
| 36 new store/schema cases | Both stores: actual membership ownership, the same Account across two tenants, independent Users, actual peer/cross-tenant ID substitutions, random/agent identity substitution, no implicit seed, immutable snapshots, clear, revision/audit/source guards and adapter reopen. Controlled pre-commit logout, password rotation, suspension, competing write and cancellation refuse unauthorized state/audit. Post-collection revocation/source change withholds values. Post-commit logout or lost transport acknowledgement preserves exactly one accepted write and factual recovery; stale replay cannot repeat it. Direct security detachment/User-ID replacement preserves Profile/security/audit. SQLite checks real FK and Profile/parent identity guards, malformed/duplicate/forged-source JSON, populated downgrade refusal and explicit upgrade without global Profile adoption. |
| 18 affected identity/startup cases | Same-Account tenant/agent independence, concurrent last-owner protection, password/session invalidation, fresh startup and incompatible/malformed schema refusal remain passing. |
| 2 HTTP surface regressions | Both stores retain all 239 current canonical method/path pairs and refuse all 228 retired pairs for anonymous, obsolete-token and actual operator clients. The original 232-pair baseline is preserved; explicit successor entries record five prior coordinated-isolation/Runtime State routes and two new Profile routes. No route discovery wildcard was substituted for exact inventory. |

Commands from repository root:

```sh
dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false
/Users/trungtran/.dotnet/tools/dotnet-ef migrations add TenantUserProfiles --project src/AgentCore.Infrastructure/AgentCore.Infrastructure.csproj --no-build --output-dir Persistence/Migrations
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Tenant_User_Profile|FullyQualifiedName~Tenant_HTTP_cutover'
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~TenantUserProfileTests|FullyQualifiedName~CanonicalStartupMigrationTests|FullyQualifiedName~CanonicalSchemaRefusalTests|FullyQualifiedName~IamIdentityIntegrationTests.Same_account_|FullyQualifiedName~IamIdentityIntegrationTests.Concurrent_owner_|FullyQualifiedName~IamIdentityIntegrationTests.Password_rotation_'
```

The complete solution and final affected test-project builds pass with zero warnings/errors. Final grouped Infrastructure run: 54/54 in 1m 10s; final Profile HTTP: 5/5 in 18s; final route-only recheck: 2/2 in 11s; extended actual-owner negative assertions: 2/2 in 7s (these last two ownership cases are already included in 54). No unaffected full suites were run.

Initial fixture corrections used the canonical HTTPS member-client origin; all production guard DDL was captured/restored unchanged around deliberate disposable schema corruption; the obsolete exact route fixture gained the five existing successor routes as well as the two new routes. Final assertions preserve the original schema-refusal, exact method/path and retired-route checks. No product guard was relaxed, no tests skipped and no broad passing subset substitutes for failed cases. EF tooling reported its existing 10.0.5 versus runtime 10.0.12 advisory during scaffolding; generated model parity and fresh/upgrade/reopen/refusal/downgrade tests pass.

Evidence logs: `/private/tmp/tenant-user-profile-{build,api-build,store-build,migration,api-final,http-qualified,store-qualified,routes-final,owner-final}.log`. TRX: `/private/tmp/tenant-user-profile-results/`. Earlier aggregate logs retain initial failures; the final distinct-result index supersedes only corrected cases.

## Remaining scope and limits

No frontend source changed. Actual backend HTTP/store workflows supply runtime evidence; no new browser/visual acceptance, frontend build, physical audio, live vendor, Linux/Windows platform, multi-instance or hosted CI claim is made. Product/Design context and the existing sidecar remain unchanged for this source-only module; original-proposal final design/context synchronization remains mandatory.

Canonical SessionRuntime still has no binding to this new source. Do not pass a fake/global Profile ID, impersonate a human login from a Run, use Session creator as every requester, or restore local/global memory lookups. The next large integration must resolve Profile/Memory from actual trusted root/requester ownership, independent current executor/source permissions and immutable/live policy ceilings; passive Review-required data must remain withheld and captured sources must be fenced through protected output. Runtime fact replacement/Evidence parity, full Definition publication/adoption, tenant learned Memory/continuity and live narrowing remain required.

Rich Skill/reference authoring/discovery/admission/private-body consent, mutable conversation locale/model, draft/Drive previews, Harness/Experience/maintenance execution, remaining internal Runtime/live-approval/default authority and schema consolidation, working-cleanup oversized proof continuation, platform guardrails and the exhaustive original 17-section requirements/routes/stores/effects/security/design/commands/invariants/deliverables audit remain open. The original goal is active.
