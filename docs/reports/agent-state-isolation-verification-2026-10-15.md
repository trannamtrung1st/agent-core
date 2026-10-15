# State and isolation verification — 2026-10-15

Status: implementation in progress on `develop/branch-1`, baseline `073cd2d4be6d05ad1fcd154a7f935fe98a38338c`. This ledger records local evidence; it is not milestone acceptance. The companion Three-Layer Context cutover is still being finalized in its owning checkout. Model-facing Runtime facts, profile filtering and `agent.state.inspect` integration remain dependent on its committed single assembly path. No provisional prompt path has been added.

## Scope and preservation

One Session Runtime and existing Activation/AgentRun, approval, effect and cleanup owners remain. State is a disposable bounded observation. Definition policy and sparse Instance fields express isolation; preset names are only UI conveniences. No state bag, mode discriminator, fourth context layer, new orchestrator or active Environment entity is introduced.

All runtime checks use task-owned Synthetic stores, workspaces, ports and local fixture pages. Real application services and user data were not restarted/reset. The owner explicitly enabled disposable Chromium isolation checks. Hosted CI, hosted models and real OpenSandbox execution were not requested/run.

## Recorded focused checks

| Check | Result |
| --- | --- |
| API filter `SessionWorkingCleanupTests`, `StateIsolation`, `RuntimeStateApiTests` | 6 passed; atomic preset apply/CAS, Home gates, terminal cleanup, bounded noncooperative provider and redaction. |
| Expanded API filter above plus `ContinuityBoundaryTests`, `ContinuityEnhancementJourneyTests` | 25 passed, 0 failed/skipped (42 s). Scoped current-Session reads exclude sibling counts. |
| Infrastructure with `AGENTCORE_CHROMIUM_ISOLATION_TESTS=1`; filters `BrowserStateIsolationTests`, `AgentBrowserProfileTests`, `BrowserEnvironmentJourneyTests`, `State_counts_runs`, `Resolved_browser_isolation`, `State_observation_redacts` | 18 passed, 0 failed/skipped (25 s). Real disposable Chromium; local pages only. |
| Application filters `StateIsolationPolicyTests`, `InstanceSettingsResolverTests`, `Request_isolation`, `ToolPolicy` | 28 passed, 0 failed/skipped (5 s). |
| Expanded Application filter above plus `MemoryProposalAdmission` | 60 passed, 0 failed/skipped (2 s). |
| Infrastructure `Request_isolation`, `SandboxMount`, `SandboxStoreTests` | 26 passed, 0 failed/skipped (5 s); original second-request test snapshot was corrected before this passing run. |
| Infrastructure `BrowserPrivacyTests`, `BrowserCredentialSinkTests`, `NativeBrowserBoundaryReviewTests`, `BrowserCleanupTests`, `OpenSandboxCleanupTests` | 62 passed, 0 failed/skipped (67 s). |
| Infrastructure `Terminal_cleanup_scan` | 2 passed, InMemory/SQLite reopen, including background-only surfaces. |
| API `SessionWorkingCleanupTests`, `StateIsolationConfigurationTests`, `StateIsolationContinuityTests` | 4 passed, 0 failed/skipped (5 s): uncertain Sandbox lease defers scratch cleanup until reconciliation/recovery; invalid audit rolls back all owners; disabled continuity retains historical sources. |
| API `StateIsolationConfigurationTests`, `StateIsolationContinuityTests`, `RuntimeStateApiTests`, `SessionWorkingCleanupTests`, `StateIsolationWorkspaceTests`, `SandboxApiTests` | 23 passed, 0 failed/skipped (27 s). Exact audit-field assertions subsequently passed 2 tests after correcting an xUnit analyzer convention. |
| Infrastructure `BrowserStateIsolationTests`, `NativeBrowserBoundaryReviewTests`, `BrowserCleanupTests` after late-view cancellation hardening | 19 passed, 0 failed/skipped (44 s). |
| Vitest `InstanceStateSection`, `StateIsolationConfigurationSection`, `AdminInstanceNavigation`, `sessionError` | 15 passed, 0 failed (5.52 s). Existing CopyDetailsButton test emits an act warning. |
| `pnpm --dir web exec tsc --noEmit` | Passed. |
| `pnpm --dir web build` | Passed (9.12 s Vite phase); existing chunk-size and dependency annotation warnings. |
| Documentation file/fence/link check and `git diff --check` | Passed for all 13 changed documentation/context files. |

Commands use `dotnet test <test-project> --no-restore --nologo --filter '<named filters>' -m:1 /nodeReuse:false`, with disposable test fixtures. A missing API clock-testing dependency and local compile naming conflict were corrected before the passing runs above. Earlier failed assertions are not counted as acceptance.

