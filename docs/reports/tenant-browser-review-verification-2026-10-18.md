# Tenant Browser durable Review prerequisite verification — 2026-10-18

The proposal remains active. This checkpoint implements internal typed Browser Review preparation, scoped stored inspection and current human decisions. It does not enable a model Browser effect, public preparation endpoint, dispatch/recovery or result delivery.

## Implemented boundary

- The current claimed Run supplies the actual canonical tenant/Instance/executor/Session. Raw Session and Browser reads plus the derived primary permission precede target observation. Both admitted and current Definition capabilities, current source ownership and effective isolation remain required. Preparation observes an already bound native target without allocation, navigation, automatic binding or isolation transitions.
- Exact normalized arguments, typed command hash, canonical semantic target and display evidence persist through the existing approval transaction. Sensitive navigation/closure/mutations require `browser.use`; true inspection uses `browser.read`. The legacy generic navigation effect label cannot lower tenant permission. Unconverted Credential, Artifact/capture/upload, coordinate mouse and geolocation adapters remain closed.
- Review never grants a role or ExceptionalGrant. Current secondary read policies and independent reviewer scopes are checked alongside the primary action. Both stores repeat owner/source/capability/raw permission/reviewer checks atomically. Read/use/Session inventory filters apply before totals and paging.
- Root suppression correlates Session/tool/typed command without mutable native targets or Instance revisions. Exact action hashes still bind those target/revision witnesses. Durable metadata excludes transient provider epochs and live SDK objects; the separate physical witness remains single-use. Current target drift blocks decisions; cancellation retains its separate authority.
- Existing approval HTTP and the conversation feed name `browser_command` explicitly. The shared inspector displays readable effective parameters including defaults, origin-only metadata, state sharing, consequence and unavailable execution. Canonical JSON and native target identities remain outside the primary projection. No layout/CSS/token/UI-kit change was introduced.
- `TenantApprovedOperationService` still rejects this non-local family with `tenant_effect_unavailable`. Approval produces neither Browser dispatch nor an effect receipt or grant. The existing tenant model tool gate remains closed.

## Executed flows

Both InMemory and disposable SQLite exercised preparation → stored wait → reopen → current review → exact decision → one continuation signal. Expected and observed: exact consent survives reopen; no grant or Browser receipt appears; the local operation executor refuses Browser dispatch. Changed physical target/mode or revoked raw executor authority disables decision, produces no decision signal and permits independently authorized cancellation.

Scoped reviewers initially lacked Browser read and/or the actual Session. Expected and observed: zero visible rows and zero total, even with inbox/use authority; a sibling-agent read did not help. Exact reads restored visibility. A stricter independent Session-read policy additionally required approval authority on that Session and excluded the original requester. Revocation hid the row again; restoring current authority enabled the separate qualified reviewer.

Read-only Review required no Browser-use grant. A busy preflight created no request or Run wait; subsequent current preparation recovered. Strict argument parsing rejected duplicate/unknown fields, malformed JSON and unconverted adapters. Canonical rebuild rejected forged read-only/command hashes. Disposable migration preserved an actual prior-family request payload byte-for-byte, retained foreign-key integrity and rejected mismatched Browser kind/boolean/Session/target JSON or overlapping typed payloads. No installed database was migrated.

Four native HTTP cases used actual disposable headless Chromium and its loopback fixture, with both stores and unchanged/drifted targets. Trusted fixture warmup was explicitly separate from a model effect. Expected and observed: the saved origin omitted URL path/query, preparation/review/decision left the native command revision unchanged, a competing native command invalidated approval, exact current consent succeeded, conversation-feed identity was correct, anonymous reads failed, and grants/effect receipts stayed absent. Existing exact-target/dialog/concurrency and canonical approval/tool regressions remained green.

## Focused results

Final selections passed **36 Infrastructure cases, 5 API cases and 14 frontend cases**, with no skips or failures. The 5 API cases include the four native cases above and the existing canonical approval HTTP/CSRF/revision regression. The frontend production build passed; Vite retained its existing large-chunk warning. Earlier overlapping runs are not double-counted.

