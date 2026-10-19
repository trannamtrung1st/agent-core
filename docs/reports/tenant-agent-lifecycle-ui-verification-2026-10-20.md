# Tenant Agent Lifecycle UI qualification — 2026-10-20

This checkpoint completes the canonical human Lifecycle page for archive, restore and irreversible Agent decommission. Access retains audience/execution roles and links to Lifecycle. It advances the original multi-tenant IAM proposal; autonomous/background/event/transport integration, remaining resource families, general System guardrails/audit, historical execution reliability qualification and final obsolete-authority/schema consolidation remain open. The full goal stays active; no milestone or full proposal acceptance is claimed.

## Resulting behavior

The canonical `/t/{tenantId}/admin/agents/{instanceId}/lifecycle` destination reads coherent original-human facts: exact Agent name, current organization context, lifecycle, security/Instance revisions, independent update/deletion hints and incumbent reference/profile-reset blockers. The backend adds the raw `canUpdate` hint and fenced `organizationName` without changing deletion authority; the original-human/security fence remains after its final read. Inventory and Instance navigation expose Lifecycle; Access no longer duplicates archive/restore controls.

Archive and restore use exact saved witnesses. Archived permanent retirement uses a named danger confirmation and a fresh operation ID, explains irreversible identity/binding/local-configuration retirement and Browser/home/workspace cleanup, and preserves retained-reference blockers. Cancel writes nothing and returns focus. Every attempt retires its witness; a validated success may reload current facts, while stale, lost, malformed or failed completion requires deliberate Reload lifecycle and a new confirmation before any new command. Readback never repeats the command.

Known archive/restore/retirement acknowledgements survive a failed follow-up read. Inaccessible Agent facts and controls disappear. Committed retirement and completed/pending physical cleanup are distinct; a fresh read or full browser reload proves logical retirement only and explicitly leaves physical cleanup unconfirmed. Unmount aborts command ownership and destroys an open confirmation; late results cannot reopen the departed page. Shared panels, native responsive details and centered confirmation preserve the incumbent dark Ant Design v6 presentation, including wrapping names and controls through 320px. No CSS, theme, token, font, raster asset or second UI kit was added.

## Focused local evidence

**33 distinct passing cases**: 25 frontend unit/component/route cases, three API cases and five browser journeys. Focused repeats count once. No skipped case is counted as a pass.

| Check | Result | Observed boundary |
| --- | --- | --- |
| Lifecycle component | 13 passed | Exact archive/restore witnesses; named cancel; pending cleanup; stale explicit readback with new operation; lost successful reply without replay; reference/profile-reset blockers; update without delete; denied/malformed reads fail closed; acknowledged decommission/archive preserved on denied follow-up; open confirmation destroyed and in-flight command aborted with late completion suppressed. |
| Access component and route regressions | 12 passed | Existing scoped audience/execution-role behavior, failed/stale selection recovery, scoped-reader controls, canonical route parsing including Lifecycle. |
| Decommission HTTP | 3 passed | Actual cookie/CSRF InMemory/SQLite flows, archived current update/deletion hints, retired update false, exact retirement, sibling preservation, strict input and actual SQLite application-host recreation after committed unknown cleanup. |
| Synthetic Playwright | 5 passed | Two existing Access journeys plus three Lifecycle journeys: create/inventory navigation, archive→restore→archive, long-name confirmation/cancel/focus, 320px decommission, sibling live inventory preserved, full reload leaves cleanup unconfirmed; concurrent IAM revision409 then deliberate readback; real successful DELETE with malformed reply then no replay; real retained Session blocker; registered read-only user has no update/delete action authority. |
| Production frontend build | Passed | TypeScript and Vite output; existing large-chunk advisory remains. |

Executed commands (repository root unless stated):

```sh
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore -m:1 --filter 'FullyQualifiedName~Tenant_Agent_decommission' --nologo
pnpm --dir web exec vitest run src/features/access/TenantAgentLifecycle.test.tsx src/features/access/TenantAgentAccess.test.tsx src/features/access/accessRoute.test.ts
pnpm --dir web exec vitest run src/features/access/TenantAgentLifecycle.test.tsx
pnpm --dir web run build
```

The browser batch ran from `web/` against the owned disposable hosts:

