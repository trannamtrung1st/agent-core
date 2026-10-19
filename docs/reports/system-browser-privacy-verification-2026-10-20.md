# Canonical System Browser privacy qualification — 2026-10-20

This checkpoint converts the existing global screenshot policy from owner-capability Admin authority to canonical System governance. It completes this module within the original multi-tenant IAM proposal. Profile decommission, autonomous Browser adapters, other resource/lifecycle/transport/background families, general System guardrails/audit, final isolation and obsolete-authority/schema consolidation remain open. The full goal stays active; no milestone or full proposal acceptance is claimed.

## Implemented behavior

Authenticated GET/PUT `/api/v2/system/browser/privacy` derives the original Account/session and actual global User Principal. Both require raw `system.governance.manage`; tenant Owner, tenant agent and the old owner header cannot substitute. Both stores repeat live Account/status/session/stamp/global Principal, central system permission, deployment ceilings and command validity inside the existing BrowserPrivacy/canonical AuthorizationAudit transaction. Revision CAS and audit identity prevent partial or duplicate commits. Audit identifies the real global human, host target and safe action/reason without origin values or policy bodies. Runtime policy storage is read-only outside this canonical writer; the former LocalOwner/Admin-history save and `/api/v2/admin/browser/privacy` mapper are removed.

The singleton schema retains its existing owner. Migration `20261020080000_SystemBrowserPrivacyAuthority` adds bounded closed JSON, revision, origin and deletion guards without a new table/column/model. Empty downgrade/upgrade preserves model parity; populated downgrade refuses. Saved policy remains distinct from the effective startup policy. Saves never hot-switch native privacy, restart a host or delete existing Artifacts. A later startup still constrains saved policy by immutable deployment ceilings.

The existing Ant Design editor now lives at `/system/browser-privacy` in System / Platform, outside every organization. Actual current system permission gates navigation and server reads independently. Unknown System routes fail closed; the old Admin page has no alias. Every Unmasked save confirms global scope, exact deployment-approved origins and exposure. Stale, uncertain or malformed completion blocks further saves until deliberate readback, retains the draft and never replays PUT. Lost access clears protected policy facts/controls. The shared navigation guard retains Stay/Discard behavior. The incumbent shell, panel, form, details, confirmation and spacing owners remain.

## Focused verification

All checks were local and key-free. Distinct passing cases: **73** (26 Infrastructure, 3 API, 40 frontend unit/route/component, 4 browser journeys). Focused repeats are not counted twice.

| Check | Result | Boundary exercised |
| --- | --- | --- |
| Infrastructure filter `SystemBrowserPrivacyTests` and `BrowserPrivacyTests` | 22 passed, no skips | Both stores; logout/password rotation at the facade-to-store boundary; wrong/global/tenant actor denial; immutable ceilings/acknowledgement; concurrent CAS winner; real body-free audit; SQLite raw guards/down/up/reopen; startup constraining; actual native Chromium pixel/redaction/frame/retirement behavior retained. |
| Infrastructure IAM filters `Same_account_has_distinct_tenant_authority_and_no_platform_or_agent_shortcut` and `Password_rotation_revokes_all_sessions_and_platform_suspension` | 4 passed | Existing central Account/system permission and rotation/suspension behavior in both profiles. |
| API filter `System_Browser_privacy_HTTP` | 2 passed | Authenticated cookie/CSRF HTTP in InMemory and SQLite; strict body/modes/origins; ceilings/ack; saved/effective distinction; no-store; tenant-only Owner403; anonymous/old-owner denial; retired route404. |
| `IdentitySqliteRestartTests` | 1 passed | Real production Synthetic dependency composition, cookie and tenant authority plus Instance identity retained across SQLite host recreation. |
| Privacy component and app/access route files | 39 passed | Nine editor cases, 22 app routes and eight access routes, including denial, lost/stale completion, draft retention and route ownership. |
| Existing Admin effective-configuration link test | 1 passed | Read-only provider diagnostics link to the canonical System owner. Other 43 cases in that selected file were deliberately deselected. |
| `playwright.system-browser-privacy.config.ts` | 4 passed | Real running Synthetic UI: normal save/effective pinning, malformed successful reply and deliberate readback, stale409 and new exact-origin acknowledgement at320, actual tenant-only Owner denial/no protected requests, unknown System route, read503 recovery, keyboard and Stay/Discard/no-PUT at1440/390. |
| `pnpm --dir web run build` | Passed | TypeScript and production bundle; incumbent chunk-size/SignalR annotation advisories remain. |

