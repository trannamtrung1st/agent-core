# Message completion time verification

2026-10-10 (Asia/Ho_Chi_Minh). Bounded regression repair to the existing chat and P8.5 application-message behavior; no milestone reopened or accepted.

## Behavior

The assistant placeholder retains its immutable generation-start `createdAt`. A terminal assistant reply (completed, failed or interrupted) records server `completedAt` once in the Session mailbox, persists it as nullable UTC milliseconds, and sends it in the terminal event and HTTP/reattach history. Message and header time use that value. User messages and progress notices retain their publication time. Historical replies without a recorded completion time use the latest same-response progress notice as a lower bound. No timestamp uses reload time or reorders durable entry sequences.

## Executed checks

| Command | Result |
| --- | --- |
| `dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --filter 'FullyQualifiedName~ApplicationMessageTests\|FullyQualifiedName~ResponseProgressRuntimeTests' --nologo -m:1` | 26 passed. FakeTimeProvider advances during generation: progress is later than response creation, final completion is later than progress, and public history/terminal output match the snapshot. |
| `dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-build --filter 'FullyQualifiedName~SpeechPlaybackTests\|FullyQualifiedName~ClientSpeechSegmentRuntimeTests\|FullyQualifiedName~VoiceRealtimeRegressionTests' --nologo` | 43 passed. Existing synthetic playback and interruption regressions. |
| `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter FullyQualifiedName~MemoryStoreContractTests --nologo -m:1` | 35 passed. In-memory/SQLite save retry, fresh store history read, forward migration preserving old text/sequence/start time and leaving old completion time null. |
| `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter 'FullyQualifiedName~HealthAndSessionLifecycleTests\|FullyQualifiedName~SessionCatalogApiTests\|FullyQualifiedName~SqliteHostRecoveryTests' --nologo -m:1` | 35 passed. HTTP/session lifecycle and durable recovery. |
| `pnpm exec vitest run src/features/chat/Conversation.test.tsx src/state/sessionStore.test.ts src/features/chat/chatTime.test.ts src/features/chat/ChatApp.test.tsx` | 113 passed. Completed and historical grouped reply times, terminal hydration, duplicate-terminal time preservation, and chat/header consumers. |
| `pnpm run build` | Passed; existing bundle-size and SignalR annotation warnings. |
| `pnpm exec playwright test e2e/p8.5-messaging-skill-journey.spec.ts --project=synthetic` | 1 passed. Publish the fixture Definition and instance, send the scripted billing request, observe progress then final answer, compare displayed final time to HTTP `completedAt`, and reload with the same time. |
| `git diff --check` | Passed. |

The CLI browser run used `CI=1 PLAYWRIGHT_FAITHFUL_MANUAL=1 PLAYWRIGHT_API_PORT=5107 PLAYWRIGHT_WEB_PORT=5207 PLAYWRIGHT_FIXTURE_PORT=5109 PLAYWRIGHT_SQLITE_PATH=/tmp/message-time-e2e/store.db` plus disposable workspace/artifact/attachment/credential roots under `/tmp/message-time-e2e`. The existing Real host and its data were untouched.

## Playwright MCP

Reopened that disposable Synthetic database on 5107 with Vite on 5207. The stored billing progress/final history rendered successfully. Repeating its authored request after host restart was rejected by the existing provider Run authority fence (`Current owner, source policy or model no longer permits this Run`); it produced a failed entry without completion time. Follow-up inspection showed that the browser fixture deliberately archives this instance at the end of its journey. The subsequent authority rejection is expected for the archived instance; it is not evidence of a restart regression. Failed replies now also record terminal time, as verified below.

Started a fresh chat with the built-in examiner through visible controls and sent `Hello`. Observed `Hello from synthetic.`. Its start time was `2026-10-09T18:01:09.334Z`; live displayed time and HTTP completion time were both `2026-10-09T18:01:09.371Z`. After reload, message and header retained that completion time. No browser console errors occurred. Stopped only the disposable hosts started for this verification.

