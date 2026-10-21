# Tenant coordinated State & Isolation configuration — 2026-10-21

This checkpoint restores a canonical coordinated metadata editor and atomic save. It does not close the full multitenant proposal or claim Runtime observation, Experience generation or identity-maintenance engine parity. The original goal remains active.

## Implemented boundary

The scoped Instance State & Isolation page prepares Persistent, Session isolated, Stateless and Definition defaults drafts. Shared recipes and finite validation now live in `IsolationConfigurationPlanner`; the old internal service delegates to those rules and supplies no canonical HTTP authority.

Original-human `TenantAgentReadService` fences owned reads/previews before and after I/O. The existing tenant configuration snapshot reads actual Experience/maintenance values and revisions alongside the Instance. Apply validates current `agents.read`/`agents.update`, exact tenant security revision, all three owner revisions, fingerprint, lifecycle and effective Definition constraints. The specialized tenant configuration change commits all owners, tenant revision and one body-free audit in the same store transaction. Original login is checked at commit. Metadata races after the final application preview still refuse the complete save.

SQLite reuses the existing Instance, ExperienceSettings and IdentityMaintenanceSettings rows with one commit and no migration. InMemory uses the existing owner locks and synchronous validated assignments. Definition defaults clears State/Memory and Voice enablement overrides while preserving independent learning opt-ins and unrelated Voice fields. Browser/Sandbox/Runtime observations are not collected by these metadata reads or previews; enforcement state is explicitly `NotObserved`.

The canonical browser has one editor for State, Memory and Voice enablement. Generic Settings keeps independent fields such as Voice ID/rate and links to that editor. Existing Ant Design panels, State fields, source/default reset hints and responsive `IsolationChangePreview` present human labels and On/Off values. Presets are drafts, never implicit saves. Stale/uncertain outcomes retain draft choices, invalidate preview and require read recovery plus a new deliberate preview. Confirmed malformed mutation replies use the shared saved-but-view-incomplete recovery. Loss of read/write authority retires private fields.

## Executed verification

All checks use local Synthetic/disposable stores with no provider calls or CI.

| Check | Final result | Observed behavior |
| --- | --- | --- |
| .NET solution build | Pass, 0 warnings/errors | Domain/Application/Infrastructure/API and test projects compile. Final API/test build after ownership/mapping corrections also passes, 0 warnings/errors. |
| Focused API integration filter | **81 passed** | 13 coordinated cases; original-login read/write fences including new State endpoint and apply; scoped reader/foreign ownership, generic Settings projection and existing persona/settings witnesses. |
| Application `StateIsolationPolicyTests` | **17 passed** | Sparse false/reset inheritance, Definition ceilings, admitted/current restriction intersection and continuity-tool restrictions. |
| Focused frontend | **26 passed** | New editor save/stale/uncertain/denied/malformed-reply/read-only cases plus existing Settings, route and Definition policy consumers. |
| Frontend build | Pass | TypeScript and Vite build; existing large-chunk advisory remains. |
| Native browser CLI | **1 complete journey passed** | Stateless preset/save; stale refusal after independent persona write; retained Experience draft on reload and fresh explicit save; maintenance commit with aborted reply, read recovery and no replay; desktop/narrow presentation and the generic Settings consumer. |
| Playwright MCP | Unavailable | Navigation attempted against the running isolated app; tool returned `Transport closed`. CLI supplied the runtime fallback. |

API command:

```sh
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Tenant_isolation|FullyQualifiedName~Agent_read_does_not_release_captured_data|FullyQualifiedName~Every_agent_HTTP_mutation|FullyQualifiedName~Creation_scoped_inventory|FullyQualifiedName~Editor_witnesses'
```

The two store paths save both opt-ins, apply Stateless, refuse stale replay, reset inherited fields and preserve other Voice fields. SQLite recreates the host and reads identical saved configuration. Six stale-owner cases change Instance, Experience or maintenance before apply and verify no partial write. Four additional cases change Experience/maintenance after the final preview, immediately before the atomic store call; both stores refuse without changing Instance/settings/security/audit. Foreign tenant ownership, forged fingerprint, unsupported/null section fields and missing CSRF are refused. Scoped readers can inspect and prepare previews but cannot apply. Original-session logout/re-login cannot lend authority to an in-flight read or mutation.

