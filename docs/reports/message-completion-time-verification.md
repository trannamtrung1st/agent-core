# Message completion time verification

2026-10-10 (Asia/Ho_Chi_Minh). Bounded regression repair to the existing chat and P8.5 application-message behavior; no milestone reopened or accepted.

## Behavior

The assistant placeholder retains its immutable generation-start `createdAt`. A successful final reply records server `completedAt` once in the Session mailbox, persists it as nullable UTC milliseconds, and sends it in the terminal event and HTTP/reattach history. Message and header time use that value. User messages and progress notices retain their publication time. Historical replies without a recorded completion time use the latest same-response progress notice as a lower bound. No timestamp uses reload time or reorders durable entry sequences.

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

Reopened that disposable Synthetic database on 5107 with Vite on 5207. The stored billing progress/final history rendered successfully. Repeating its authored request after host restart was rejected by the existing provider Run authority fence (`Current owner, source policy or model no longer permits this Run`); it produced a failed entry without completion time. This separate authored-session restart path was not repaired or claimed as passing.

Started a fresh chat with the built-in examiner through visible controls and sent `Hello`. Observed `Hello from synthetic.`. Its start time was `2026-10-09T18:01:09.334Z`; live displayed time and HTTP completion time were both `2026-10-09T18:01:09.371Z`. After reload, message and header retained that completion time. No browser console errors occurred. Stopped only the disposable hosts started for this verification.

Logs: `/tmp/message-time-runtime.log`, `/tmp/message-time-voice.log`, `/tmp/message-time-store.log`, `/tmp/message-time-api.log`, `/tmp/message-time-web.log`, `/tmp/message-time-build.log`, `/tmp/message-time-e2e.log`, `/tmp/message-time-mcp-api.log`. Full solution/whole frontend suites, hosted-provider calls, physical voice checks and hosted CI were not run for this bounded repair.