Logs: `/tmp/message-time-runtime.log`, `/tmp/message-time-voice.log`, `/tmp/message-time-store.log`, `/tmp/message-time-api.log`, `/tmp/message-time-web.log`, `/tmp/message-time-build.log`, `/tmp/message-time-e2e.log`, `/tmp/message-time-mcp-api.log`. At the initial verification, full solution/whole frontend suites, hosted-provider calls, physical voice checks and hosted CI were not run. Follow-up coverage is recorded below.

## Follow-up review

The review extended durable terminal time to failed and interrupted assistant entries and their wire events. Interruption captures the time before clearing the active response. The frontend preserves a settled status/time against conflicting late terminal events even after history hydration. Tests cover delayed success/failure after progress, supersession interruption, all terminal display states and legacy fallback. A stale Real-host fixture was also aligned with the already-shipped Low reasoning default.

Focused runtime/voice tests passed 70 cases; MemoryStoreContractTests passed 35 cases. Frontend focused state/conversation tests passed 86 cases before splitting the six terminal/legacy combinations into independent cases; the final broad run passed all 89 state/conversation cases. Build passed with existing SignalR annotation and chunk-size warnings.

The Synthetic CLI browser run passed both the P8.5 progress/final journey and Stop after refresh. The latter now compares the displayed interruption time to HTTP history and checks that reload preserves it. Command: `CI=1 PLAYWRIGHT_FAITHFUL_MANUAL=1 PLAYWRIGHT_API_PORT=5107 PLAYWRIGHT_WEB_PORT=5207 PLAYWRIGHT_FIXTURE_PORT=5109 PLAYWRIGHT_SQLITE_PATH=/tmp/message-time-review-e2e/store.db pnpm exec playwright test e2e/p8.5-messaging-skill-journey.spec.ts e2e/text-conversation.spec.ts --project=synthetic --grep 'P8.5 sends|Stop after refresh'`, with disposable persistence roots under `/tmp/message-time-review-e2e`.

Through Playwright MCP, started a fresh built-in examiner chat, sent `Please hold the line`, observed streaming `Hello`, pressed Stop, and reloaded. Assistant creation was `2026-10-09T18:10:29.646Z`; the live displayed time, HTTP `completedAt` and reloaded displayed time were all `2026-10-09T18:10:39.428Z`, with `interrupted` / `userStop` preserved. No console errors or failed API requests appeared during the exercised flow. After stopping the disposable hosts, the still-open page produced expected connection-refused polling/reconnect errors; the page was then closed. Only the disposable hosts were stopped.

The first unrestricted frontend run encountered admin-test timeouts under parallel load and was stopped; the rerun limits workers to two. The first full API run passed 399 tests, skipped two opt-in live tests, and failed the stale Medium default assertion corrected above. The final API rerun (`dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --nologo -m:1`) passed 400 tests and skipped two opt-in live tests.

Review logs: `/tmp/message-time-review-runtime.log`, `/tmp/message-time-review-store.log`, `/tmp/message-time-review-web.log`, `/tmp/message-time-review-web-limited.log`, `/tmp/message-time-review-api-final.log`, `/tmp/message-time-review-e2e.log`, `/tmp/message-time-review-build.log`. Hosted provider calls, physical voice checks, the full backend solution and hosted CI were not run; this is a local regression repair, not milestone acceptance.

Final frontend coverage: `pnpm exec vitest run --maxWorkers=2` exercised 830 cases: 759 passed, 70 failed because Node exposed unavailable experimental Web Storage, and one unrelated Admin publish case timed out under load. `NODE_OPTIONS=--no-experimental-webstorage pnpm exec vitest run src/services/api.catalog.test.ts src/services/artifacts.test.ts src/services/attachments.test.ts src/services/realtime.race.test.ts src/services/schedules.api.test.ts src/services/sessionModel.test.ts --maxWorkers=2` passed all 70 affected cases. `pnpm exec vitest run src/features/admin/AdminApp.test.tsx --maxWorkers=1 -t 'saves visible instructions before publishing a draft'` passed the timed-out case in 31 seconds without changing its timeout. All 830 cases therefore passed across the broad run and targeted retries; the unmodified-environment single full run was not clean. Retry logs: `/tmp/message-time-review-storage-final.log`, `/tmp/message-time-review-admin-retry.log`. Final `git diff --check` passed.
