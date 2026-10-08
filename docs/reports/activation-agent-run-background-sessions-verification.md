# Activation, AgentRun and background Sessions verification

## Current status

The user approved destructive legacy retirement and disposable demo reset on 2026-10-08. The implementation is complete enough to run the shared production path; final regression and exact-SHA hosted acceptance remain in progress. This report supersedes the earlier foundation-only and retirement-blocked status. No final freeze is claimed before the remaining gates pass.

Reviewed starting main: `bd44046896df6f3e0fc2e7d15d60dd479a5349cd`. Foundation commit: `f98dbb6236ab976b5e2923e00a47ec90a140cf1e`. Final behavior candidate: `ef5109ff955c477838084f0c3da0dccaaf68b38a`. Hosted workflow [37742655032](https://github.com/trannamtrung1st/agent-core/actions/runs/37742655032) verifies that exact SHA; all five jobs must succeed before closure.

## Implementation and retirement

SessionRuntime is the mutable Session owner. Both attached and headless AgentRuns dispatch through SessionHost and its mailbox. The common coordinator uses revision/generation CAS claims, lease recovery and the same run/Activation/response identities across retries. Input batching retains one effective turn for the accepted suffix. Pending accepted input is repaired durably.

Occurrence intake atomically commits child Session, task input, Activation, AgentRun and acceptance receipt. Immediate `background.start` is fenced by the current parent user Run and same-instance ownership, with two children per parent Run and eight active children per owner. Frozen Definition/persona/model/Skill pins and checkpoint load state survive recovery; current authorization is still rechecked before provider and tool use.

Outcomes commit with their assistant entry; NoAction removes only its empty draft. External effects retain receipts across retries, exact approval decisions and uncertain-effect recovery. Initial child completion emits at most one eligible parent Activation; quiet/unavailable/policy-disabled parents receive a durable skip receipt. Later child user turns cannot report again.

Continue in chat adds ChatList to the existing background Session. It preserves ID, history, artifacts, workspace and immutable origin; opening it creates no Run. Background Work is Session-first. Admin Runs and shared run controls use the canonical AgentRun API with bounded owner-safe fields and revision-bound decisions.

Legacy WorkItem/ConversationTurnExecution production models, stores, workers, executors, DTOs, routes and EF mappings are retired. Migration `20261008063000_RetireLegacyExecution` removes their current tables. Historical migration source files are immutable. Startup validates the exact current model or migrates a fresh database; no old-schema converter, repair or compatibility suite remains. The approved demo reset removed the selected native demo database and sidecars; blobs were retained.

Packaging exposed an ImageSharp audit/license blocker. At the user's request, Infrastructure now uses MIT-licensed SkiaSharp 4.153.1 plus matching minimal Linux native assets. Metadata stripping, PNG/JPEG output, first-frame GIF/WebP normalization and pixel bounds remain verified. Cache version advances to `/3`; vulnerability auditing remains enabled.

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
| Frontend unit | 98 files, 743 tests passed (`NODE_OPTIONS=--no-experimental-webstorage pnpm run test --run --maxWorkers=1`) |
| Frontend build | TypeScript/Vite passed; existing chunk-size warning remains |
| Focused current store/runtime | 65 store cases, 62 runtime cases, 26 receipt/retry cases and 12 fast-admission/durability cases passed before final instrumentation |
| Full backend | Domain 161 and order-event plugin 4 passed; Infrastructure 809 passed, 15 opt-in/environment skips; Application 1,303 passed, one opt-in skip on the final cleanup fence; API 361 passed, three opt-in skips |
| API regression | Final full suite 361 passed, three opt-in skips; retry cleanup race corrected and 22 focused recovery/reattach cases passed |
| Browser | Prior primary 111/117 passed; all six failures corrected and focused batch 10 passed with paging setup corrected. All 16 phase journeys passed across corrected reruns, including all four secretary cases. Final full primary rerun in progress; automatic-scroll cursor error/retry and Admin paging both pass in the isolated two-case rerun |
| Compose/SQLite volume | Final Release image and SQLite restart passed locally; exact-SHA hosted Compose passed |
| Exact-SHA hosted | Candidate pushed; workflow 37742655032 running all five jobs |

Opt-in hosted provider/nopCommerce checks are not part of default key-free acceptance. Default Synthetic makes no paid provider calls. Manual audible headset quality is outside this cutover's automated evidence. Until the full browser/backend/phase and exact-SHA gates pass, this report remains an in-progress acceptance record.