## Executed UI and provider scenarios

Task-owned SQLite Synthetic API uses port 5097, Vite 5187, Browser/OpenSandbox disabled and `/tmp/agent-state-isolation-verification` data. The managed fixture Instance is approval-demo v1. No real provider credentials are used.

- State: durable Sessions and hosted runtimes are separate; disabled Browser/Sandbox show Unknown with Partial. Refresh succeeds. An injected 503 retains prior counts as Stale; Retry recovers Partial with a fresh timestamp.
- Preset: selecting Session-isolated prepares an exact unsaved diff. Save commits underlying values; reload retains the derived preset and source labels. Keyboard Home edit derives Custom; Discard restores saved policy. No apply occurs on selection.
- Fit: State and State & Isolation at 320, 390, 768 and 1440px have document/main width no greater than their available viewport. Mobile and desktop screenshots were inspected in `.playwright-mcp/state-isolation-mobile.png` and `state-isolation-desktop.png`; existing Ant Design panels, tokens, wrapping and form grid remain readable. The console after these navigations has no errors/warnings. Earlier injected 503 console errors were expected failure evidence.
- Ephemeral browser: concurrent Sessions have distinct contexts, cookie/local storage and tab references. A sibling cannot switch/close the other's handle. Closing B leaves A usable.
- Persistent browser: authentication storage intentionally shares an Instance profile, but tab/popup ownership stays Session-specific. A delayed popup from A remains owned by A while B is active. Closing B preserves A and profile bytes.
- Transition: tightening during provider initialization returns pending, then safely replaces the owned view with an ephemeral context. Historical profile directory remains.
- Workspace: live ReadOnly/Disabled overrides deny old pinned Run mutation/read routes and indirect binary writes, normalize relative cwd to working, and preserve owner-readable Home bytes.
- Cleanup: ordinary Run completion and pause retain scratch. Terminal eligibility removes A scratch while retaining B scratch, Home, Artifact, transcript and Run receipt.
- Definition: forked a task-owned draft, changed Home by keyboard, saved and reloaded ReadOnly. Published version controls remain disabled and show compatibility defaults. Draft page and published drawer fit 320/390/768/1440px after resize settlement. The asynchronous Save was allowed to finish before verifying reload; its beforeunload guard correctly warned during the first premature reload attempt.

## Acceptance coverage and remaining work

| IDs | Evidence / status |
| --- | --- |
| AS-01 | Static owner/DI review; no new execution or persistence owner. Final context integration audit pending. |
| AS-02–05 | RuntimeStateApiTests, store state/reopen tests, InstanceStateSection tests and live stale/Retry journey. |
| AS-06 | Owner HTTP denial and trusted scoped read tests pass; malicious state-tool args pending B4. |
| AS-07–09 | Pending companion A and model-facing B4 integration. |
| AS-10–12 | Policy/resolver, InMemory/SQLite atomic revision, audit-failure rollback and frontend draft/conflict tests pass. |
| AS-13 | Optional policy defaults and resolved browser pin/reopen tests pass; integrated A migration compatibility still pending. |
| AS-14 | Provider/resource separation proven; two-customer model-context and profile evidence filtering pending A. |
| AS-15 | Synthetic ordinary Session terminal/new-request rejection and durable same-admission retry/reopen pass. Integrated two-request UI journey remains. |
| AS-16 | Experience/Continuity current gates, maintenance Instance overrides and live Memory admission implemented; end-to-end projection proof pending A. |
| AS-17–18 | Home access integration and Sandbox mount/transfer boundary tests pass. Sandbox mounts only private working staging. |
| AS-19–21 | Disposable Chromium ownership/storage/transition tests pass. Broader uncertainty/cancellation regression review remains. |
| AS-22–23 | Durable Sandbox uncertainty/redaction, mount/transfer and deterministic cleanup suites pass; selection/switch integration and real server execution remain distinct checks. |
| AS-24–25 | Terminal/deferred cleanup, background recovery scan and live Home restriction tests pass. Integrated child/effect and model refresh coverage remains. |
| AS-26 | Focused continuity, browser environment/profile and tool-policy regressions pass; final cross-feature set pending integration. |
| AS-27 | New surface Vitest, Synthetic save/discard/recovery, Definition authoring/read-only, keyboard and all four widths verified. |
| AS-28 | Architecture, ports, execution, frontend, API, persistence, testing, operations and product tooling context synchronized with independent work; final integration/closure docs pending. |

Required six cross-feature journeys and all AS outcomes must be reconciled before claiming the full proposal complete. Optional unrun CI does not block local completion. Live provider/customer-account acceptance remains distinct from Synthetic and local Chromium evidence.
