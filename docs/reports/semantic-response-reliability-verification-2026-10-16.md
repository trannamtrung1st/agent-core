# Semantic response reliability verification — 2026-10-16

## Verified incident evidence

Read-only inspection of the existing local SQLite AgentRun/checkpoint and assistant FailureReference, corroborated by `local/dev/api.log`, found:

| Field | Recorded evidence |
| --- | --- |
| AgentRun | `01a142c3-f806-717f-b56c-1fe893979764` |
| Diagnostic | `b213d9e5-9f41-47d6-9d7d-13df9dbe6dee` |
| Terminal code | `semantic-response-invalid` |
| Exact parser reason | `invalidBlocks` |
| Response channel | `responseFunction` |
| Model | `deepseek/deepseek-v4.1-flash`, catalog `deepseek-v41-flash`, `primary-llm`, low reasoning |
| Attempt | 1 of 3 |
| Execution phase | Work |
| Preserved receipts | Eight: seven web.search results and one web.fetch result, with web.fetch last |
| Remaining budget | 693,634 ms |
| Effect fence | None; no in-flight or indeterminate mutation recorded |

The verified terminal cause is rejection of response-function rich-block metadata during initial background Work. It is **not** evidence of truncation or a specific invalid block field: the original provider arguments/body and stop/token usage were not retained. The pinned conversation setting was 4,096 tokens; baseline tool-offered requests use an effective ceiling of 8,192. The catalog route selects ChatCompletions and prefers the response function. Those request/transport values are reconstructed from the pinned setting and baseline/catalog code, not captured incident request telemetry. New diagnostics record the actual prepared request ceiling and configured transport when available.

## Architectural issues found separately

1. Initial BackgroundOutcome requested the conversational semantic schema although work.complete owns its outcome; ignored display/block metadata could therefore fail useful work.
2. Background Runs were excluded from protocol repair. The allowlist covered presentation defects only.
3. Repair usage was reset within the tool loop, and a checkpoint repair reason could re-enter correction after restart without a durable dispatch outcome.
4. Run details retained reason/channel in checkpoint diagnostics but the generic terminal summary hid the actual contract violation; request ceiling, transport and recovery decisions were absent.

## Implemented scope

Initial background Work/Cleanup omit the conversational schema and response function. Cleanup retains work.complete. Existing ordinary valid completion and its bounded validation correction remain. A semantic corrective generation cannot gain another generation through completion validation.

The existing protocol repair allowlist now includes relevant JSON, response-function arguments, marker-envelope and output-limit failures. One complete JSON code fence normalizes before full authority/action validation; partial JSON is never salvaged. Conversational correction has no tools/schema/Memory and accepts a bounded plain final answer only after normal completion. Background correction offers only named work.complete and reports NeedsAttention from existing evidence, including explicit uncertainty or lack of verified results.

The allowance is saved before dispatch. Retry/restart preserves Run/Response identity, completed receipts, remaining budget/accounting and effect fences. A known transient transport retry can resume that restricted correction; an uncertain interrupted dispatch cannot obtain a new allowance. A checkpointed completion can settle without another generation. InvalidResponse is never added to ordinary provider retries. Authorization/policy violations, cancellation, hard deadlines, pending calls, visible output and uncertain effects remain fenced/terminal.

Diagnostics add bounded eligibility/strategy/outcome, actual generation ceiling and optional provider-neutral transport. Unknown reason/channel values are excluded from new diagnostic metadata. Policy stops retain contentFiltered instead of becoming format failures. The shared Run details component reuses Ant Design Flex/Typography/Descriptions and existing spacing ownership; readable recovery details appear before configuration metadata. No CSS overrides, second kit, storage migration or new orchestration service was added.

## Local verification

All verification used credential-free Synthetic or offline adapters and disposable stores. The existing Real host/database and incident Run were read only and were not restarted or retried.

| Check | Result and observed behavior |
| --- | --- |
| Application runtime, repair, Run durability, tool-call/format checkpoints and effect receipt journeys | **127 passed.** Format failure recovers once; repeated invalid output fails. Completed web.fetch/browser/file receipts are retained without replay. Both semantic transports, unsafe authority/Memory, cancellation, hard deadline, uncertain effects, reserved allowance restart, known transient resume and saved-completion restart are exercised. Existing completion and provider retry cases pass. A further focused regression verifies that invalid completion after semantic correction cannot obtain a second generation. |
| Infrastructure parser, transport and InMemory/SQLite reopen checks | **98 passed.** Exact complete fence normalization preserves destination rejection; policy stops are terminal with the correct channel. Same allowance, receipt and accounting survive store reopen/retry/reclaim. |
| Background Session HTTP journeys | **5 passed.** Owner-scoped Run/background details and existing continuation/admission contracts remain valid. |
| BackgroundWorkDrawer/Run details component tests | **34 passed.** Precise channel/reason, request ceiling/transport, correction result and receipt count are visible; existing navigation/approval/history interactions remain valid. |
| Playwright MCP against isolated Synthetic API 5098 / Vite 5188 | Started a real background task, observed exactly one truthful partial completion, opened original Run details, read invalidBlocks/responseFunction and completion-only success. At 1440/768/767/390/320px the drawer and recovery region had no horizontal overflow, and shared inner gap remained 8px. Recovery appears before configuration metadata. |
| Focused Playwright regression journeys | **2 passed.** Real background admission/report delivery for semantic recovery success and exhaustion, exact reason/channel/strategy/result, one attempt, five widths, and Escape dismissal; no page errors. |
| API build / frontend production build | Passed; API zero warnings/errors. Frontend retains existing bundle-size and dependency annotation warnings. |
| Documentation / whitespace | Canonical architecture, ports, implementation, protocol, persistence, testing and operations synchronized; changed-document relative links and git diff whitespace checked. |

Commands were focused, including `dotnet test` with filters for the listed classes, `pnpm --dir web exec vitest run src/features/chat/BackgroundWorkDrawer.test.tsx`, and the two `background semantic response` Playwright scenarios against the disposable host. Initial .NET execution hit sandbox IPC restrictions; the checks ran successfully with local execution permission. Temporary browser configuration was removed after verification.

No live-provider replay/UAT, CI dispatch, full CI suite, deployment or milestone acceptance was requested or claimed. The exact original invalid provider block remains unavailable. This change does not rewrite or automatically restart the failed incident Run.