Commands:

```sh
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore -m:1 --filter 'FullyQualifiedName~SystemBrowserPrivacyTests|FullyQualifiedName~BrowserPrivacyTests' --nologo
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore -m:1 --filter 'FullyQualifiedName~Same_account_has_distinct_tenant_authority_and_no_platform_or_agent_shortcut|FullyQualifiedName~Password_rotation_revokes_all_sessions_and_platform_suspension' --nologo
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore -m:1 --filter 'FullyQualifiedName~System_Browser_privacy_HTTP' --nologo
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore -m:1 --filter 'FullyQualifiedName~IdentitySqliteRestartTests' --nologo
pnpm --dir web run test --run src/features/admin/BrowserPrivacySection.test.tsx src/app/appRoute.test.ts src/features/access/accessRoute.test.ts
pnpm --dir web run test --run src/features/admin/AdminApp.test.tsx -t 'renders read-only effective configuration'
cd web
pnpm exec playwright test --config playwright.system-browser-privacy.config.ts
```

The API batch also selected the existing opt-in native profile-reset case, which skipped; this report counts no reset requalification from that batch. Prior profile-reset evidence remains in its own reports.

## Runtime and presentation evidence

Playwright MCP operated the actual isolated Synthetic app at API6888/UI6889. A normal Disabled save advanced saved revision while effective Protected revision0 remained pinned. After independent changes, one acknowledged stale Unmasked submit returned409, disabled Save and kept its exact-origin draft. Explicit Reload read the new saved revision, retained the draft and issued no replay. Unmasked confirmation displayed all-organization scope and exact `https://example.test`; focus entered the dialog, Cancel returned focus to Save and performed no mutation. Document overflow was absent at1440,390 and320.

The seven inspected captures under `.impeccable/review/system-browser-privacy/` pair top/bottom views of the independently scrolling main area with390/320 exposure confirmation. A single detector pass returned no findings. A fresh finish reviewer found no visual repair and one stale design-context owner sentence; its correction preserved all presentation guidance. The reviewer scored that fix resolved and returned **ship** at the fix-list scope. The fresh documenter compared context, sidecar, source/shared owners and rendered controls, confirmed incumbent palette/type/rhythm and required no token-bearing sidecar change. Pre-existing sidecar fingerprint drift also exists at HEAD and was reported without unrelated re-extraction.

## Corrections and practical limits

Initial compilation exposed an Application/Infrastructure visibility boundary; a public rules wrapper now uses the central internal owned-account evaluation. The initial SQLite HTTP fixture changed IAM storage but left privacy reads in memory; it now shares the real SQLite backing and deployment authority. The standalone identity restart fixture had accumulated missing resource/worker dependencies; it now uses production Synthetic registration with every writable root isolated. Frontend fixtures were updated for deliberate readback names, retained stale drafts and asynchronous Ant Design field state. Browser locators account for the required-label marker and transient loading icon. The canonical journeys then passed together. Retired owner-only privacy E2E/config and its old Admin draft-discard cases are replaced by canonical coverage rather than retained as failing executable aliases.

The UI host used disposable InMemory storage; durable transaction/reopen/startup behavior was exercised by SQLite backend/native tests. No live model/provider, enterprise target, full CI suite or hosted CI was requested or run. These results do not qualify all autonomous Browser privacy usage, general System administration, historical intermittent execution failures or final clean-baseline schema cutover. Existing user data/profile roots were untouched; all runtime roots were created specifically for this checkpoint.
