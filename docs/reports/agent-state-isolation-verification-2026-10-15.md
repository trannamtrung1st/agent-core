# State and isolation verification — 2026-10-15

Status: requested implementation completed locally on `develop/branch-1`. This records focused local verification, not milestone acceptance, production deployment or a full-suite/CI run.

Baseline: `073cd2d4be6d05ad1fcd154a7f935fe98a38338c`. The finalized Three-Layer Context cutover `ff875ea48cb42a72829dac51f87a9c8bd939e8b8` is integrated through merge `ce6e5b01`. B implementation commits include `edd33c7b`, `1e1162a5` and `604634ab`; the final follow-up commit contains the remaining verification fixes and this ledger. Final production-source SHA-256: `3cf53b6d1b944d8ab9155e12b694a971fc8e22d5152111a0e1aef96d83583875`. The fingerprint covers 1,334 sorted tracked paths under `src`, `web/src` and `agents`, hashing each UTF-8 path, NUL, file bytes and NUL; tests/docs are excluded.

## Scope and preservation

One Session Runtime owns mutable conversation state through its mailbox. Existing Activation/AgentRun, approval, effect, workspace and cleanup owners remain. Runtime State is a bounded disposable observation; policy and sparse Instance overrides express isolation. Preset names are UI conveniences. There is one typed ContextAssembler path: fixed Core procedures, trusted replaceable Runtime facts and provenance-preserving Evidence. No state bag, fourth context layer, new orchestrator, alternative Session/Run type or provider allocation on State reads was added.

All checks used task-owned Synthetic stores, workspaces, ports and local fixture pages. Real application services and user data were not restarted/reset. The user explicitly enabled disposable Chromium checks. Hosted CI, the full local CI suite, hosted models, private external accounts, live OpenSandbox and hardware-audio qualification were not run for this change. Earlier Sandbox reports remain historical evidence, not new live-provider proof.

## Focused commands and results

Backend command form: `dotnet test tests/<project>/<project>.csproj --no-restore --nologo --filter '<filter>' -m:1 /nodeReuse:false`. Every filter fragment below uses `FullyQualifiedName~` and alternatives use `|`. Counts are separate overlapping invocations; they must not be summed as unique coverage.

| Project / filter | Observed result |
| --- | --- |
| Application: `StateIsolationPolicyTests`, `InstanceSettingsResolverTests`, `Request_isolation`, `ContextAssemblerTests`, `MemoryProposalAdmissionTests`, `SessionMemoryPromptTests`, `ResponsesCheckpointRecoveryTests`, `ToolRegistryPolicyTests` | 86 passed, 2 skipped, 0 failed (2 s). Only the two paid background prepare/resume opt-ins skipped. |
| Application: `ToolApprovalTests`, `TriggerScheduleRuntimeTests`, `CapabilityProjectionTests`, `CapabilityDiscoveryRuntimeTests`, `CompletionInboxCapabilityTests` | 75 passed, 0 failed/skipped (767 ms). |
| Application final settlement regression: `StateIsolationPolicyTests`, `BackgroundCompletion`, `AcceptedConversationWork`, `ContextAssemblerTests` | 33 passed, 0 failed/skipped (109 ms). |
| API: `StateIsolation`, `RuntimeStateApiTests`, `SessionWorkingCleanupTests`, `BackgroundSessionJourneyTests`, `SandboxApiTests`, `ContinuityBoundaryTests` | 51 passed, 0 failed/skipped (37 s). |
| API: `StateIsolationContextTests`, `SessionCatalogApiTests` | 22 passed, 0 failed/skipped (7 s), after effective Voice availability correction. |
| API: `StateIsolationContextTests`, `RuntimeStateApiTests` | 8 passed, 0 failed/skipped (11 s), including expired claim and visibility revoked during collection. |
| API final continuation regression: `StateIsolationContextTests`, `BackgroundSessionJourneyTests`, `SessionWorkingCleanupTests` | 16 passed, 0 failed/skipped (12 s final rerun). Four strict child cases cover both completion orders, quiet accounting and non-reporting work. |
| Infrastructure: `ContextCutover`, `Request_isolation`, `Resolved_browser_isolation`, `State_counts` | 12 passed, 0 failed/skipped (5 s). |
| Infrastructure: `Sealed_run_configuration_hashes`, `Request_isolation`, `Terminal_cleanup_scan` | 6 passed, 0 failed/skipped (3 s), including explicit ephemeral browser pins through conversion/reopen. |
| Infrastructure final continuation/reopen: `Request_isolation`, `Completion_receipt_commits`, `Quiet_completion`, `Terminal_cleanup_scan`, `Resolved_browser_isolation`, `ContextCutoverTests` | 19 passed, 0 failed/skipped (6 s). |
| Infrastructure with `AGENTCORE_CHROMIUM_ISOLATION_TESTS=1`: `BrowserStateIsolation`, `AgentBrowserProfile`, `BrowserEnvironment`, `State_observation_redacts`, `ContextCutover` | 21 passed, 0 failed/skipped (25 s). |
| Infrastructure final Chromium `BrowserStateIsolationTests` | 4 passed, 0 failed/skipped (6 s), after unknown-mode observation correction; includes ordinary and delayed `noopener` popup ownership. |
| Infrastructure: `BrowserPrivacyTests`, `BrowserCredentialSinkTests`, `NativeBrowserBoundaryReviewTests`, `BrowserCleanupTests`, `OpenSandboxCleanupTests` | 62 passed, 0 failed/skipped (67 s). |
| Infrastructure: `Request_isolation`, `SandboxMount`, `SandboxStoreTests` | 26 passed, 0 failed/skipped (5 s). |
| Infrastructure: `BrowserStateIsolationTests`, `NativeBrowserBoundaryReviewTests`, `BrowserCleanupTests`, after late-page cancellation hardening | 19 passed, 0 failed/skipped (44 s). |
| API targeted configuration/continuity/cleanup checks | Atomic invalid-audit rollback, exact qualified audit fields, busy transition preview, live continuity gates and deferred uncertain-Sandbox cleanup passed. Separate invocations: 4 tests (5 s), 2 tests (7 s), 3 tests (6 s). |
| Vitest: State, State & Isolation, Admin navigation, session error and Context provenance | 17 passed (10.51 s). |
| Vitest: `realtime.race.test.ts` | 63 passed (2.92 s), including terminal acknowledgement drain. |
| Vitest: realtime queue and protocol | 34 passed (3.63 s). |
| Vitest: `ChatApp.test.tsx` | 27 passed (26.17 s), including ended-chat New chat action. Existing React test act warnings remain. |
| `pnpm --dir web exec tsc --noEmit`; `pnpm --dir web build` | Passed. Final Vite build phase 5.70 s; existing chunk-size and SignalR annotation warnings. |
| API build | Passed, 0 warnings/errors; affected integration test builds also succeeded. |
| Documentation and data checks | 17 changed Markdown files have balanced fences and valid local file links; all 18 authored Definition JSON files parse with schema 2, captured State JSON parses, source fingerprint matches, and `git diff --check` passes. |