```sh
AGENTCORE_CHROMIUM_ISOLATION_TESTS=1 dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --filter 'FullyQualifiedName~TenantBrowserApprovalTests|FullyQualifiedName~TenantBrowserOwnershipTests|FullyQualifiedName~TenantOperationAuthorizationTests.Durable_approval|FullyQualifiedName~Tenant_Sandbox_read_Review_consumes_exact_status|FullyQualifiedName~Shared_tool_boundary_uses_actual_executor' --disable-build-servers -p:UseSharedCompilation=false -m:1 --logger 'console;verbosity=normal'
AGENTCORE_CHROMIUM_ISOLATION_TESTS=1 dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --filter 'FullyQualifiedName~Tenant_Browser_Review_HTTP_|FullyQualifiedName~Canonical_durable_approval_HTTP_' --disable-build-servers -p:UseSharedCompilation=false -m:1 --logger 'console;verbosity=normal'
cd web
pnpm exec vitest run src/features/access/TenantApprovals.test.tsx
pnpm run build
```

Final logs are `/private/tmp/agent-core-browser-review-final-infrastructure-20261018.log`, `/private/tmp/agent-core-browser-review-final-api-20261018.log`, `/private/tmp/agent-core-browser-review-ui-tests-20261018.log` and `/private/tmp/agent-core-browser-review-ui-build-20261018.log`.

The first integrated flow exposed the legacy navigation classification mismatch and was corrected. Initial local test execution could not bind the runner socket; the same focused tests ran successfully with local permission. EF's initial restore/build was stopped by exact owned process IDs and retried through a successful no-restore build followed by `dotnet ef migrations add ... --no-build`. Fixtures now explicitly register built-in Definition authority when exercising current source checks, matching application DI. Initial test-only compile/matcher and nonexistent-sibling fixture errors were corrected; no failed or aborted attempt is counted as passing evidence.

## Shared UI runtime evidence

Playwright MCP exercised a disposable Synthetic InMemory host at `5098` and Vite at `5198`, with explicit temporary storage roots. Health confirmed `profile=Synthetic`. Real sign-in and organization navigation succeeded. Browser requests remain unavailable from the model, so the Browser approval GET/cancel responses were explicitly controlled presentation fixtures; this was not backend authorization evidence.

The incumbent inspector displayed named origin/state/parameters/consequence/availability fields and long wrapping parameters. At 1440×1000, 390×844 and 320×568 the document/command region had no horizontal overflow, the footer remained reachable and the body retained its shared 16px section gap. Native responsive transitions were allowed to settle before measurement. Refreshed target drift disabled Approve while preserving Cancel. Opening/abandoning confirmation sent no decision. Confirming cancellation sent exactly one saved action/revision object, showed a separate cancellation acknowledgement and removed the approval CTA. Console errors/warnings: zero. Development StrictMode/navigation cancelled obsolete GETs (`ERR_ABORTED`) and the replacement reads succeeded; there were no unexplained command failures.

Local screenshots are `.playwright-mcp/browser-review-1440-20261018.png` and `.playwright-mcp/browser-review-390-20261018.png`; host logs are under `/private/tmp/agent-core-browser-review-ui-20261018-2HyMz4`. These are local verification evidence, not shipping raster assets. Final execution-availability copy was checked in the running inspector. The shared provider-unavailable message remains provider-neutral for other approval families. Owned API/Vite processes were stopped after verification. The presentation verdict for this existing shared composition is ready; this is not complete Browser product acceptance. DESIGN.md records component reuse. Impeccable reported that its design sidecar predates DESIGN.md; the sidecar was not regenerated as an unrelated repair.

## Scope and remaining work

No source/test edits occurred while build/test/EF processes were live. Tests used disposable local data/Chromium only. No user profile/data reset, Real host restart, external account/provider call, installed-data migration, full CI suite, hosted CI or deployment was performed. No milestone or full-proposal acceptance is claimed.

Browser dispatch/effect settlement/recovery, model offering/continuation/results, protected Credential filling, capture/download/Artifact delivery, privacy/reset/decommission and autonomous admission remain open. Other proposal resource/effect families, Governance/platform, voice/rich Chat, live input arbitration, obsolete authority/schema consolidation and the historical SQLite preference reliability follow-up also remain open. Exact native targets do not prove the business meaning or reversibility of arbitrary Browser actions.
