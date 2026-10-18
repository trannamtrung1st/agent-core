# Tenant Browser reviewed dispatch and recovery verification — 2026-10-18

The full proposal remains active. This checkpoint adds internal exact reviewed Browser dispatch, factual settlement and abandoned-attempt recovery. Model offering, durable observation/checkpoint delivery and complete Browser product acceptance remain open.

## Implemented boundary

- `TenantBrowserEffectService` reuses existing Review, actual Session/Run/Instance ownership and the generic operation receipt. Atomic start persists InFlight on the existing Run with original claim generation/start revision/start UTC, security revision and dispatch audit. No Browser ledger, queue, table, column, index or migration is added.
- Current raw compound Session/Browser permissions, admitted/current capabilities, isolation, exact source/reviewer/requester and semantic target are checked before start. A nonserialized Core callback repeats current authority/claim/expiry inside incumbent native gates before physical witness consumption. Disabled hosts cannot execute or consume the target. Consent creates no grant.
- Dispatch is bounded by remaining claim/approval lifetime, admitted per-tool and overall checkpoint budgets, including preparation time. Expiry after committed start can deny before calling the Browser port. Cancellation seals uncertainty independently before returning; late noncooperative completion cannot promote the saved receipt.
- SDK-confirmed completion, no-start denial and unknown attempt are separate immutable facts. Completed means the command completed, not that its business effect was independently verified. Transient observations need current post-I/O authority and the same uncancelled live claim, fenced again after access reads; Bytes/Downloads are stripped. Receipt replay returns no observation and grants no provider authority.
- Recovery reads matching owned original fences and records Indeterminate without native I/O, including lease expiry and retained deleted-Session ownership. Terminal/new frames stay terminal. Generic Run origin metadata is also preserved for the existing credential-check fence; related regressions are included.
- The shared Ant Design inspector uses Command completed and explicit application-verification guidance, or unknown/check-current-state/no-replay guidance. It adds no component, CSS, token or UI kit. Existing refresh rereads details only.

## Executed backend flows

Both InMemory and disposable SQLite exercised exact prepare → stored Review → human decision → resumed claim → dispatch → receipt → repeat/reopen. Expected and observed: one effect, one immutable receipt, no ExceptionalGrant, and no replayed observation or native action. Concurrent duplicate callers shared facts only; only one returned fresh observation. Native snapshot acceptance could not release an in-flight durable operation fence.

Revocation after durable start was detected at the dispatch gate: no effect, a saved denial and no repeat. Unknown SDK completion retained Indeterminate. Revocation after I/O preserved the factual Completed receipt while withholding observation data. Run cancellation during I/O likewise retained Completed facts, left cancellation in place and returned no late observation; duplicate access did not execute again. Caller cancellation returned only after an unknown outcome was sealed; a late provider success did not upgrade it. Lease recovery and cancelled/deleted Session recovery sealed retained attempts once with no provider call. Fake-time consent expiry immediately after committed start produced one no-dispatch denial and zero Browser-port invocations on either store, including repeat calls.

Six native HTTP cases used actual disposable headless Chromium and loopback pages, both stores and explicit disposable profile roots. Trusted owner binding/cold preparation allocated no context. Cookie-authenticated exact approval plus a resumed claim and the internal service navigated once, and scoped approval reads exposed the matching receipt. Duplicate dispatch did not execute again or return observation. Revocation between persisted start and native entry blocked the command; an admitted one-second per-tool budget bounded a paused call with no context allocation/command consumption. The budget outcome conservatively remained Denied or Indeterminate depending on whether cancellation raced entry; neither permitted replay. These tests call the internal effect owner explicitly; no public dispatch endpoint or model tool is enabled.

Actual native ownership/target tests also proved Core callback denial and a disabled host prevent closure without consuming the physical witness. Existing exact target, dialog, replay, ownership, credential-fence and closed tenant tool-boundary regressions remained green.

## Focused checks

Final selections passed **61 Infrastructure, 6 API and 16 frontend cases** with no skips or failures. The frontend production build passed with the existing Vite large-chunk warning. The two initial expiry cases are included in the final 61, and earlier overlapping runs are not double-counted.