Frontend command:

```sh
NODE_OPTIONS=--no-experimental-webstorage pnpm --dir web exec vitest run src/features/access/TenantAgentIsolation.test.tsx src/features/access/TenantAgentConfiguration.test.tsx src/features/access/accessRoute.test.ts src/features/admin/DefinitionStateIsolationPolicy.test.tsx
pnpm --dir web run build
```

The native journey runs `web/e2e/tenant-isolation.spec.ts` (permanent entry: `web/playwright.tenant-isolation.config.ts`) using the tenant Chat Synthetic configuration with isolated ports **5096/5186**, a disposable root and a fresh already-built API. User hosts on 5080/5173 are untouched. It verifies exact API readback and four attempted saves: initial success, stale refusal, corrected success and committed/lost-reply success. Reload adds no apply request. The same journey opens generic Settings, verifies the coordinated link and absent independent State/Memory controls, expands Voice and finds Voice ID with no duplicate Enabled switch. Console and failed-network inspection separates deliberate aborted save replies and lifecycle-cancelled GET reads from unexpected failures. No page exceptions, obsolete owner requests or document overflow at 320px were observed. The reviewed capture run passed in 10.9 seconds; a separate console/network confirmation passed the same complete journey in 9.5 seconds, preserving the reviewed images. Browser observations do not establish physical audio or live-provider behavior.

## Findings corrected during verification

The first browser execution exposed raw enum serialization (`0`) on the new safe Instance projection, which incorrectly disabled an active editor as archived. The endpoint now reuses the established safe mapper and integration coverage asserts `Active`.

The first expanded API regression run failed 70 fixture hosts before behavior because `AgentReadContent` did not preserve `ITenantResourceSourceContentStore`. The wrapper now delegates that existing facet to its real backing store. The final 81 cases pass; the earlier setup failures are not represented as passing behavior.

The finish reviewer requested the existing shared change preview in place of raw owner/field/JSON rows. That correction is resolved with a **SHIP** verdict limited to the correction and captured visible regions. Initial scoped detector output was `[]`; it was not rerun after review, following the bounded review workflow.

## Evidence and limits

Local command logs are under `/private/tmp/tenant-isolation-verification/`. Captures show visible regions of the existing internal scroller, not whole-page composites: [desktop policy](assets/tenant-isolation-2026-10-21/desktop.png), [desktop preview](assets/tenant-isolation-2026-10-21/desktop-preview.png), [320px editor](assets/tenant-isolation-2026-10-21/mobile.png), [320px preview](assets/tenant-isolation-2026-10-21/mobile-preview.png), [320px actions](assets/tenant-isolation-2026-10-21/mobile-actions.png), [390px retained-draft warning](assets/tenant-isolation-2026-10-21/stale-390.png). [Finish review](assets/tenant-isolation-2026-10-21/finish-review.md) and [design comparison](assets/tenant-isolation-2026-10-21/design-comparison.md) state their narrower evidence limits.

Existing visual tokens, DESIGN/PRODUCT and `.impeccable/design.json` remain unchanged; the sidecar SHA256 remains `8bdc3659a25b706454ac8cc0394323c5e0e60ea1ade616756b180efbaecef49b`. Pre-existing stale-sidecar/two orphaned brief findings were preserved without unrequested repair. No measured contrast, full keyboard, full five-width or broad design-system acceptance is claimed.

This checkpoint qualifies configuration metadata and recovery. Full canonical context/cleanup/browser/sandbox behavior across successive Runs, Runtime counts/agent inspection, rich scoped authoring, Experience/maintenance execution, remaining internal authority/schema consolidation and the requirement-by-requirement proposal audit remain required work. No full proposal or milestone acceptance is claimed. CI was not requested or run.
