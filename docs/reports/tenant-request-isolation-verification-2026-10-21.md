# Canonical tenant request isolation and settlement — 2026-10-21

One-request tenant Sessions now enforce their admission boundary and settle through the existing canonical host after its Run generation releases. This completes a coherent request/child-accounting/native-retirement module in the ongoing multi-tenant proposal. The overall proposal and broader context/learning/retention parity remain open.

## Changes and ownership

- Human admission checks the immutable Session and prior admitted Run policies before another independent root. The owning transaction repeats the check. An Instance relaxation cannot reopen a used strict Session, including one created before the strict Run was admitted and one still waiting for a child. Existing-Session Automation admission independently masks an ineligible request boundary as `automation_conversation_unavailable`, without admitting a Run or creating consent.
- Exact original principal/message/text/attachment/interruption/delivery-mode matches recover the original factual receipt after current original-login, Session/Chat and source checks. This works before and after terminal completion with stale original witnesses. Changed body or delivery mode conflicts. Recovery creates no work, security revision, effect or file-read grant.
- `ITenantRequestIsolationStore` uses existing Session, Run, completion receipt and Sandbox lease owners. Bounded technical proof refuses pending inputs, nonterminal/uncertain work, incomplete inventory, active causal children and unaccounted completion. Quiet children use the existing skipped accounting. Report finalization consumes the saved outcome without another model call.
- `TenantSessionRuntimeHost` takes its own quiescent Session reservation after releasing the actual generation. Tenant Runtime no longer attempts an ordinary active-claim save after its Run has already completed. The service and transaction repeat the ownership/graph/revision/resource proof; no completed Run regains a write claim.
- An exact existing Sandbox lease enters retirement and remains pending until confirmed through its existing reconciler. An existing retirement reason is preserved. Native Browser retirement repeats the canonical proof under native gates, closes only the owned view and preserves siblings/profile bytes. A cold probe does not bind or claim a namespace. This module does not delete working files.
- No-op scans leave admission untouched. A completed request marks only in-flight dispatch attempts for that Session; it does not advance the global Session-write epoch. These fences live in the existing bounded-by-in-flight dispatch registry, not a second execution owner or retained per-Session cache.
- The completed/ended metadata event uses ordinary recipient-authorized `session.state.changed`, with idle input/output and no stream token. It carries no retired response authority. Reconnect reads durable terminal state. Later text gets a normal `fresh_session_required` command rejection, rather than an unhandled Hub exception.

No table, migration, HTTP route, protocol version, UI component, CSS, theme, provider DTO, second queue or conversation controller was added. The new bounded recovery worker invokes the same canonical host reservation and store transaction. Existing tenant lifecycle event production remains atomic with completion.

## Focused verification

Local logs: `/private/tmp/tenant-request-isolation-verification/`. **84 distinct focused cases passed**, plus **one native Synthetic browser journey**. Repeated corrections/confirmation runs are not counted twice.

| Check | Result and observed behavior |
| --- | --- |
| Request/Automation qualification | **21 passed**: both-store normal one-request admission, pre/post-terminal exact retry, changed body/mode rejection, existing Session plus later strict pin, live relaxation, unconfirmed lease refusal, wrong owner/tenant/revision, concurrent settlement, actual SQLite host recreation and scheduled recovery, no-op scan against an unrelated claim held after snapshot load, parent-first/child-first and quiet/non-reporting/reporting accounting, real MessagePack terminal state/retry/denial/reconnect, and used-target Automation refusal without another Run or approval. |
| Affected regressions | **57 distinct passed** across the grouped run and corrected focused recheck: Existing-Session Automation modes, attachment-only input/consent, interruption retry and runtime registry/capacity. The first aggregate was 56/57; the SQLite Run-review case timed out. Its two-store focused recheck passed. Following Session-specific fencing, the final seven-case host/Run-review/interruption aggregate passed. The other fifty cases were not redundantly rerun after that focused host correction. |
| Native Browser | **6 passed**, 14.1s, opt-in local Chromium: new cold non-allocation/namespace exclusion, current proof refusal, wrong tenant and sibling-safe retirement in persistent/ephemeral modes; affected Runtime context/page observation remains valid. |
| Native Synthetic browser | **1 passed**, final confirmation 13.8s (journey 7.9s), at 1440px and 390px: configure Stateless through canonical HTTP; send through the visible Chat composer; receive the reply and automatic read-only completion; reload retained history; use New chat and send a second request in another Session; verify separate history. No unexpected page exception, console error, failed request or obsolete owner route/header occurred. Expected GET lifecycle cancellation is classified separately. |
| Builds | Solution build and final API-test build: zero warnings/errors. Frontend TypeScript/Vite build passed, with its existing chunk-size advisory. |

