# Administrative client transport cutover — 2026-10-21

Canonical administrative controls now require their tenant-owned transports and metadata. The five former SDK modules (`adminApi`, `instanceConfiguration`, `sandboxes`, `drive`, `stateIsolation`) retain shared types and pure helpers; 112 obsolete HTTP function exports are removed. There is no replacement owner request facade or compatibility server.

`InstanceSettingsSection` requires an explicit settings transport and receives Sandbox inventory/summary from the tenant owner. Event filter testing requires its evaluator callback. Email monitoring requires the exact tenant and complete watch/access witnesses, with global watcher counts and Instance-management links removed. Definition forms receive authoring metadata; budget controls consume explicit scoped limits/error/reload without a global fetch.

`ContainerRegistryFields`, `DriveAccountFields`, `CustomEmailAccountFields` and `DefinitionCandidateForm` preserve the canonical field compositions from their former combined modules. The owner-authorized dialogs, Gmail panel, old Sandbox/import compositions and unused coordinated isolation provider are removed. The old unscoped Session Sandbox drawer is also removed from Chat composition; the canonical tenant drawer remains.

## Discovered deletion bug

The first native Credential journey successfully exercised General/Drive/Gmail/Custom/Registry creation, separate Synthetic Google cancellation/consent, metadata changes and protected replacement. Its final DELETE returned 204, but the inspector stayed open: its busy navigation guard rejected the confirmed-completion callback's navigation.

The successful deletion callback now calls the existing `guard.release()` before collection navigation. Rejected/uncertain operations do not release or replay deletion. The entire journey then passes at desktop and again at 390×900, including an empty removed-item result and closed inspector. The original failed trace is retained under `/private/tmp/transport-cutover/credentials-results/` and its DELETE 204 was inspected; the failure was not hidden by weakening the expected outcome.

## Preservation and focused checks

`web/src/test/fixtures/retired-admin-transports.json` records the baseline (`53ab8b58`), removed function names, removed/replaced source paths and 61 retired owner-contract test declarations (parameterized declarations counted once). Independent registry-host normalization and Definition budget/form/Skills/error cases remain. Shared settings tests inject a transport; monitoring race/recovery tests exercise the tenant request path. Old owner-only dialog/SDK tests are retired rather than skipped.

Initial ported tests exposed seven fixture expectations tied to the removed defaults: four-argument owner adapters versus injected cancellation signals, retained local form tabs versus a remounted pure fixture, global Event links and a single-alert assumption. Fixtures now model the explicit transport/metadata owner and assert canonical behavior; all affected cases pass. No production owner adapter was restored.

Commands below run from `web/` with `NODE_OPTIONS=--no-experimental-webstorage` for Vitest. Paths are disposable local evidence, not repository dependencies.

| Check | Result | Evidence |
| --- | --- | --- |
| `pnpm exec vitest run` on InstanceSettingsSection, AutomationEventFilter, EmailMonitoringSection, ExecutionBudgetsSection, definitionCandidateEditor and services/sandboxes | 47 passed, six files, 36.77s | `/private/tmp/transport-cutover/unit-corrected.log` |
| Focused TenantAgentAutomations, TenantAgentConnections, TenantSessionSandboxDrawer | 26 passed, three files, 99.91s | `/private/tmp/transport-cutover/consumers.log` |
| Final EmailMonitoringSection, definitionCandidateEditor, TenantAgentConfiguration, TenantAgentSandbox, TenantAgentControls | 39 passed, five files, 26.40s; 22 overlap the first group | `/private/tmp/transport-cutover/final-consumers.log` |
| `pnpm exec tsc --noEmit` | Passed | `/private/tmp/transport-cutover/typecheck.log` |
| `pnpm run build` | Passed; existing bundle-size warning, Vite 8.80s | `/private/tmp/transport-cutover/build-final.log` |
| Corrected desktop native Credential purposes/consent/delete plus protected-failure recovery | 2 passed, 32.1s | `/private/tmp/transport-cutover/credentials-corrected.log` |
| Native Definition exact-read/revoked-write plus owned authoring/resources/evaluation/publication/lifecycle | 2 passed, 22.1s | `/private/tmp/transport-cutover/definitions.log` |
| Native 320px Profile form/JSON, guarded departure, stale save and independent settings save/reset | 1 passed, 12.9s | `/private/tmp/transport-cutover/settings.log` |
| Final 390px Credential purposes/consent/protected replacement/confirmed delete | 1 passed, 27.0s; zero obsolete owner endpoints/headers and page/console errors | `/private/tmp/transport-cutover/credentials-narrow.log` |

These groups cover 90 distinct focused frontend cases across 12 files, plus six final native browser journeys. Unit fixtures emit existing React act/AntD measurement warnings; they are not claimed warning-free. CLI configurations in `/private/tmp/transport-cutover/` import the repository's Synthetic configurations, use the existing API binary with no C# build, and isolate ports/data. No user hosts/data were reused or reset.

MCP navigation was attempted against the disposable Synthetic Vite server and returned `Transport closed`. Playwright CLI is the practical fallback; no successful MCP journey is claimed. No physical microphone, acoustics or hosted provider work was exercised. No C# sources, styles, fonts, assets, theme or token policy changed, and no new Impeccable review/detector result is claimed. `.impeccable/design.json` SHA-256 stays `8bdc3659a25b706454ac8cc0394323c5e0e60ea1ade616756b180efbaecef49b`.

## Remaining proposal work

This is an administrative client module checkpoint, not proposal or milestone acceptance. Core Chat/realtime/file-preview owner-token issuance/storage/retry and unscoped/default transports remain; internal SessionHost/old approval/workers, schema/DTO cleanup, missing canonical feature parity (including coordinated state/runtime/Harness/Experience/maintenance where absent), remaining platform guardrails and exhaustive proposal qualification remain open. Earlier historical reports retain their original evidence. CI/full local CI were not requested or run. The original goal remains active.
