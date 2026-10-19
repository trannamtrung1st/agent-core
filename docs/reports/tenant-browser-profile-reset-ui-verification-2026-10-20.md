# Tenant Browser human profile reset UI qualification — 2026-10-20

The existing Agent Connections page now has an independently authorized Browser state section and exact human reset interaction. This closes the shared reset control pending after [backend qualification](tenant-browser-profile-reset-verification-2026-10-20.md); it does not complete decommission/privacy/autonomous Browser work or the full IAM proposal.

## Behavior and composition

`TenantBrowserProfilePanel` reads the actual profile facade separately from Credential bindings. It shows the current Agent name/lifecycle, profile presence, persistent-view count and historical reset facts. The facade now includes coherent `agentName` and `lifecycle` from the same current Instance/security observation. It adds no permission or unrelated metadata dependency. Credential-binding denial cannot hide independently authorized Browser facts.

The shared centered `confirmAction` names the exact Agent and organization, describes closing all persistent Browser views and removing saved cookies/sign-ins/state, and separates ephemeral contexts/other resources. Confirm sends exactly one new operation UUID plus the displayed preflight's security/Instance/native witnesses. Every returned command outcome invalidates that observation and removes the stale details/action. The user must deliberately Reload Browser state before deciding on another reset. Reload changes the query identity immediately, so old protected values cannot appear while a new read runs. Neither mutations nor result polling repeat automatically.

The adapter checks returned operation/status/reason/time correspondence before accepting a result. Confirmed, pending, unknown, refused and unreadable/lost replies have distinct safe messages. Historical confirmed reset does not assert current absence after later Browser activity. A new deliberate reset requires another confirmation and a new operation ID. Read-only, archived and busy state explains unavailable reset. Unmount destroys the confirmation and aborts its owning command/query; late results cannot move into another Agent.

The established Ant Design dark world is preserved. Existing `AgentConfigurationPanel` owns section/header/body insets; Connections owns outer section gap and bounded column. Native Flex rows wrap identity, lifecycle and actions. There is no new CSS, token, visual system, raster asset or generic control wrapper. The development-only [surface brief](../../.impeccable/surfaces/web-src-features-access-tenantagentconnections-tsx.md) records the local extension.

## Executed UI use cases

Component checks cover exact escaped names and confirmation scope, captured witnesses, one POST and deliberate readback, pending/unknown results, network failure, malformed JSON and mismatched operation response, 403 and 409 invalidation, fresh revision/operation selection, read-only/archived/busy explanations, independent binding denial, cancel/focus recovery, confirmation cleanup and late completion after an owner change. Existing binding confirmation, stale draft/removal and denied/read-only checks run as affected consumers.

The actual Synthetic browser run uses a dedicated InMemory host on API port 6768 and Vite port 6769, with every file/key/Browser path under `/private/tmp/agent-core-browser-profile-ui-phhtd6bi`. No user profile, database, credential or live provider is involved. The two reset journeys exercised:

- Exact named confirmation and cancellation/focus at 1440px, real canonical reset, explicit GET readback and no additional POST. At 390px a route fault makes the real successful server reply unreadable; UI reports unconfirmed, then GET reads the actual saved confirmed operation. The two deliberately submitted operation IDs differ and readback dispatches nothing.
- At 320px, a real role/security change after opening confirmation makes the captured reset stale. Actual POST returns 409 and no reset state is saved. Explicit reload plus a new confirmation succeeds; archiving the actual Instance then leaves profile facts readable and reset disabled.

Both incumbent Connections journeys also passed: paged exact Credential draft/save/remove, narrow focus/discard, stale removal and archived inspection; plus scoped configuration management without IAM-inspector permission and protected-data loss after revocation.

Playwright MCP independently used the same verified disposable Synthetic host and a separate test-owned tenant/Agent. Actual visible controls exercised Cancel returning focus to Reset, one confirmed reset returning focus to Reload, deliberate saved-fact readback, a controlled security change causing 409, and recovery by GET with no repeated reset. Normal flow had no console errors. The deliberate stale POST produced one expected 409 resource-console entry. The StrictMode initial GET was cancelled, and a transient busy observation was recovered with the exposed Reload action; it initiated no reset.

