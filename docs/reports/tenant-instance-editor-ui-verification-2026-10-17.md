# Tenant Instance Profile and Settings verification — 2026-10-17

This checkpoint adds canonical tenant Profile and Settings to the existing Instance UI. It advances the multi-tenant proposal's frontend cutover; it does not complete the whole Instance editor, full proposal or a milestone. The supported Connections and Access flows remain independent. The unfinished credential-check draft was previously preserved outside the working tree and is not part of this delivery.

## Delivered behavior

- Exact `/t/{tenantId}/admin/agents/{instanceId}/profile` and `/settings` deep links, scoped Instance navigation and inventory-to-Profile entry.
- A coherent original-human/session editor read through the existing configuration and IAM owners. Profile, settings and update affordances share matching security/Instance witnesses; concurrent configuration/security changes and retired human sessions fail closed.
- Shared strict four-field persona Form/JSON controls. Invalid non-object JSON is actionable and submits nothing. Explicit saves carry security, Instance and persona revisions. Stale saves retain drafts; a current read refreshes witnesses without retrying a mutation.
- Shared typed Settings controls with a canonical injected transport. Sparse section drafts remain independent; dependent values refresh after a save, and section reset clears only that section's overrides. Defaults, source hints and backend constraints remain visible. Confirmed saves with unreadable responses retire only submitted drafts, require read recovery and do not replay writes.
- Scoped model catalog reads with visible retry and retired-request cancellation. Read-only/archived configuration remains inspectable. Authority-loss reads remove protected controls. State & Isolation uses existing finite constraints and plain labels. Sandbox configuration explicitly stays unavailable until its tenant catalog/effects exist; no legacy platform fallback is used.

Configuration supplies guidance and eligibility. Agent IAM, approvals and execution admission remain separate. No storage migration, new UI kit, token system, provider connection or execution authority was added.

## Focused automated results

All commands ran locally without provider credentials or outbound provider calls. No hosted CI or full CI suite was requested or run.

| Check | Exact scope | Result |
| --- | --- | --- |
| Infrastructure | `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --filter 'FullyQualifiedName~Tenant_instance_editor\|FullyQualifiedName~Tenant_instance_configuration' --nologo` | 10 passed, including both stores, coherent read/write/reopen and deterministic concurrent change/logout during Definition I/O |
| HTTP | `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --filter 'FullyQualifiedName~TenantAgentAccessTests' --nologo` | 3 passed; exact scoped/revoked editor reads, stale write rejection, fresh save and reset |
| Frontend | From `web`: `NODE_OPTIONS=--no-experimental-webstorage pnpm exec vitest run src/features/access/TenantAgentConfiguration.test.tsx src/features/access/accessRoute.test.ts src/features/admin/InstanceSettingsSection.test.tsx src/features/admin/instanceManagedControls.test.ts` | 32 passed; canonical payloads, stale draft/read-only recovery, parser/route bounds, shared section recovery and model-read retry |
| Browser | From `web`: `TENANT_CHAT_API_PORT=61295 TENANT_CHAT_WEB_PORT=61296 CI=1 pnpm exec playwright test --config playwright.tenant-instance-editor.config.ts` | 2 passed; isolated disposable Synthetic hosts, both journeys described below |
| Production build | From `web`: `pnpm run build` | Passed; existing large-chunk advisory remains |
| Diff | `git diff --check` | Passed |
| Documentation links | Newly added relative links and heading fragments in changed canonical documents | 6 checked, no missing file or anchor |

Total: **47 focused passing cases** plus production build and direct MCP interactions. An initial unit assertion used an unsupported asymmetric `toHaveValue` matcher; the correct actual value was present, and the assertion was corrected. Two browser attempts exposed locator mistakes: opening mobile navigation was required, and an override icon contributes to the section's accessible name. The final journeys use the real drawer and appropriate accessible names; both pass without increasing timeouts or weakening behavior assertions.

Final logs are `/private/tmp/tenant-instance-editor-infrastructure-final.log`, `/private/tmp/tenant-instance-editor-http-final.log`, `/private/tmp/tenant-instance-editor-web.log`, `/private/tmp/tenant-instance-editor-e2e.log` and `/private/tmp/tenant-instance-editor-build-final.log`.

## Executed user journeys