```sh
AGENTCORE_CHROMIUM_ISOLATION_TESTS=1 dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --filter 'FullyQualifiedName~TenantBrowserApprovalTests|FullyQualifiedName~TenantBrowserOwnershipTests|FullyQualifiedName~Approved_runtime_credential_check_rotates_protected_grant_once_with_durable_receipt_and_reopen|FullyQualifiedName~Abandoned_credential_dispatch_recovers_after_lease_or_deleted_session_without_provider_replay|FullyQualifiedName~Concurrent_exact_credential_check_callers_share_only_the_saved_receipt_and_never_provider_authority|FullyQualifiedName~Caller_cancellation_settles_an_uncertain_dispatch_before_returning_and_ignores_late_provider_success|FullyQualifiedName~Shared_tool_boundary_uses_actual_executor' --disable-build-servers -p:UseSharedCompilation=false -m:1 --logger 'console;verbosity=normal'
AGENTCORE_CHROMIUM_ISOLATION_TESTS=1 dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --filter 'FullyQualifiedName~Tenant_Browser_effect_HTTP_' --disable-build-servers -p:UseSharedCompilation=false -m:1 --logger 'console;verbosity=normal'
cd web
pnpm exec vitest run src/features/access/TenantApprovals.test.tsx
pnpm run build
```

Logs: `/private/tmp/agent-core-browser-effect-final-infrastructure-20261018.log`, `/private/tmp/agent-core-browser-effect-native-http-20261018.log`, `/private/tmp/agent-core-browser-effect-ui-tests-20261018.log` and `/private/tmp/agent-core-browser-effect-ui-build-20261018.log`. The isolated expiry setup/result is in `/private/tmp/agent-core-browser-effect-expiry-20261018.log`.

Initial recovery assertions incorrectly used the pending request revision and assumed terminal cancellation had changed the factual InFlight disposition. Fixtures now use the approved revision and distinguish cancellation from lease recovery while asserting preserved origin and immutable settlement. Initial budget fixtures were corrected to pin the one-second execution budget at admission. Those failed/setup attempts are not counted as passing checks. A final review additionally found that transient output needed the same uncancelled current claim after I/O. That fence and a both-store cancellation regression were added, then the affected final selections were rerun. No source/test edits occurred while build/test/EF processes were live.

## Shared inspector runtime evidence

Playwright MCP used a disposable Synthetic API at 5098 and Vite at 5198, with storage/logs under `/private/tmp/agent-core-browser-effect-ui-20261018-XLYQWv`. Health confirmed Synthetic. Actual sign-in and organization navigation used the running shell. Because model offering/delivery remains closed, approval GETs were controlled presentation projections with separate Completed and Indeterminate request IDs; these projections do not prove backend authorization or dispatch.

At 1440×1000, 390×844 and 320×568 both receipts showed the appropriate distinct guidance, no approval/replay CTA, no document/result horizontal overflow and a reachable footer. Refresh retained the unknown outcome and sent zero decision POSTs. Console errors/warnings: zero. Development StrictMode/navigation aborted obsolete GETs and replacement reads succeeded; no unexplained command error occurred. Desktop completion and mobile uncertainty were visually inspected in the incumbent dark native Drawer. No additional layout repair was needed.

Local screenshots: `.playwright-mcp/browser-effect-completed-1440-20261018.png`, `.playwright-mcp/browser-effect-completed-390-20261018.png`, `.playwright-mcp/browser-effect-indeterminate-1440-20261018.png`, `.playwright-mcp/browser-effect-indeterminate-390-20261018.png`. These are verification rasters, not shipping assets. Owned API/Vite/browser resources were stopped after verification. DESIGN.md records shared result wording/component reuse.

## Limits and open work

No installed database/profile reset or migration, Real-host restart, external account/provider spend, CI/full-suite run or deployment was performed. Tests used disposable stores, local fixtures and explicit native Chromium opt-in. No milestone or full-proposal acceptance is claimed.

The generic local operation service still rejects a first Browser dispatch, and tenant model Browser tools/authorization intents remain closed. Wiring this internal owner into the existing continuation, offering Allowed reads, durable bounded output/checkpoint delivery and restart continuation remain open. Receipt-only replay cannot reconstruct transient observations. Protected Credential fill, captures/downloads/Artifact delivery, privacy/reset/decommission and autonomous origins remain open. Other proposal families, Governance/platform, voice/rich Chat/input arbitration, obsolete authority/schema consolidation and the historical SQLite preference reliability follow-up also remain open. Arbitrary Browser commands still require separate evidence of business success or reversibility.
