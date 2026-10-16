# Tenant Credential UI verification — 2026-10-17

Baseline: `8eead65408a152b444bff8f1083019ed888d4af9`. This checkpoint adds canonical tenant Credential management and the exact OAuth callback destination over the previously verified backend. The full proposal remains active. Existing Real hosts, user data and protected stores were preserved; no reset, deployment, Real restart or CI was requested or performed.

## Delivered behavior and corrections

The organization Credentials destination uses `/t/{tenantId}/integrations/credentials`, with exact selection through `credentialId`. Owned inventory is server-filtered before paging; local search explicitly covers the loaded page. An exact-resource read/access snapshot permits item-only readers to inspect their granted Credential, with read-only feedback and no management controls. Tenant-wide read/manage permits creation. Every mutation obtains a fresh security witness and retains the saved logical revision; the server remains authoritative.

Five purposes reuse existing typed fields: General, Gmail, Google Drive, Custom email and container registry. Protected replacement is separate from safe metadata editing. Google accounts save before explicit consent; cancel/connect return to that saved Credential, and disconnect distinguishes local success from remote revocation. Setup describes offline Synthetic consent, missing Google configuration and host-disabled Drive. Trusted destination validation prevents arbitrary authorization navigation. Creation/connection does not bind an agent, grant tool access or approve an effect.

Review and runtime interaction corrected purpose-switch name retention while discarding transient secrets, duplicate field defaults, stable action names during Ant Design spinner transitions, collection wrapping, focus restoration and independently visible Credential lifecycle/connection status. Shared connection-tag defaults remain compatible with existing consumers. Successful mutation responses whose body cannot be read now become acknowledged-completion failures: Reload page is offered and duplicate submission is blocked. Ordinary failed writes retain safe drafts and clear protected inputs.

## Executed checks

| Check | Result and observed behavior |
| --- | --- |
| `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --nologo --filter FullyQualifiedName~TenantCredentialAccessTests -v minimal` | 10 passed. Canonical cookie/CSRF management and the access snapshot retain safe views, exact grants, foreign denial and immediate revocation; manage does not imply use. |
| Focused Vitest, eight files | 36 passed: `CredentialConnectionTag`, `CredentialsSection`, `EmailAccountDialog`, `AddCredentialDialog`, `accessRoute`, `useTenantCommand`, `identity`, `tenantCredentials`. Shared fields/tag compatibility, exact routing/destination validation and acknowledged-response recovery remain covered. Existing registry date-picker tests emit a jsdom NaN-height warning; normal browser flows are console-clean. |
| `pnpm --dir web build` | Passed TypeScript and Vite build. Existing SignalR annotation and large-chunk warnings remain. |
| `TENANT_UI_EXTERNAL=1 TENANT_UI_URL=http://127.0.0.1:5398 pnpm --dir web exec playwright test --config playwright.tenant-credentials.config.ts` | 3 passed against the task-owned Synthetic host. All five purpose forms, protected create/replace/delete, Gmail and Drive cancel/connect/disconnect, separate Disabled/Active lifecycle with retained Synthetic connection and disabled reconnect, exact grants/read-only inspection, revoked-write and stale-draft recovery, foreign denial, secret clearing and no duplicate creation after an acknowledged unreadable response. Normal flows observed no legacy Admin API requests, console errors or unexpected HTTP/network failures. |
| Playwright MCP interaction | Exercised actual creation/replacement, purpose changes, Google consent/cancellation/disconnection, metadata editing, dirty Escape/Keep editing/Discard and focus return. Safe long names and Custom defaults were inspected in the running UI. |
| Responsive rendered checks | Inspector at 1440/1024/768/390/320px, collection and Custom editor at 1440/320px. No document overflow; 640px inspector capped to viewport; long labels available in the inspector; native form scrolling and reachable footer actions. Section gap remains 16px. |
| Documentation and patch checks | All 11 changed Markdown files passed local link/anchor, balanced-fence and complete JSON checks: 681 local references and 15 JSON examples. `git diff --check` passed. |

The host used InMemory Synthetic mode, explicit fixture bootstrap, ports 5298/5398 and fresh private temporary roots. Profile/readiness was checked through Playwright request access because sandboxed shell loopback was unavailable. No provider credentials, external Google calls or existing catalogs were needed. The browser fixture is repeatable through `web/playwright.tenant-credentials.config.ts`; its default mode starts disposable servers, while the environment override above reuses only this known task-owned host.

## Bounded visual review and design documentation

All captures under `.impeccable/review/` were opened and validated after entrance motion settled: `credentials-detail-{1440,1024,768,390,320}.png`, `credentials-custom-editor-{1440,320}.png` and `credentials-collection-{1440,320}.png`. These local evidence files are ignored by Git. The automatic design hook reported no deterministic issue on changed targets.

The fresh finish reviewer initially required distinct lifecycle and connection state. After the correction and recapture, its verdict was **ship**, scoring that one material finding resolved with no fix-batch regression. This is a verdict on the scored correction, not broader runtime, live-provider or multi-tenant approval acceptance. The UI remains an ordinary extension of the existing Operate surface: direct Ant Design v6, shared AdminShell/toolbar/fields/confirmations, existing 8/12/16 spacing and responsive detail rules, with no new palette, font, token or CSS subsystem.

The independent shipped documenter checked all nine captures, the changed/shared components, theme/spacing/detail/modal owners and canonical UI specification. It indexed the Credential targets in the existing surface brief and found no remaining design-documentation gap or new drift in scope. `DESIGN.md`, `.impeccable/design.json` and application styles remain unchanged. There are no new shipping raster assets; the captures are review evidence only.

## Remaining boundaries

Resource assignment administration and Instance binding UI are pending. Tenant runtime credential refresh/use, secret sinks and execution-time effect permission checks remain pending, as do other resource/store families, Sandbox/decommission, Session/Run/Hub/tool/background tenancy, trusted lineage, durable approvals, obsolete global authority removal and final schema/reset consolidation. Custom transport, registry authentication and live Google consent were not exercised by these browser checks. Existing field/component tests do not establish those external behaviors. This checkpoint does not complete the full proposal or claim application milestone acceptance.