The strict child fixture initially had a NoAction receipt with an outcome entry and expected an unnecessary second model call; both assertions were corrected to the existing completion contract. A missing-source rejection initially expected Conflict from both stores; SQLite correctly returned NotFound. Earlier compile/test setup errors (clock dependency, test naming and xUnit assertion conventions) were corrected before passing verification. No failed run is counted as passed evidence.

## Executed cross-feature journeys

1. **Personal assistant / shared browser:** disposable real Chromium Sessions deliberately share authentication cookies/local storage only under PersistentAgent. Tabs, media/console/network/frame handles and popups stay Session-owned; foreign tab operations fail. Delayed popups, including `noopener`, stay with their opener Session while a sibling is active. Closing the sibling retains the first Session and profile bytes. Ownership-only binding does not invent a known sharing mode.
2. **Customer support:** Session-isolated model fixtures exclude other-customer transcript, stored Profile/Experience/learned memory and scratch; within-A follow-up retains A history. Two simultaneous UI Sessions use the same Instance and retain separate conversations. A follow-up stays in A's original Session. Chromium ephemeral contexts have distinct cookie/local storage and foreign handles cannot be used. These checks do not claim separation of a deliberately shared external SaaS account.
3. **Strict request:** the Stateless preset atomically disables Voice and requires ordinary fresh text Sessions. Three UI requests completed in distinct Sessions without old history; catalog Voice availability is false. Same-input replay remains idempotent and new input into an old request returns `fresh_session_required`. Old transcript, scratch under its retention policy and Run audit remain readable. The inline New chat action opens a fresh draft at all four widths. Fast completion drains the queued acknowledgement before closing its own transport, with no console errors in the repeated journey. Strict child tests keep the parent active until child/receipt settlement, deliver the existing receipt-based report when requested, and then terminalize; quiet and non-reporting children also converge. No extra model call is needed for the receipt-based report.
4. **Isolation under load:** preview reports busy/unavailable resource transitions truthfully while allowing authored policy to save. Live restrictions deny unsafe new Home/browser/tool/continuity operations under an older pin. Native provisioning/cancelled late-page ownership retains pending fencing until safe replacement; tightening then creates an isolated view without purging historical profiles. Existing deterministic Sandbox receipt, mount, generation, selection, cleanup and unknown-outcome tests remain green; no live server switch is claimed here.
5. **Offline observation:** Synthetic API 5097 and Vite 5187 use task-owned SQLite under `/tmp/agent-state-isolation-verification`, Browser and OpenSandbox disabled. State shows Partial, unavailable contributors and unknown counts. Injected 503 retains prior values marked Stale; Retry restores Partial with a new timestamp. Text chat remains functional. Malicious tool IDs, missing/expired claims, wrong Run, refresh floods and visibility revoked during observation are denied without granting new capabilities or allocating resources.
6. **Restart:** the task-owned SQLite host was stopped, backed up and converted through the actual offline `migrate-context` CLI (2 retained records), then restarted. A/B history and saved policy survive; A sends a new turn in the same Session with its older turns visible. InMemory/SQLite reopen tests preserve immutable policy/browser pins, tool checkpoints, atomic source receipts and same-admission dedupe. Durable rows never fabricate live Browser/Sandbox resources. New Runs take current Instance restrictions; old execution pins remain immutable.

