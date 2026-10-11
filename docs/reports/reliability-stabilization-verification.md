# Reliability stabilization verification

Date: 2026-10-12. Scope: the requested scheduling/background reliability successor on the existing Automation → Occurrence → Activation → AgentRun architecture. Historical milestone freezes are unchanged. Existing unrelated frontend/design edits were preserved.

## Implemented behavior

- Schedule errors round-trip `instructions` and original arguments. One immediately following interval-only correction preserves the task, destination and requirements. Clear timed imperatives bypass the classifier; negation and unrelated follow-ups do not authorize scheduling. Partial classifier output followed by failure is rejected.
- Initial background prose enters one checkpointed completion-only phase. Core offers only named `work.complete`, reserves a final step and bounded time/output within the existing Run budget, and refuses new substantive tools. A retry restores the completion phase and completed receipts. Hard expiry and genuine provider errors retain their causes.
- Missing completion is execution/CompletionRequired (`backgroundCompletionRequired`, `backgroundCompletion`). Tool-call incompleteness, semantic response validation, transport failure and budgets remain distinguishable. Run details expose bounded safe failed-attempt metadata, remaining checkpoint budget and last tool result; terminal provider failures share their conversation diagnostic ID.
- Requested terminal reports use deterministic paragraph formatting through the existing durable parent admission and mailbox. The existing busy-turn and receipt gates retain single delivery and source results. Reporting needs no provider generation.
- `/health.scheduler` and Admin Activity → Runs expose starting/ready/degraded/stale status and pass/success timestamps. Existing scheduler/due-occurrence and receipt views remain. Runtime metrics add scheduler passes, terminal outcomes/retry recovery and completion admission/delivery latency without task content or identifier labels.
- Live catalog qualification now includes completion-only `work.complete` after a verified tool receipt for `gpt-6-luna`, `claude-haiku-5.5`, `gpt-6.1-sol` and `deepseek-v41-flash`; existing qualification covers substantive continuation, structured conversation and vision.

## Functional evidence

`ReliabilityJourneyTests` uses temporary SQLite and the owned production runtime/coordinator. Five scheduling journeys create an Automation, reopen storage, advance due time, execute the occurrence and observe exactly one greeting in its original conversation. They cover `say hello in 15s`, direct recurring 60 seconds, and 30 → 60 seconds via `okay so 60s`, `60 seconds`, and `1 minute`. The scripted correction deliberately offers a wrong replacement task, destination and interval; Core preserves the original task and destination. Additional cases deny explicit negation and consume a draft on unrelated input.

Eight background journeys save a workspace file and artifact, then exercise completion success, repeated prose, prohibited finalization work, transient unavailable-provider retry after reopen, invalid structured response, incomplete tool call, authentication failure and reserve entry. Every recovery request exposes only named `work.complete` without a conversational response envelope. Artifacts survive reopen, the successful file effect occurs once, and no prohibited replay file is created. A FakeTimeProvider test crosses the work cutoff and hard expiry: cutoff completes truthfully with NeedsAttention; hard expiry fails `run-deadline` without a fabricated result or retry.

Two additional integrated cases hold a real parent Runtime's user reply open while a successful or failed background Run becomes terminal. Reporting stays pending, no completion-report Run is admitted and the user's assistant entry stays Streaming. After the foreground reply completes normally, the terminal report is delivered once. SQLite reopen preserves the original child status/artifact, one foreground reply and one completion report. These cases were added after the full backend run and passed in the final 19-case reliability/readiness regression.

The Synthetic browser journey observes one durable parent failure report, no literal `\\n\\n`, completion delivery `delivered`, and original Run diagnostics in Background Work. The successful child/artifact journey continues the same child conversation and retains the original result. Existing destination and diagnostic regressions also pass.

Playwright MCP exercised the actual Admin controls against API `5280`, web `5373` and native fixture `5291`, with disposable SQLite/workspace/artifact roots under `/private/tmp/agent-core-reliability-browser`. It created an Instance, opened Activity → Runs and observed live Scheduler: Ready/last-pass metadata, then opened the actual failed background conversation and its original result. The report rendered three paragraphs; details showed completion recovery, attempt 1, `InvalidResponse/backgroundCompletionRequired/backgroundCompletion`, 30 seconds remaining at the checkpoint and zero tool receipts. The dialog had no horizontal overflow. No browser console errors or failed related API requests were observed.