## Rendered evidence

One batched desktop/mobile/narrow capture round inspected the Connections consumer, profile panel, long-organization confirmation, historical confirmed result and stale refusal. Every file was opened and checked for valid rendered content. Desktop is 1440×1000, mobile 390×844 and narrow 320×740. The app uses an internally scrolling MAIN: mobile overview is captured at its top and the separate profile capture deliberately scrolls that main area to show the whole panel/action.

Captures remain local under `.impeccable/review/browser-profile-reset/`: `desktop.png`, `desktop-confirmation.png`, `mobile.png`, `mobile-profile.png`, `narrow-confirmation.png`, `narrow-refused.png`. No horizontal document overflow was observed. The narrow confirmation measured x=8, width=304, top=151, bottom=589 within 320×740. Computed shared panel heading padding is 16px and body sibling gap 12px. Buttons, long organization text and notices wrap without clipping the destructive scope or recovery action.

The independent finish reviewer returned **disposition: ship** in its persistence, fidelity, ceiling, material-fixes and keep sections. No material fixes were requested. The separate documenter inspected the implementation, shared layout/theme/CSS owners, surface brief and representative captures: **existing system preserved**, no system-file changes required. PRODUCT.md, DESIGN.md and the design sidecar remain intact. Its pre-existing prohibition on a live Browser progress panel is scoped by the canonical administrative profile-management contract; no unrelated context drift was rewritten.

## Commands and results

- `pnpm --dir web run test --run src/features/access/TenantBrowserProfilePanel.test.tsx src/features/access/TenantAgentConnections.test.tsx` — **18 passed**, two files.
- `pnpm --dir web run build` — **passed**, strict TypeScript and Vite. Existing large-bundle advisory remains separate from this bounded change.
- From `web/`: `TENANT_UI_EXTERNAL=1 TENANT_CHAT_API_PORT=6768 TENANT_CHAT_WEB_PORT=6769 TENANT_CHAT_TEST_RUN_ROOT=/private/tmp/agent-core-browser-profile-ui-phhtd6bi pnpm exec playwright test --config playwright.tenant-browser-profile.config.ts` — **4 passed** (two reset, two incumbent Connections).
- `AGENTCORE_CHROMIUM_ISOLATION_TESTS=1 dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore -m:1 --filter FullyQualifiedName~Tenant_Browser_profile_HTTP_reset --nologo` — **12 passed**, no skips; both-store actual native closure/deletion/recovery now also assert coherent Agent identity/lifecycle in preflight.
- Impeccable detector ran once against the three changed product TSX files — **`[]` findings**.

These are **34 distinct focused test cases** at this checkpoint; the backend cases also belong to the earlier 117-case backend qualification and are not new cumulative coverage. Initial checks caught two incorrect test focus matchers at TypeScript/test time, then one confirmation visibility assertion before the native modal settled. The fixture now uses the proper focus matcher and waits for visible content; no product runtime failure was observed in the final matrix.

Whitespace checks passed; **1,273 relative links across eleven changed Markdown files** resolve without missing targets/fragments. Both disposable test-server ports were released after verification.

## Limits and remaining work

The UI's actual host starts without a live native Browser, so the browser journeys reset a cold disposable profile. Actual warm shared-context closure, retained localStorage removal and physical unknown outcomes remain qualified by the separate native/backend matrix. Pending/unknown rendering, permission denial and malformed-result variants are component checks; the actual lost-reply journey executes a successful backend reset before modifying only its response. Stale/archived UI behavior is exercised against real current canonical state. No native-failure debug endpoint is introduced.

No Real provider, live user account/profile, hosted CI, full CI suite or milestone acceptance is claimed. The host and Vite processes started for this qualification are stopped after verification. Browser decommission/privacy/autonomous adapters, other resource/effect/cleanup, voice/rich Chat/live input, autonomous/background/event/transport, platform/system guardrails, general authorization audit/reconciliation/notification and final isolation/UX/obsolete-authority/clean-schema cutover remain open. The original full goal stays active.