```sh
TENANT_UI_EXTERNAL=1 TENANT_CHAT_API_PORT=6988 TENANT_CHAT_WEB_PORT=6989 TENANT_CHAT_TEST_RUN_ROOT=/private/tmp/agent-core-lifecycle-ui-5m8w1v8b pnpm exec playwright test --config playwright.tenant-agent-lifecycle.config.ts
```

A focused repeat of `--grep 'archive, restore and exact'` passed after correcting the screenshot's animation wait. The same accepted component cases passed again after removing TypeScript-unsupported Testing Library selector options. Neither repeat increased the distinct count.

## Actual MCP interaction and presentation

Playwright MCP interacted with the actual Synthetic app on the disposable InMemory API6988/Vite6989 pair: sign in, choose the test organization, create Alex from the examiner template, follow Access→Lifecycle, confirm archive, inspect archived Restore/Permanent retirement, cancel the named decommission dialog and observe focus return, then confirm decommission once. The page reported saved retirement and completed cleanup; Restore disappeared and focus moved to Reload lifecycle. Expected outcomes matched observed outcomes. This is functional execution, not screenshot/health-only evidence.

Desktop1440, mobile390 and narrow320 had no document horizontal overflow. The shared panel retained16px inner padding. Captures under `.impeccable/review/tenant-agent-lifecycle/` (local ignored evidence) include `desktop.png`, `mobile.png`, `mobile-controls.png`, `user-320.png`, `retired-320.png`, `retired-details-320.png` and `long-name-confirmation-390.png`. The controls/retired-details supplements intentionally show the inner main region scrolled; base captures show their stated top/state. MCP console reported zero errors/warnings. Actual API requests returned expected200/201; StrictMode/navigation-cancelled GETs were followed by successful reads. E2E separately stages deliberate409 and malformed completion recovery.

The one detector pass returned `[]`. A fresh finish reviewer inspected all seven captures and source and returned **disposition: ship**, with no material fixes. After the final organization-name correction, all seven captures were refreshed in place; the reviewer reopened them and returned **ship** again. The fresh documenter compared final source, all captures, PRODUCT/DESIGN and the sidecar; it found no new presentation or ownership defect and preserved the incumbent tokens/sidecar. Existing historical context/access/shared-consumer fingerprint drift was reported and left unrepaired. Active, restricted and recovery states have functional evidence but no dedicated visual-finish captures; the reviewer explicitly limits visual acceptance accordingly. The generic agent harness hosted fresh reviewer/documenter roles with no inherited build transcript.

Whitespace and relative-link/fragment checks passed:1,276 relative references across11 changed Markdown files, zero missing targets/fragments. Only the owned disposable API/Vite processes were stopped at completion.

## Corrections and limits

A final consistency check moved organization labels from the potentially stale outer context into the same fenced lifecycle projection as the Agent and witnesses; the named-confirmation test deliberately supplies a stale outer organization label. Initial frontend build exposed unsupported `exact` options in new Testing Library calls; supported exact string matching now compiles. Browser fixture assertions now wait for the closing modal before checking focus. The combined browser batch initially exhausted the unchanged10/min/IP account-login limit; traces showed an expected successful login followed by429. Later scenarios now reuse the fixture's existing authenticated cookie state, and the scoped-reader scenario clears only its isolated test cookies before signing in as the actual reader. Product rate limits were unchanged. The long-name capture initially preceded settled modal animation; the bounded evidence repair now waits for animations and captures the visible dialog.

The MCP/CLI UI hosts use disposable InMemory state with Synthetic providers, Browser and OpenSandbox disabled. The API restart case uses temporary SQLite. Native Chromium/profile cleanup, mount refusal, logout/reopen and durable tombstone qualification remain separately owned by the [40-case backend predecessor](tenant-agent-decommission-verification-2026-10-20.md); those native checks were not rerun or counted anew here. The UI pending-cleanup presentation is component-tested; no delayed physical cleanup was manufactured through the live UI. No production tenant, user catalog or browser data was reset. No live model/provider or enterprise account was called. Hosted/full CI was neither requested nor run.

[Frontend owner](../13-frontend-implementation-spec.md#tenant-agent-lifecycle), [UX owner](../19-multi-tenant-iam-ux.md#agent-lifecycle-interaction-contract), [wire contract](../14-api-and-realtime-protocol.md#tenant-agent-decommission-http).
