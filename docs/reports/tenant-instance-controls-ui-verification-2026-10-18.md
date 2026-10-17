# Tenant Instance execution controls verification — 2026-10-18

This checkpoint extends the tenant Instance editor with Execution budgets, Self-management and Unattended model. It advances the supported Phase 7 scope; it does not accept a milestone or the full multi-tenant proposal. Version adoption/preview, Skills/resources, Sandbox cutover, other resource/effect families, voice/rich delivery, autonomous origins and obsolete-authority/schema consolidation remain open. The earlier SQLite direct-preference reliability follow-up is unchanged.

## Implementation and ownership

The three canonical destinations live under `/t/{tenantId}/admin/agents/{instanceId}`. `TenantAgentControls` reads `/configuration-controls`, a coherent safe Instance/budgets/management composition through the existing `TenantAgentReadService`. Child Instance/security witnesses must match the parent capture; the common facade repeats original-human, exact read and current identity/security checks after I/O. Exact `agents.update` supplies an advisory hint independently of IAM inspection. Existing budget, management/freeze and unattended-model service/store commands retain their mutation authority, original-session atomic commit fence and exact revision requirements. No schema, provider or alternate authorization engine was added.

The UI shares the existing budget, self-management and model fields. `TenantAgentHeading` now owns identity/lifecycle/read-only presentation for these pages and Profile/Settings. No CSS, palette, font, theme token or UI kit changed. The existing bounded form owns section gaps, each panel owns its inset, and native field/action layouts own wrapping.

Reset stages inheritance/defaults until explicit Save. Reload retains a rejected draft and refreshes witnesses; Discard adopts the latest saved values. A confirmed acknowledgement retires the submitted draft even if its body or subsequent read fails; recovery reads never replay a command. Freeze/unfreeze acts on saved policy and preserves unrelated edits. Model catalog errors block Save and offer read recovery; unknown choices remain repairable. Definitive read revocation removes protected controls.

## Executed local checks

| Check | Result |
| --- | --- |
| Infrastructure: `Tenant_instance_editor_keeps` and `Human_agent_reads_reject_captured_configuration` filters | 50 passed, including InMemory/SQLite controls coherence, read-only hint and concurrent persona/security/logout fences |
| HTTP: `TenantAgentAccessTests.Agent_read_does_not_release` | 22 passed, including controls logout and logout/relogin during protected I/O |
| HTTP: `Creation_scoped_inventory_and_archive_restore_use_current_tenant_authority` | 1 passed, including controls exact scoped read and revoked access |
| Vitest: Controls, Configuration, accessRoute, ExecutionBudgetsSection, SelfManagementSection, ExecutionModelFields | 33 passed across six files |
| `pnpm run build` | Passed TypeScript and Vite; existing large-chunk advisory |
| `CI=1 TENANT_CHAT_API_PORT=61299 TENANT_CHAT_WEB_PORT=61300 pnpm run test:e2e:tenant-instance-controls` | 2 passed: 320px owner configuration/draft journey and exact scoped reader/revocation |

Backend commands used `dotnet test` on the respective project with `--no-restore --nologo` and the filters above; the final scoped HTTP check also used `--no-build`. Vitest used `NODE_OPTIONS=--no-experimental-webstorage pnpm exec vitest run` with the six files listed. All checks used Synthetic or deterministic temporary stores with no provider credentials/calls. Local runner `CI=1` prevents server reuse; no hosted CI or full CI suite was requested or run.

Frontend regressions exercise staged budget inheritance with exact witnesses, stale-save draft retention and permission-loss clearing, saved policy freeze/unfreeze with unsaved Off retained, malformed successful acknowledgement/read recovery without replay, catalog failure/retry and incompatible-effort clearing, exact read-only inspection, default-model reasoning and existing shared field consumers. Initial test-harness corrections included recovery-button naming, asynchronous catalog readiness, unsupported Testing Library options, exact popup ownership/active-option navigation and awaiting the canonical read after a save. The final two browser scenarios passed against a fresh isolated host. Product behavior was exercised before expanding regressions.

## Playwright MCP observations

A task-owned fresh InMemory Synthetic API on 61297 and Vite on 61298 used separate temporary roots and a separate review organization/agent. The actual journey:

1. Set Standard to Override/Extended, saved, then read the canonical API: only Standard had the 48-step override. Reset staged inheritance while the saved override/revision remained unchanged; explicit Save applied it.
2. Saved Assisted management with Settings as its sole area. Staged Off, confirmed Freeze with exact agent/organization text, and read the backend: saved Assisted/Settings remained, frozen was true, and the UI still held Off. Unfreeze removed the saved lock without applying Off; Discard restored Assisted.
3. Saved Scripted Beta and observed its canonical key with null effort. Reset and saved execution defaults; the default Alpha reasoning selector remained available. At 320px, mouse selection of Off and Discard worked without overflow.

Latest inspected navigation had zero console errors. Network showed successful scoped reads plus expected development StrictMode/unmount aborts, with no unexpected failed application response in that inspected navigation. This is not a claim about every historical request in the browser session.

## Bounded visual review and design synchronization

One visual batch produced 18 local PNGs in `.playwright-mcp/tenant-controls-20261018/`. Parent and fresh Impeccable finish reviewer opened every image: each new page at 1440×900, 768×1024 and 320×740; shared Profile/Settings headings at desktop/320; lower controls at 320, lower management at desktop and the 320px confirmation. The long agent name wrapped, actions remained reachable, and document width stayed within the viewport. Measured parent section gap was 16px; panels measured 768px at desktop and 296px at 320. Internal main scrolling is intentional; first-viewport captures are supplemented by lower-form captures.

The independent review found no material presentation defect and reached the incumbent extension's craft ceiling. Its only correction was persistence: synchronize DESIGN/PRODUCT and companion metadata for the supported controls. No new world, seed, concept roll or comp applies. Active edit-hook feedback reported no deterministic issues for Controls; no redundant detector pass was run.

A fresh documenter synchronized DESIGN/PRODUCT and companion metadata without changing normative tokens or historical evidence. The same finish reviewer verified that persistence-only correction and returned `disposition: ship`. Loading, archived, denied, stale, catalog-error and failed-refresh states were not all captured visually; the functional boundaries above have separate test evidence. Headless CLI pointer hit-testing encountered popup motion during harness development; keyboard regression coverage and separately observed MCP mouse behavior are distinct evidence. No physical device, audible voice or live provider behavior is claimed.

Final local logs: `/private/tmp/tenant-controls-infra.log`, `tenant-controls-api.log`, `tenant-controls-api-access.log`, `tenant-controls-vitest-complete.log`, `tenant-controls-build-complete.log` and `tenant-controls-e2e-settled.log` in `/private/tmp`. The passing E2E run used a disposable `agent-core-tenant-chat-e2e-dmMpNm` root. Total focused cases: 108, plus the frontend build and separately observed MCP journey.

Cleanup: both task-owned MCP hosts exited and the isolated CLI runner retired its own servers. A stale antiforgery cookie from a previous disposable host produced a startup validation log; normal CSRF issuance recovered it before the successful sign-in/write journey. No user-installed store or unrelated process was reset.
