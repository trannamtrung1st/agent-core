# Activation, AgentRun and background Sessions verification

## Current status

The user approved destructive legacy retirement and disposable demo reset on 2026-10-08. The implementation is complete enough to run the shared production path; final regression and exact-SHA hosted acceptance remain in progress. This report supersedes the earlier foundation-only and retirement-blocked status. No final freeze is claimed before the remaining gates pass.

Reviewed starting main: `bd44046896df6f3e0fc2e7d15d60dd479a5349cd`. Foundation commit: `f98dbb6236ab976b5e2923e00a47ec90a140cf1e`. The requirement-by-requirement audit supersedes the earlier `ef5109ff` candidate. Workflow [37746910839](https://github.com/trannamtrung1st/agent-core/actions/runs/37746910839) on `89f64abd` passed acceptance and Compose, but exposed API retry ownership, frontend fixture and core browser failures. Those failed gates are retained as evidence; Workflow [37750577332](https://github.com/trannamtrung1st/agent-core/actions/runs/37750577332) on `1dd2b4b5` passed frontend, acceptance and Compose, but failed a browser popup collection-enumeration race and the drawer Escape fixture. The next candidate removes nullable Run-store execution and corrects both fixture races; all five hosted jobs remain required before closure.

## Implementation and retirement

SessionRuntime is the mutable Session owner. Both attached and headless AgentRuns dispatch through SessionHost and its mailbox. The common coordinator uses revision/generation CAS claims, lease recovery and the same run/Activation/response identities across retries. Input batching retains one effective turn for the accepted suffix. Pending accepted input is repaired durably.

Occurrence intake atomically commits child Session, task input, Activation, AgentRun and acceptance receipt. Immediate `background.start` is fenced by the current parent user Run and same-instance ownership, with two children per parent Run and eight active children per owner. Frozen Definition/persona/model/Skill pins and checkpoint load state survive recovery; current authorization is still rechecked before provider and tool use.

Outcomes commit with their assistant entry; NoAction removes only its empty draft. External effects retain receipts across retries, exact approval decisions and uncertain-effect recovery. Initial child completion emits at most one eligible parent Activation; quiet/unavailable/policy-disabled parents receive a durable skip receipt. Later child user turns cannot report again.

Background summary rows show a bounded file count (`50+` for a truncated metadata page), a two-line result/failure preview and exact source Automation navigation. The selected Automation survives refresh. Archived owners retain inspection but Continue returns 409 without changing visibility. Continue in chat adds ChatList to the existing background Session. It preserves ID, history, artifacts, workspace and immutable origin; opening it creates no Run. Background Work is Session-first. Admin Runs and shared run controls use the canonical AgentRun API with bounded owner-safe fields and revision-bound decisions.

Legacy WorkItem/ConversationTurnExecution production models, stores, workers, executors, DTOs, routes and EF mappings are retired. Migration `20261008063000_RetireLegacyExecution` removes their current tables. Historical migration source files are immutable. Startup validates the exact current model or migrates a fresh database; no old-schema converter, repair or compatibility suite remains. The approved demo reset removed the selected native demo database and sidecars; blobs were retained.

Packaging exposed an ImageSharp audit/license blocker. At the user's request, Infrastructure now uses MIT-licensed SkiaSharp 4.153.1 plus matching minimal Linux native assets. Metadata stripping, PNG/JPEG output, first-frame GIF/WebP normalization and pixel bounds remain verified. Cache version advances to `/3`; vulnerability auditing remains enabled.

## Implementation owners

| Phase | Current owner |
| --- | --- |
| A — domain and storage | `src/AgentCore.Domain/Conversation/Activation.cs`, `AgentRun.cs`; `src/AgentCore.Infrastructure/Persistence/SqliteAgentRunStore.cs`, `InMemoryAgentRunStore.cs`, `AgentRunStoreMapping.cs` |
| B/C — shared dispatch and recovery | `src/AgentCore.Application/Execution/AgentRunCoordinator.cs`; `src/AgentCore.Application/Sessions/SessionRuntime.AgentRuns.cs`, `SessionRuntime.RunCheckpoint.cs`, `SessionRuntime.RunControl.cs`; `src/AgentCore.Api/Realtime/SessionHost.AgentRuns.cs` |
| D — background admission | `src/AgentCore.Application/Execution/BackgroundOccurrenceIntake.cs`, `BackgroundSessionAdmissionFactory.cs`; `src/AgentCore.Application/Sessions/SessionRuntime.BackgroundStart.cs`, `SessionRuntime.RunOutcomes.cs` |
| E — initial completion reports | `src/AgentCore.Application/Execution/BackgroundCompletionReporter.cs`, `BackgroundCompletionProjection.cs`; `SessionRuntime.BackgroundCompletion.cs`; store completion partials |
| F — foreground and inspection | `src/AgentCore.Application/Sessions/SessionRuntime.Foreground.cs`; `src/AgentCore.Api/BackgroundSessionEndpoints.cs`; `web/src/features/chat/BackgroundWorkDrawer.tsx`, `AgentRunDetails.tsx`, `SessionArtifacts.tsx`; `web/src/features/admin/InstanceContinuitySection.tsx` |
| G — retirement and packaging | `RetireLegacyExecution` migration, canonical DbContext snapshot, strict `SqliteMemoryStore` startup, SkiaSharp `AttachmentProcessor`, current protocol/design/docs and exact-SHA gates |

## Acceptance and scenario coverage

The coverage below identifies current tests, not historical execution engines. Final milestone acceptance additionally requires every mandatory local and hosted gate below.

| Criteria / journeys | Canonical evidence |
| --- | --- |
| AC01–04, J1–2 | ActivationAdmissionTests, AgentRunContractTests, AgentRunCoordinatorTests, AgentRunAdmissionStoreTests, AgentRunDurabilityTests: batch/replay, CAS winner, same-ID attempt/recovery, stale-worker fencing |
| AC05–07, J8–9 | AgentRunToolCallCheckpointTests, AgentRunCheckpointFormatTests, EffectReceiptJourneyTests, SessionCaptureRecoveryTests, store approval/effect cases: exact hashes, expiry, receipt retention, indeterminate effects, pinned load state |
| AC08, J9 | Fresh/current-model SQLite reopen tests, native receipt recovery fixtures, API SQLite host recovery, current-schema Compose restart; historical migrations remain unchanged |
| AC09–10, J7 | Existing native initiative, voice, interruption, SignalR/MessagePack and browser speech fixtures; atomic live receipt/run and quiet settlement parity tests |
| AC11–12, J3/J12 | AgentRunDurabilityTests and AgentRunAdmissionStoreTests: committed prompt return, bounded fan-out, independent child, atomic response/NoAction/attention outcomes |
| AC13, J6–7 | Store occurrence/intake parity, AutomationJourneyTests, ContinuityBoundaryTests, unified-event-automation browser tests: separate recurrence Sessions, admitted pins, dedupe and revoked policy |
| AC14–15, J4/J11 | Completion projection/reporter/store/runtime cases: initial-only reports, replay, parent races/terminal/deletion/policy guards; eligible-parent browser journey |
| AC16–17, J5/J10 | BackgroundSessionJourneyTests, shared runtime headless completion tests, agent-run-background-session E2E: same Session foreground, follow-up user Run, no second report |
| AC18, J8/J11 | Owner-scoped API/store cases, shared run control tests, Session/Admin cursor paging, exact approval and diagnostics browser fixtures |
| AC19 | No Add to chat, mention chips, ContextRef or cross-Session context APIs introduced |
| AC20 | Production retirement inventory, canonical docs/design synchronization and final exact-SHA CI; pending until all gates pass |

Legacy test retirement preserves behavior under canonical owners: domain work transition cases map to AgentRunContractTests; work-store/handoff cases map to atomic AgentRunAdmissionStoreTests; standalone runner/coordinator cases map to AgentRunCoordinatorTests and AgentRunDurabilityTests; checkpoint/capture cases map to AgentRunToolCallCheckpointTests and SessionCaptureRecoveryTests. Existing real tool, approval, browser recovery and API SQLite journeys continue to run through the shared Session runtime. Old-schema compatibility assertions are intentionally retired rather than ported.

## Executed runtime journeys

- Playwright MCP on the reset Synthetic native host: send an actual background-start turn, inspect the completed child, Continue in chat to its original ID, and send a follow-up. Observed a new completed child user Run. The default General Assistant parent has initiative disabled; its completion receipt correctly skipped `parent-policy-unavailable`.
- Real eligible-parent Playwright E2E: publish a bounded Synthetic Definition with initiative enabled, send `[test:background-start]`, observe parent start and one completion report, open child history, Continue in chat and send `Check B too`. Observed same child Session, new user Run, both surfaces, one parent report and no page errors. This found and fixed drawer unmounting during the route transition.
- Integrated first-tool flow found `JsonElement.TryGetProperty` on array-valued tool output; SafeExecutionTrace now handles nonobjects. Model retry now waits durably on the same Run with preserved receipts rather than using an internal generation retry.
- A real fast-admission/coordinator race found a stale Claim revision. Both user and initiative fast paths now defer to the winning coordinator dispatch; the canonical runtime regression proves one provider request and one response.
- Detached cleanup regression: full API execution exposed extraction racing the next retry claim. Cleanup now shares admission exclusion, cancels obsolete cleanup waits and rechecks accepted work through the Session mailbox. The recovery/reattach suite passes 22 cases.
- Image processor: eight attachment tests passed on SkiaSharp, including metadata stripping, GIF/WebP normalization, MIME truthfulness and pixel bounds.
- Compose: Release image built with SkiaSharp; container recreation retained completed AgentRun outcome and waiting approval with redacted DTOs, Session catalog, published resources, Skills, binary home/scratch, Automation, credentials and bindings.

## UI and documentation evidence

The bounded Impeccable inspection and confirmation used the actual shared components at 1440×900, 768×900 and 390×844. Local evidence is under `local/verification/agent-run-layout-preview/`: `agent-run-catalog-final-{1440,768,390}.png`, `agent-run-history-final-{1440,768,390}.png` and `agent-run-admin-final-{1440,768,390}.png`. Shared Ant Design v6 tokens, operational drawer geometry, status text/icons, bounded reading regions, narrow-screen action targets and focus return were synchronized in the product design context. Functional browser fixtures cover approval expiry, cancelled/failed/retrying details, source navigation, pagination error/retry and same-Session Continue in chat. No second UI kit or composer context feature was introduced.

Canonical architecture, interfaces, event routing, implementation, frontend, protocol, persistence, testing and operations documents now describe the same owners. Historical freeze reports and migration sources remain unchanged. Link/anchor/fence validation checked 22 changed Markdown documents with zero issues, and all 15 canonical JSON examples parsed; `git diff --check` passed.

## Gate ledger

| Gate | Current result |
| --- | --- |
| Frontend unit | 98 files, 745 tests passed on `1dd2b4b5` (`NODE_OPTIONS=--no-experimental-webstorage pnpm run test --run --maxWorkers=1`) |
| Frontend build | TypeScript/Vite passed; existing chunk-size warning remains |
| Focused current store/runtime | 65 store cases, 62 runtime cases, 26 receipt/retry cases and 12 fast-admission/durability cases passed before final instrumentation |
| Full backend | Domain 161 and order-event plugin 4 passed; Infrastructure 809 passed, 15 opt-in/environment skips; Application 1,305 passed, one opt-in skip with mandatory Run admission; API 361 passed, three opt-in skips |
| API regression | Final full suite 361 passed, three opt-in skips; retry cleanup race corrected and 22 focused recovery/reattach cases passed |
| Browser | Full primary 118 passed on `1dd2b4b5`. All 16 phase journeys passed across corrected reruns, including all four secretary cases; hosted acceptance passed on `1dd2b4b5`. The final candidate additionally focuses Close before sending Escape after drawer animation |
| Compose/SQLite volume | Final Release image and SQLite restart passed locally; exact-SHA hosted Compose passed |
| Exact-SHA hosted | Corrected audit candidate pending push and all five successful jobs |

Exact local backend commands were `DOTNET_PROCESSOR_COUNT=4 dotnet test tests/AgentCore.{Application,Api,Infrastructure}.Tests/AgentCore.{Application,Api,Infrastructure}.Tests.csproj --nologo` (each project run separately). Domain and order-event plugin gates used their corresponding `dotnet test` projects. The final full browser invocation uses `CI=1 pnpm exec playwright test --project=synthetic --project=browser-stt --project=browser-browser --workers=1`, with every API/web port and SQLite path isolated. Phase projects are faithful-manual, admin-lifecycle, p76-admin, p97-harness, p9899-continuity, p910-continuity-maintenance and secretary-demo. Packaging uses `COMPOSE_PROJECT_NAME=agent-core-cutover-verification AGENTCORE_COMPOSE_PORT=5780 bash scripts/compose-sqlite-volume.sh`; its disposable resources were cleaned afterward.

Opt-in hosted provider/nopCommerce checks are not part of default key-free acceptance. Default Synthetic makes no paid provider calls. Manual audible headset quality is outside this cutover's automated evidence. Until the full browser/backend/phase and exact-SHA gates pass, this report remains an in-progress acceptance record.

## Final proposal audit corrections

- Compared every AC01–AC20 and J1–J12 requirement with production code and current regression owners. Added the missing summary file count/result preview, exact View Automation navigation with refresh selection, and current active-owner foreground guard. The API journey includes 51 artifacts and archived-owner rejection without mutation; the new mobile browser journey opens a real authored source.
- Hosted `89f64abd` failed the durable Experience repair/retry journey. The full local API suite reproduced attempt two remaining Running. A gated Application regression failed before the fix: the previous response terminal flag made a new background brain evaluation look idle. Running claims now retain accepted-work ownership before the new response starts. All 13 durability cases and the full API suite (361 passed, three explicit opt-in skips) pass without increasing the timeout.
- The hosted ChatApp fixture now awaits the drawer's async render inside `act`. The 43 ChatApp/background component cases pass. The workspace browser fixture now waits for a Session-only control before Ready, since the initial new-chat shell also presents Ready. One local repeated workspace case was invalidated by editing `index.html` during the run and triggering Vite reload; five unaffected cases passed. The corrected focused run and final full runner supply acceptance evidence.
- Added a served SVG favicon rather than filtering resource failures. Verified HTTP 200 and SVG MIME through MCP. Fixture ports can be isolated with the API/web ports so independent Synthetic hosts do not share their browser fixture service.
- Actual MCP: created an Automation, ran it, inspected its child result/file count, opened the exact Automation and refreshed with the same details expanded, continued the child Session, sent “Check B too” and observed “Hello from synthetic.” Browser console reported no errors on the final successful flow. Wrong-URL/selector exploratory tool calls are not acceptance evidence.

- Final review removes nullable `IAgentRunStore` construction, all no-store dispatch/checkpoint/capability bypasses and the internal generation retry loop. Existing runtime fixtures now supply canonical stores, including real SQLite admission and reopen. Canonical admission exposed two real lifecycle gaps: pausing before generation now resumes the same admitted Run; durable terminal saves settle the Run even when their continuation callback is suppressed. Superseding an unstarted Run commits cancellation. Protocol-repair state survives a durable retry without repeating completed effects; oversized checkpoints reject before dispatch. Full Application: 1,305 passed and one opt-in skip; API: 361 passed and three opt-in skips.
- `1dd2b4b5` hosted failure corrections snapshot the browser page collection before asserting and focus the drawer Close control before keyboard Escape. Provider/browser focused integration: 65 passed and five opt-in skips. SQLite summary/reopen: ten passed.

- Final source inventory corrects the browser lease parameter/field to `sessionId`: it already binds and releases the real Session, with no change to dispatch behavior. All 51 browser lifecycle/native occurrence/durability integration cases passed after this terminology-only correction. Workflow `37755415521` on the preceding `8d442973` candidate is superseded; the final exact-SHA gate must include this correction.
