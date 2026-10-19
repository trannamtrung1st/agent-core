# Tenant Browser human profile reset qualification — 2026-10-20

This checkpoint adds canonical human preflight/reset for an actual tenant Agent's persistent Browser profile. It uses the incumbent Instance, IAM, native profile and audit owners. It does not accept the full IAM proposal or add its frontend control, privacy administration, profile decommission or autonomous Browser origins.

## Authority and ownership

Independent current raw `agents.read`, `browser.read` and nondelegatable `browser.manage` apply to the actual Agent. The original authenticated Account/login/Principal, active Instance, exact security/Instance revisions and opaque native witness are rechecked. The witness covers private provider lifetime, exact native context, persistent command/reset revision and directory identity; it is not a permission or stored approval. Preflight reads existing handles and bounded filesystem metadata without binding, launching or creating a profile.

Before physical work, both canonical stores atomically record pending reset on the existing Instance, advance Instance/security revisions and append the unique operation's start audit. Reset closes the Agent's shared persistent context and all its persistent views, confirms actual native Close evidence, takes the reusable exclusive `.lock` inode and checks current human/revisions again immediately before deletion. Linked paths and competing locks refuse deletion. Only actual directory absence confirms reset. Ephemeral contexts and other Agents remain live. Profile bytes never enter Domain/Application, audit or HTTP.

Pending/unknown Instance facts fence actual persistent model target preparation and dispatch. Other Instance writes preserve those facts. Factual completion advances the existing revisions and audit independently of caller cancellation/logout. No Run, consent request, exceptional grant, reset table, cleanup worker or additional execution engine is created. General Instance serialization omits the internal reset state; the human facade returns only normalized status/reason/times and operation ID.

## No-replay recovery

Current-operation retries return stored facts without another close/delete, even with obsolete submitted revisions. A unique start-audit ID prevents an older operation from acting again after a later reset. Native state retains only the latest operation's normalized result/in-flight identity. An authorized GET can settle pending from matching cached facts, or conservatively record unknown after host loss. Busy native work remains pending, including the interval between committing pending and publishing its in-flight flag. Recovery never invokes physical reset. A new deliberate operation after unknown needs fresh current authority and witnesses.

A five-second independent token bounds factual settlement; a failed reply/settlement can therefore leave pending until readback. Unknown may mean native closure succeeded while bytes remain. No request acknowledgement or native close alone is treated as successful deletion.

