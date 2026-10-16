# Automation authorization truncation verification — 2026-10-16

## Finding and correction

The earlier model-route correction left the platform classifier's output budget at 64 tokens. A typed length-limit terminal discarded the partial decision correctly, but had no bounded recovery. Responses also classified all incomplete events as tool truncation. The exact reported `everyday … think …` and `every week, think …` requests missed the deterministic recognizer.

The classifier now starts at 512 output tokens and permits one 1024-token recovery only after confirmed length termination. Both requests retain the same pinned model, supported effort, action, current turn, Core/Runtime/Evidence, referent and draft. Both share one 30-second TimeProvider deadline and caller cancellation. Text from the truncated attempt is discarded, even if it looks like a complete Allow. A completed Deny/Ambiguous, invalid JSON, tools, filtering, incomplete stream or unrelated provider failure does not retry or gain authority.

Real registration selects the least expensive recognized reasoning effort supported by the same catalog descriptor; unsupported `none` is never forced. Ordinary conversation selection, provider options and Run budgets are unchanged.

Responses recognizes only explicit `max_output_tokens` exhaustion with incomplete status and text/reasoning output on a no-tools request as `ModelCompleted(LengthLimit)`, preserving usage. Unknown/malformed reasons and partial or unsolicited tools remain failures. This follows the documented [Responses incomplete reason](https://developers.openai.com/api/reference/resources/responses/methods/retrieve); [output-token limits include reasoning tokens](https://developers.openai.com/api/reference/python/resources/responses/methods/create). Reasoning consumption is a plausible contributor to the incident, not a measured live-provider fact.

The bounded timing recognizer now includes `everyday`, optional recurring clock wording and `think`, preserving negation/qualification and current-turn authority gates. Scheduling still requires actual clock/weekday/timezone details or configured defaults. The scheduling Core procedure preserves requested autonomous work, reports an exhausted classifier blocker once and does not suggest rephrasing clear intent or substituting a Chat reminder. Existing error codes/reason tokens remain stable.

Structured information logs and trace activities record bounded route/action, effort, budgets, attempts, monotonic elapsed time, terminal/failure code, available output-token count and full-valid-decision status. They contain no user/draft text, classifier text/reasoning, credentials or provider bodies. Creation authorization grants no later Memory/harness/external-effect authority.

## Verification

The initial sandboxed test run could not bind the .NET runner's local IPC socket. The focused offline tests ran successfully with the required execution permission. Before fixing recognition and Responses mapping, new regressions reproduced four missed clear directives and the incorrect `toolCallTruncated` failure.

All commands use `TMPDIR=/private/tmp DOTNET_CLI_USE_MSBUILD_SERVER=0 dotnet test <project> --no-restore -m:1 -p:UseSharedCompilation=false --nologo --filter '<filter>'` from the repository root.

| Project | Focused filter | Result |
| --- | --- | --- |
| `tests/AgentCore.Application.Tests` | `FullyQualifiedName~ModelTriggerCommandAuthorizerTests\|FullyQualifiedName~TriggerCommandAuthorizerTests\|FullyQualifiedName~TriggerAuthorizationTests\|FullyQualifiedName~TriggerScheduleSemanticsTests\|FullyQualifiedName~ScheduleConversationContextTests\|FullyQualifiedName~AutomationActivation` | 127 passed |
| `tests/AgentCore.Infrastructure.Tests` | `FullyQualifiedName~AutomationAuthorizationProviderTests\|FullyQualifiedName~ResponsesTransportTests\|FullyQualifiedName~OpenAICompatibleLanguageModelTests` | 139 passed; 5 explicitly opt-in live tests skipped |
| `tests/AgentCore.Api.Tests` | `FullyQualifiedName~AutomationAuthorizationJourneyTests\|FullyQualifiedName~AutomationJourneyTests` | 41 passed |

Application cases verify both exact user turns, conventional daily/please forms, negative controls, unchanged full context across retries, partial Allow → final Deny, repeated exhaustion and no retry for other failures. Fake-time cases expire the shared deadline before recovery or during attempt two; caller cancellation also prevents recovery.

Production Real classifier registrations run against offline HTTP/SSE on both declared transports. Wire assertions verify selected model, supported effort, separate system/developer roles, increasing 512/1024 budgets, unchanged remaining payload and no tools. Safe-log assertions exclude private input, key and decision/reasoning content. Authentication/rate-limit/server/request errors, stream loss/malformed SSE, filtering and unsolicited/truncated calls remain closed. Legitimate offered-tool idle/cancellation recovery is retained.

The actual Session runtime with disposable SQLite performs creation/update, generation and scheduler admission. Expected and observed outcomes:

- The exact daily request bypasses the classifier; unrecognized equivalent wording exercises the Real adapter and bounded recovery. Both preserve one Automation through the Vietnam-time update: 09:00 Asia/Ho_Chi_Minh, background target, no report-back and unchanged task instructions. After reopen, two repeated scans per occurrence yield one quiet Run per due day, no parent-Chat delivery and no duplicate Automation.
- Two length-limited classifier calls on either transport create zero Automations, execute no work and return `authorization_classifier_invalid_output / TruncatedOutput` with truthful budget-exhaustion guidance.
- Weekly creation through either deterministic authority or completed classifier recovery rejects missing clock/day fields without saving anything. Explicit Friday/10:30 Vietnam details create one quiet background Automation with current-user provenance. The scripted conversation submits no altered recurrence or reminder fallback.

Canonical port, backend, protocol, test and operations documentation was synchronized; local links/fences and `git diff --check` were checked. This is a focused regression correction, not milestone acceptance.

## Deployment and remaining boundaries

No user database/Automation was modified, no live provider was called, and the user's existing host was not restarted. The running host's effective route, private token distribution and model's scheduling judgment were not verified by these offline fixtures. Apply the updated backend through an intentional API restart before retrying in the running app. The optional no-mutation live probe remains explicit opt-in. Hosted CI was not explicitly requested or dispatched; historical CI results are not evidence for this change. No frontend code changed, so no new browser/build verification is claimed.