## Checks

| Gate | Result |
| --- | --- |
| Focused Application scheduling/authorizer/timer regressions | 27 passed |
| SQLite reliability and scheduler readiness regressions | 19 passed, including both active-parent completion/restart cases |
| Reliability, destination and completion-boundary regression run | 47 passed before the added diagnostics/reserve cases |
| Frontend production build | Passed; existing large-bundle warning |
| Background Session and exact-destination Playwright cases | 4 passed |
| Diagnostic Playwright regressions | 10 passed |
| Full backend suites on final implementation | 3,583 passed / 21 live skips: Domain 195, Application 1,694, Infrastructure 1,174, API 516, plugin 4 |
| Full frontend unit suite | 925 passed with experimental Node Web Storage disabled |
| Diff whitespace checks | Passed |
| Changed documentation fences, report link target and complete health JSON example | Passed |
| Live catalog qualification | Explicit opt-in attempt: 3 theories skipped because no OpenRouter credential is configured; no hosted requests executed |
| Hosted CI/deployment verification | Not executed in this local request |

Backend commands use `TMPDIR=/private/tmp DOTNET_CLI_USE_MSBUILD_SERVER=0 dotnet test ... --no-restore -m:1 -p:UseSharedCompilation=false --nologo` because the restricted environment could not create MSBuild sockets with its default temporary directory. The focused API filter is `FullyQualifiedName~ReliabilityJourneyTests|FullyQualifiedName~SchedulerReadinessTests`; the focused Application filter selects `Background_work_cutoff`, `ScheduleDraftAdmissionTests` and `ModelTriggerCommandAuthorizerTests`. The final regression command targets `AgentCore.sln`.

Live qualification was attempted with `AGENTCORE_MODEL_CATALOG_LIVE=1 dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~ModelCatalogLiveCompatibilityTests --nologo`; all three theories skipped on the missing-credential gate. The experimental Free route retains its separate existing opt-in probe; custom catalog overrides require corresponding live qualification.

Frontend checks: `pnpm run build`; `NODE_OPTIONS=--no-experimental-webstorage pnpm exec vitest run --maxWorkers=2`. The shell Node is 22.18.0 while `pnpm exec node` selects 26.7.0. The initial unmodified unit command failed because experimental Node Web Storage shadowed JSDOM localStorage; the corrected command disables that feature without changing application/tests.

Browser command: `PLAYWRIGHT_FAITHFUL_MANUAL=1 PLAYWRIGHT_API_PORT=5280 PLAYWRIGHT_WEB_PORT=5373 PLAYWRIGHT_FIXTURE_PORT=5291 PLAYWRIGHT_SQLITE_PATH=/private/tmp/agent-core-reliability-browser/synthetic.db pnpm exec playwright test e2e/agent-run-background-session.spec.ts e2e/automation-destinations.spec.ts e2e/diagnostic-details.spec.ts --project synthetic`. The first diagnostic run omitted the fixture-port variable and failed the native sign-out scenario; the corrected diagnostic command passed all ten cases. Only the known disposable Synthetic servers were reused.

## Remaining acceptance limits

No screenshot diagnostic/correlation IDs or original hosted logs were supplied, so this work does not identify the exact historical pictured Run or prove its selected provider/model. No OpenRouter API key is present in environment or the API's user-secrets configuration. The new real-model qualifier and existing catalog qualifiers must run with an authorized configured key before hosted protocol acceptance; Synthetic evidence does not establish live-model reliability. The initial investigation did not execute hosted CI or deployment verification. The subsequent user-authorized review and [explicit greeting fix](greeting-scheduling-authorization-verification.md) includes commit/push and exact-candidate CI verification.

The optional fixed-reminder deterministic delivery recommendation remains a separate design decision; this pass preserves scheduled AgentRun execution and implements deterministic delivery only for already admitted background terminal reports. There is no new scheduler, model timeout increase, retry-limit increase, public debug endpoint, persistence migration or multi-agent feature.