Canonical owners: [architecture](../03-system-architecture.md#tenant-browser-human-profile-reset-ownership), [ports](../04-backend-interfaces.md#tenant-browser-persistent-profile-reset), [implementation](../12-backend-implementation-spec.md#tenant-browser-profile-reset-implementation), [HTTP](../14-api-and-realtime-protocol.md#tenant-browser-profile-reset-http), [schema](../15-persistence-and-configuration.md#tenant-browser-profile-reset-state), [tests](../16-testing-strategy.md#tenant-browser-profile-reset-checks), [operations](../17-observability-and-operations.md#tenant-browser-persistent-profile-reset-operations), [UX](../19-multi-tenant-iam-ux.md#browser-persistent-profile-reset-facts).

## Executed use cases

The authenticated HTTP matrix uses real native Chromium, actual memory/temporary SQLite stores and disposable local profile directories. Two persistent views of one Agent, an ephemeral view of that Agent and another Agent's persistent view are opened through trusted exact-owner fixture setup. A private localStorage value establishes retained state. Human preflight observes two persistent views; confirmed reset closes both, preserves the unrelated/ephemeral pages and a fresh exact-owner reopen has no saved localStorage. A cold GET creates no profile namespace; strict unknown body members reject before effects.

Stale native command witnesses return conflict with pages and reset state unchanged. Injected deletion failure and unproven native close record unknown; repeating the original request never repeats physical work. A new deliberate request with refreshed facts confirms deletion. HTTP logout after actual context closure prevents deletion, preserves the profile directory and records unknown; subsequent GET is unauthorized. Public results omit private errors and provider epoch.

Infrastructure cases independently omit each human permission; reject foreign/Agent identities; change security/logout before deletion; keep busy pending; recover matching cached versus missing host facts; reject reused old IDs; and fence an actual persistent model target before provider read. The SQLite native-host recreation case stages pending through the owning transaction, stops the first native provider, constructs a new native provider and SQLite IAM adapter over retained files, then observes unknown with original bytes and zero deletes. Repeating the old operation still deletes nothing; a fresh human operation deletes exactly once. This is explicit crash-boundary staging, not a forced production crash.

Native filesystem cases hold a competing exclusive lock, substitute root/Agent/lock symlinks, preserve an external sentinel behind a linked profile child, refuse current delete authority, simulate a delete that leaves bytes, compare provider epochs and run overlapping reset requests at an internal barrier. Expected versus observed results are unknown/no deletion for unsafe/unconfirmed cases, one confirmed deletion for the allowed case, and no begin callback for stale/busy attempts. Existing full native ownership/retirement tests protect sibling/tenant isolation and technical cleanup semantics.

SQLite guards reject clearing reset state, malformed/extra fields, witness/actor changes and forged completion. Downgrade refuses retained reset facts. Empty downgrade/upgrade restores the final model without pending model changes. Static inspection also confirms both creation ports reject injecting reset state during new Agent creation.

## Failures corrected

The first ten-case HTTP/native run passed six cases and failed four SQLite result cases: the start audit/security advance saved, but the untracked Instance row did not. Both start and settlement now attach that actual loaded Instance before changing reset state. The corrected ten-case run passed. Inspection also found that busy native work could be mistaken for abandoned pending in the narrow start/in-flight interval; recovery now excludes busy observations and its focused case passes.

The first Infrastructure attempt stopped at test compilation (missing explicit observation arguments and repository analyzer requirements). The next executed batch passed 28 cases and found one fixture formatting failure: raw SQL's literal `{}` was parsed as an EF composite-format placeholder. Escaping the fixed adversarial fragment corrected the fixture; this was not a product/schema failure. Final results below include the corrected matrix and additional authority/host/lifecycle cases.

## Focused commands and results

All commands run from repository root. Chromium cases opt in with `AGENTCORE_CHROMIUM_ISOLATION_TESTS=1`. No user profile/database, hosted credentials or external provider is used.

- `AGENTCORE_CHROMIUM_ISOLATION_TESTS=1 dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore -m:1 --filter 'FullyQualifiedName~TenantBrowserOwnershipTests|FullyQualifiedName~Tenant_Browser_profile' --nologo` — **61 passed**, no skips. Thirty reset-specific cases and 31 incumbent ownership/retirement cases.
- `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~Tenant_Browser_Allowed_read_requires_no_use_grant|FullyQualifiedName~Tenant_Browser_preparation_requires_raw_use' --nologo` — **4 passed**, no skips. Incumbent Allowed reads and raw-use preparation remain intact..
- `AGENTCORE_CHROMIUM_ISOLATION_TESTS=1 dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore -m:1 --filter 'FullyQualifiedName~Tenant_Browser_profile_HTTP_reset|FullyQualifiedName~Tenant_Browser_lifecycle_HTTP|FullyQualifiedName~Tenant_Browser_HTTP_active_counts|FullyQualifiedName~Canonical_Session_HTTP_pins|FullyQualifiedName~Creation_scoped_inventory_and_archive_restore' --nologo` — **52 passed**, no skips. Twelve reset cases plus 36 lifecycle/recovery and four incumbent authority/observation cases..

Repeated cases count once: **117 distinct passing cases** (65 Infrastructure, 52 API), including 42 reset-specific cases. Whitespace checks passed; **1,689 relative links across thirteen changed Markdown files** resolve without missing targets/fragments.

## Scope limits and next work

This is backend/native/HTTP qualification; no frontend component changed and no new Playwright MCP interaction is claimed. Native setup is trusted fixture work, not a product Browser-opening debug endpoint. Host loss is staged at the durable pending boundary; live multi-process hostile filesystem replacement and production deployment are not qualified. No Real provider, live account, Docker, physical device, optional hosted CI, full CI suite or milestone acceptance is claimed.

The shared human reset control remains open, followed by decommission/privacy/autonomous Browser work and the remaining resource/effect/cleanup, voice/rich Chat/live input, autonomous/background/event/transport, platform/system guardrail and general audit/reconciliation/notification families. Final isolation/UX acceptance and obsolete-authority/clean-schema cutover remain open. The original full goal stays active.