1. **Canonical Profile:** Sign in to the disposable Synthetic organization, edit the actual persona name in Form, switch to JSON and observe the same draft. Save JSON `null`: an actionable object-shape error appears and no write occurs. Repair JSON and save: the actual saved name and revisions advance, and Save becomes disabled. The repeatable 320px journey also attempts navigation with a draft, chooses Keep editing, causes a concurrent settings change, observes stale-save rejection, reloads while retaining JSON, then explicitly saves with fresh witnesses. The API read confirms the intended saved name.
2. **Independent Settings:** Open Conversation and Behavior, change language and independently toggle interruption acknowledgement. Save Conversation: the actual language changes while the unrelated Behavior draft remains unsaved and enabled. Confirm Reset Conversation: its language override disappears and source returns to Definition; the unrelated Behavior draft remains, and the backend still holds its original acknowledgement value. Reset identifies agent and organization and explains its section-only impact. Discard the remaining draft explicitly.
3. **Scoped reader/revocation:** Give a human membership plus exact `agents.read`, with no agent update or IAM-inspection grant. Settings and Profile load read-only; no save action becomes available. Revoke the exact assignment and reload Profile: protected fields disappear and access failure is shown. No legacy owner request is needed for the tenant journeys.
4. **Shared legacy consumers through MCP:** Create a separate disposable legacy Instance, edit its persona through the extracted Form/JSON controls and save through its existing configuration action bar. Observe the saved name and successful configuration status. Edit its Conversation language and save through the same existing action frame; observe the retained saved field and successful next-Run status. This checks that transport injection/extraction preserves the preexisting consumer.

Playwright MCP ran against API `61293` / Vite `61294` with a fresh InMemory Synthetic root `/private/tmp/agent-core-instance-editor-20261017-z250akur`. The CLI runner used separate free ports `61295` / `61296`, an independent ephemeral root, no server reuse and two successful journeys. No installed database, user session catalog or unrelated process was reset.

The current legacy consumer console read returned no errors. One React/AntD development `flushSync was called from inside a lifecycle method` diagnostic was observed during the canonical mobile draft-navigation confirmation; its Keep editing/discard behavior and subsequent navigation worked. The shared guard was unchanged. This checkpoint does not claim that development diagnostic is resolved. Historical console entries from previously stopped hosts are not current editor failures; the final E2E journeys report no page errors and no tenant-to-legacy requests.

## Rendered design evidence

Ordinary code-led Operate extension: direct Ant Design v6, incumbent dark shell, native forms/tabs/disclosures and existing bounded form/section spacing. No CSS, visual token or asset changes. The direction contract is `.impeccable/surfaces/web-src-features-access-tenantagentconfiguration-tsx.md`; `/docs/19` owns behavior.

The first complete rendered batch contains 12 actual viewport PNGs in `.playwright-mcp/tenant-instance-editor-round1`: Profile and expanded Conversation Settings at 1440×900, 1200×647, 1024×768, 768×1024, 390×844 and 320×740. Supplemental final evidence in `.playwright-mcp/tenant-instance-editor-final` contains nine PNGs: canonical JSON at 1200/320, narrow Settings actions, narrow State & Isolation, exact Reset confirmation, and shared legacy Profile/Settings at 1440/320. Supplemental views intentionally scroll to their controls; the base matrix records initial viewports. Every image was opened. No implementation changes followed these captures. Long names wrap; controls stack; the document remains within the viewport; narrow confirmations retain readable content and reachable actions.

Initial attempted screenshot writes to `/private/tmp` were refused by the browser tool's allowed-root policy and produced no evidence; captures were saved in the allowed repository `.playwright-mcp` directory. Those setup failures were not treated as product or visual failures. The active design edit hook reported no deterministic issues in the explicitly observed test-file scans; no redundant manual detector was run. Pixel/source review remains the craft evidence.

The fresh Impeccable finish reviewer returned **ship**, with no material fixes. It independently opened all 21 images and inspected sources; it treated runtime results as parent-reported and scoped hook evidence to the explicitly observed scans. Its private report is `/private/tmp/tenant-instance-editor-finish-review.md`.

The fresh documenter completed the explicitly requested in-place design sync in `.agents/context/DESIGN.md`, `.agents/context/PRODUCT.md` and the narrow Overview/provenance mirror in `.impeccable/design.json`. It corrected outdated cutover-pending claims using the dated evidence owners and recorded the shared Profile/Settings patterns. Normative tokens, component previews and visual metadata stayed unchanged. Seven bounded current source hashes identify this merge; earlier inspection dates/base/source hashes remain historical, and unrelated sidecar narrative drift is not presented as newly refreshed. Documentation link/anchor, canonical-section order, exact Overview mirror, fingerprints and diff checks passed. Its private handoff is `/private/tmp/tenant-instance-editor-documentation.md`. No design-system or runtime acceptance delta is claimed.

The task-owned native API/Vite and CLI hosts were stopped after verification; ports 61293–61296 were confirmed free. Disposable evidence remains available without altering installed data.

## Remaining scope

Tenant Instance version adoption, budgets/management, Skills/resources and other resource Access panels remain open. Voice/rich resources and receipts, live input arbitration, remaining resource/effect families, autonomous origins, Governance/platform administration and final obsolete authority/schema consolidation remain proposal obligations. The earlier SQLite direct-preference/provider-unknown reliability follow-up remains separate. No Real-provider, voice/hardware, full-suite or full-proposal acceptance is claimed.
