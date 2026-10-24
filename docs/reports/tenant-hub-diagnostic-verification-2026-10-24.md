# Tenant Hub diagnostic boundary qualification

Date: 2026-10-24. Preimage: `e2e28570`. This closes the unexpected SignalR application-fault gap recorded in the [previous diagnostic checkpoint](tenant-diagnostics-isolation-verification-2026-10-24.md); full proposal acceptance remains open.

## Reproduced failure and correction

A real JSON/WebSocket client authenticated, created an owned Agent/Session, then encountered an injected unexpected conversation-store failure during `AttachTenant`. The client received a generic error, retried on the same connection and completed/deduplicated one conversation Run. The server's SignalR failure log nevertheless rendered the original exception's private URL/token/body sentinels. The baseline regression failed on that rendered exception in 3s. This was an observed disclosure, not an inference from source or a client-error-only check.

`SessionHubDiagnosticFilter` uses SignalR's existing API filter hook to cover unexpected invocation and connection-lifecycle faults. The existing `DiagnosticLog` sink receives the original exception and emits its bounded content-free causal snapshot. SignalR receives a new generic `HubException` containing the same diagnostic ID, with no original inner exception. Its framework log and client serialization therefore retain no original exception payload. Closed-connection cancellation produces a safe close message without allocating a failure reference.

The filter stores no invocation state, logger factory, service provider, authorization decision or work. It uses the existing singleton diagnostic ID source. It never admits, retries, grants or cancels an operation. Known authorization, validation, conflict and cancellation CommandAck paths remain with the existing Hub/services.

Connection attribution is captured before awaiting the invocation/lifecycle callback. Attach can refine it only after resolving the actual authenticated tenant membership. The per-Hub refinement and trusted live subscription supply tenant/principal IDs; unverified command identity hints, caller claims and arbitrary Items do not. Capturing the original connection context also avoids relabeling an in-flight command when another attachment changes the connection. These identifiers are diagnostic facts, not authority or metric labels.

No public Hub method, route, DTO shape, protocol version, permission, approval default, Run state machine, schema, provider or UI changes. The original fourteen-method Hub surface remains exact. No alternate dispatch or execution framework is added.

## Actual wire and recovery evidence

The new `tenant-hub-diagnostics.mjs` fixture and `Unexpected_tenant_Hub_fault*` run on InMemory and SQLite over both JSON and MessagePack, using loopback Kestrel and the real JavaScript SignalR client:

1. Sign in through current HTTP APIs, select the bootstrapped tenant, create its Agent/Session, connect with the Account cookie and submit Attach with an ignored forged principal hint.
2. Raise an unexpected nested HTTP/I/O failure from the owning conversation-store read. Assert failed Attach publishes no ready snapshot and leaves Session revision at one.
3. Check every captured warning/error record's message, complete exception rendering and structured fields for absence of all three sentinels. Verify one application diagnostic reference appears in the client error, with the actual tenant, authenticated principal and target Session, and useful original exception call-site evidence.
4. Retry Attach on the same connection, receive the current ready snapshot, submit a normal user turn and receive completed Synthetic output. Repeating the accepted command returns the same Run; canonical history contains exactly one user and one assistant entry.

All four store/protocol combinations pass. All use Synthetic adapters and make zero outbound provider HTTP calls. They qualify this application exception boundary, not arbitrary malformed transport parsing, external provider acceptance or rendered SPA behavior.

Five lifecycle cases verify safe connect/disconnect failures, bounded causal evidence and one reference, omission of unverified caller metadata, no failure ID for aborted cancellation, and successful teardown preserving its internal close reason without logging it. Existing Hub refusal/admission, actual voice, isolation/reconnect, HTTP privacy and hosted diagnostics provide the grouped regression below.

## Focused results

**29 distinct passing cases**, zero remaining failures or skips. The first four passing wire cases repeat in the final grouped run and are not counted twice.

| Check | Observed result |
| --- | --- |
| Baseline real-wire reproduction | **1 expected failing case**, 3s, before the fix: private sentinel in SignalR's rendered server exception. |
| New both-store JSON/MessagePack use case | **4 passed**, 23s, with same-connection recovery, actual attribution and dedupe. |
| Final grouped API qualification | **29 passed**, 1m37s, including the four cases above, five lifecycle cases, five original Hub surface/cutover cases, four actual voice transport cases, two request-isolation/reconnect cases, two recognition-failure recovery cases, four HTTP sink cases and three hosted diagnostic cases. |
| Solution build | **Passed**, zero warnings/errors, 28.61s. |
| Final API test-project build | **Passed**, zero warnings/errors, 10.92s, after lifecycle-only test additions. Product sources are unchanged from the passing solution build. |
| JavaScript syntax and whitespace | **Passed**. |

Build: `dotnet build AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false`. Test commands use `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo`. The primary filter is `FullyQualifiedName~Unexpected_tenant_Hub_fault`. The final filter selects `SessionHubDiagnosticFilterTests`, `Tenant_Hub_`, `Tenant_voice_real_JavaScript_MessagePack`, `Tenant_request_isolation_actual_MessagePack`, `Unexpected_tenant_HTTP_failure`, `DiagnosticLoggingTests`, `Tenant_voice_failed_recognition_worker`. `Tenant_Hub_` also matches the new wire method, explaining the repeated initial cases.

Logs: `/private/tmp/tenant-hub-diagnostic-reproduction-20261024.log`, `/private/tmp/tenant-hub-diagnostic-wire-qualification-20261024.log`, `/private/tmp/tenant-hub-diagnostic-regressions-20261024.log`, and matching build logs. All handles are terminal; native test hosts and JavaScript clients are fixture-owned. No installed-data/log reset, Real-host restart, provider-key call, CI, push or PR was performed.

## Remaining proposal acceptance

The [delivery ledger](multitenant-iam-delivery-2026-10-16.md) still requires complete store/effect/authority reconciliation and final proposal-wide security/isolation/function and rendered UI/design acceptance. This result closes its named Hub gap without narrowing those requirements.