The backend cases cross actual cookie/CSRF HTTP, authorization, canonical persistence and real Session Runtime. The lease fixtures stage validated retained metadata and inject later factual confirmation; they do **not** claim a live OpenSandbox termination. Browser cases use local pages and task-owned profiles. Actual MessagePack tests use loopback Kestrel and the real JavaScript client.

The new-host recovery case retains an active Session and unconfirmed lease across actual SQLite application-host disposal/recreation. It verifies the bounded candidate inventory, refuses completion before confirmation, then advances the injected clock so the scheduled worker completes the request. The original Run remains the sole accepted Run, and its exact input receipt remains recoverable. No old generation or model work is resumed.

Commands used (builds serial; no C# edits during builds):

```sh
dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet build tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Tenant_request_isolation|FullyQualifiedName~Existing_conversation_Automation_keeps&DisplayName~request-boundary'
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Existing_conversation_Automation_keeps|FullyQualifiedName~Tenant_interruption_replay_and_changed_intent|FullyQualifiedName~Tenant_public_file_only_message|FullyQualifiedName~TenantRuntimeRegistryTests'
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~TenantRuntimeRegistryTests|FullyQualifiedName~Tenant_interruption_replay_and_changed_intent|FullyQualifiedName~Existing_conversation_Automation_keeps&DisplayName~run-review'
AGENTCORE_CHROMIUM_ISOLATION_TESTS=1 dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Tenant_request_retirement|FullyQualifiedName~Tenant_runtime_browser'
cd web
NODE_OPTIONS=--no-experimental-webstorage npm run build
TENANT_CHAT_API_PORT=5096 TENANT_CHAT_WEB_PORT=5186 npx playwright test --config playwright.tenant-request-isolation.config.ts
```

The recorded browser runs used a temporary copy of the native runner with API `--no-build --no-restore` after the passing build. The permanent runner remains; the temporary copy was removed. The runner owns disposable hosts/data and stops its processes. User ports 5080/5173 were untouched. Playwright MCP navigation was attempted and returned **Transport closed**; native Playwright CLI supplied browser behavior evidence.

## Findings and corrections

The first valid canonical reproduction admitted a second independent request on **both stores**. The new guard closes that gap. Static tracing also found that the old tenant adapter reported no legacy children and an ordinary terminal save required an already-retired active claim; tenant settlement now uses the quiescent canonical path instead.

Real MessagePack found `fresh_session_required` escaping the command mapper as a generic Hub error. The mapper now returns its documented business rejection. Dispatch review found the new scanner advancing the global write fence during no-op/unrelated reads. It now prechecks readiness and fences only actual same-Session completion; the deterministic snapshot/registration race confirms an unrelated claim finishes without lease-expiry recovery. The original grouped Run-review timeout was observed under concurrent verification; that timeout alone does not prove its precise cause. Focused rechecks and the final host aggregate pass.

Fixture corrections retained production rules: CurrentRequest requires coordinated text-only setup; retained Sandbox profiles/generations must have valid IDs and evidence; Run terminality does not imply immediate host quiescence; quiet outcomes are already skipped, rather than pending reports; and a restarted reconciler may already own the retirement reason. One xUnit assertion was adjusted to the repository analyzer. No check was skipped or security constraint relaxed to obtain acceptance.

## Retired requirement and remaining work

The retired `Request_isolation_waits_for_child_and_completion_accounting_before_terminalizing` declaration is ported through eight actual canonical child cases on both stores, covering its parent-first/child-first, reporting/non-reporting and quiet variants, live relaxation with an active child, retained history and no extra report/finalization model call. The [eleven-declaration inventory](owner-credential-retirement-verification-2026-10-21.md) remains the requirement source for the other behaviors; this report does not close them by removal or analogy.

Working-file AfterTerminal/deletion cleanup, live profile/learned-memory restriction and per-customer continuity, replacement of one trusted Runtime fact section while retaining tool Evidence, full Definition-policy publication/adoption parity, rich Skill/reference authoring, mutable locale/model, draft/Drive previews, Harness/Experience/maintenance execution, remaining internal authority/schema consolidation, platform guardrails and the exhaustive proposal audit remain open. No full CI/Actions, push, production/multi-instance, physical audio, live hosted model or live OpenSandbox acceptance was requested or run. No new presentation system or visual-design acceptance is claimed: the existing Chat surface was exercised functionally, and normative design artifacts remain unchanged.