During UI verification, the approval-demo fixture Instance used only temporary task data. Definition draft Home was changed by keyboard, saved and reloaded; published controls were read-only. Preset selection prepared a draft, Save committed exact fields, Home edits derived Custom and Discard restored saved settings. Conflict tests retain drafts. State and State & Isolation were exercised at 320, 390, 768 and 1440 px with document/main widths within their viewport. Screenshots inspected: `.playwright-mcp/isolation-final-mobile.png`, `.playwright-mcp/isolation-final-desktop.png`, `.playwright-mcp/strict-cta-mobile.png` (local ignored artifacts). Expected injected-503/restart disconnect diagnostics were distinguished from product failures; final clean navigation reported no console errors/warnings.

## Actual observation sample

[Redacted captured State response](agent-state-isolation-sample-2026-10-15.json), observed at `2026-10-15T11:43:36.771628Z`, has a 15-second freshness window. It reports 2 durable open Sessions, 0 paused, 3 terminal; 2 hosted runtimes and 2 attached transports; 7 terminal Runs and zero in each nonterminal bucket. Browser is unavailable with sharing unknown and all live counts null. Sandbox is unavailable and Session-isolated, with unknown active counts and a known cleanup-pending count of 0. Availability is Partial. Only the task Instance identifier was replaced with `<owned-instance>`. This is captured data, not a fabricated sample or a promise of present state after the hosts stop; it predates the final strict-child settlement follow-up.

## Acceptance mapping

| IDs | Implementation and local evidence |
| --- | --- |
| AS-01 | Existing owner/DI boundaries retained; one ContextAssembler, no provisional model path, state persistence or resource framework. |
| AS-02–05 | RuntimeStateApiTests, InMemory/SQLite count/reopen tests, State UI tests and live stale/Retry journey distinguish durable/hosted/attached state, bucket Runs once, bound provider reads and preserve partial contributors. |
| AS-06 | Owner HTTP scopes, current-Session metadata readers, forged argument denial, active nonexpired claim/owner checks, visibility revocation and authorized aggregate-only Instance summary. |
| AS-07–09 | Typed ContextAssembler and StateIsolationContextTests prove one replaceable Runtime ExecutionFacts section, conditional fixed Core rule, actual Tool Evidence, bounded polling/output and no duplicated current user input. |
| AS-10–12 | Finite defaults/sparse ceilings, derived presets, exact preview diff/fingerprint, multi-owner CAS transaction and audit rollback; UI draft/Custom/Discard/conflict behavior. |
| AS-13 | Legacy absent policy defaults, immutable resolved browser pins, retained A FieldSources/hash schema and offline sealed-record conversion/reopen variants. |
| AS-14 | Two-customer model/UI/workspace and Chromium isolation; shared external account limits remain explicit. |
| AS-15 | Fresh ordinary request Session, durable replay, retained audit/history, terminal/new-chat UI, receipt-fenced strict continuations and child settlement. |
| AS-16 | Memory admission/retrieval, Experience record/recall/maintenance and Continuity source/repair gates; live narrowed projection between model calls; historical records retained. |
| AS-17–18 | All Home access routes, relative cwd and indirect writes tested; Sandbox uses only private working staging and canonical Home is never mounted. |
| AS-19–21 | Disposable native Chromium storage, tab/popup ownership, sibling close, late cancellation, pending transition and safe isolation replacement. |
| AS-22–23 | Deterministic Sandbox store/mount/API/selection/uncertainty/cleanup/reopen and redacted state projection; prior live SDK ledgers are historical. New live SDK execution was not opted into. |
| AS-24–25 | Terminal-only scratch eligibility, unresolved effect/lease/child deferral, accounted completion receipts, background-surface recovery scan and live new-effect restrictions under immutable pins. |
| AS-26 | Focused existing approval, trigger/schedule, capability, completion inbox, background, continuity, browser privacy/credentials and Sandbox suites listed above. |
| AS-27 | Existing Ant Design 6 components/layout, keyboard save/read-only flows, four widths, stale/Retry and fresh-request UI; frontend unit/build checks. |
| AS-28 | Canonical architecture, ports, execution, frontend, API, persistence, testing, operations, implementation plan, README/TODO and product tooling context synchronized. |

Deployment still requires the documented stopped-writer backup/offline context conversion for retained legacy execution data. No Real deployment, full milestone acceptance, full CI suite, hosted CI, live-provider/customer-account or hardware-audio certification is implied by local completion.

After verification, the three task browser tabs were closed and only the verified task-owned API/Vite processes on 5097/5187 were stopped. Temporary SQLite, its pre-conversion backup and local screenshots remain available for inspection. Real hosts and user stores were left running/unchanged.
