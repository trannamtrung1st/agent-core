# Gmail OAuth and main observability integration — 12 October 2026

Behavior commit: `947177e2cce6380b49e546fccde05561806a3b72`. Local integrated verification passed. The required hosted gate is the seven-job [PR #11 check set](https://github.com/trannamtrung1st/agent-core/pull/11/checks); the final PR description records its exact run, commit and outcomes. The evidence follow-up changes documentation only, with executable/test/configuration source parity checked against this behavior commit.

## Main synchronization

Fetched current main at `996e78fbe492ef351756030a8d35b457ad618e1c`. It was already an ancestor of the starting branch `4a722e8e`; no incoming commits or unresolved merge conflicts remained. The earlier Gmail/main reconciliation retained account-scoped REST while preserving incoming factory diagnostics, safe causal exception snapshots, warning scopes, trace failure marking and the bounded 1,024-sample statistics window. Model-catalog fixtures now use main's exact diagnostic registration spelling, eliminating the equivalent import/type qualification diff.

This follow-up fills the remaining integration gaps in Gmail reads, temporary refresh failures and unconfirmed remote revocation. It adds bounded operation monitoring through the existing Runtime meter/source. Custom transport, binding/approval/dispatch authority and durable receipt behavior remain covered by the integrated tests.

## Executed local verification

| Check | Result |
| --- | --- |
| Infrastructure Gmail/OAuth/Custom/durable-send/transport-diagnostics/model-catalog slice | 100 passed |
| Application shared logging, safe traces, bounded statistics and email metrics | 38 passed |
| API owner/public OAuth callback and Email Selection runtime | 13 passed |
| Main ancestry, conflict/whitespace checks | Passed |

```sh
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~GmailOAuthWorkflowTests|FullyQualifiedName~GoogleEmailOAuthTests|FullyQualifiedName~GmailEmailProviderTests|FullyQualifiedName~ProviderTransportDiagnosticsTests|FullyQualifiedName~ImapSmtpEmailProviderTests|FullyQualifiedName~EmailAccountIntegrationTests|FullyQualifiedName~ModelCatalogFactoryTests' --nologo -m:1
dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --filter 'FullyQualifiedName~EmailTelemetryTests|FullyQualifiedName~HandledFailureLogTests|FullyQualifiedName~RuntimeTelemetrySurfaceTests|FullyQualifiedName~SafeExecutionTraceTests' --nologo -m:1
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter 'FullyQualifiedName~GoogleEmailOAuthApiTests|FullyQualifiedName~EmailSelectionRuntimeTests' --nologo -m:1
```

- The InMemory/SQLite workflow connects, binds two agents, reopens the original protected key ring, refreshes without changing the approval hash, reads, sends one exact-approved message, replays its receipt without another dispatch, and disconnects every binding. Expected results/state effects were observed.
- Permanent refresh rejection produces ReconnectRequired; temporary rejection keeps the grant. The owner check receives the originating refresh diagnostic ID with one warning and no second ID allocation. Disabled warnings allocate no ID.
- A transport exception containing known token/address/body strings becomes a safe Gmail error with the same warning reference. Those strings are absent from the snapshot and metric labels. Caller-cancelled reads propagate cancellation quietly.
- Failed remote revocation logs a safe status/reference after local disconnect clears the protected grant. The response truthfully says revocation is unconfirmed and does not restore authorization.
- HTTP rejection retains its machine status in the shared snapshot. HTTP success without an accepted message ID is logged as missing_message_id, stays Indeterminate, and makes one send attempt. Custom SMTP and durable uncertainty/replay assertions still pass.
- Metrics distinguish operation outcomes, Synthetic/Real, cached authorization and throttled checks. Unknown labels normalize to other; invalid durations normalize to zero. Credential/execution IDs belong only in traces/logs; token, mailbox, query, recipient, MIME and body values are absent from labels/timeline details.

The first 49-case run had three assertion failures because HTTP status moved from an uncorrelated warning property into the shared causal snapshot. Assertions now require that exact status plus a diagnostic ID; dispatch/content assertions remain intact. The final 100-case suite passed. No assertion, timeout or Synthetic security check was weakened.

## Scope and evidence limits

The UI is unchanged; the earlier [responsive OAuth verification](gmail-oauth-verification-2026-10-12.md) remains historical evidence. Fresh hosted verification covers backend, frontend, core browser, acceptance and Compose jobs for this combined candidate. Physical devices and live Google consent/delivery are not claimed verified. Real Gmail UAT still requires operator application setup and dedicated-mailbox/send authorization. No real email was sent and no user database or process was reset.
