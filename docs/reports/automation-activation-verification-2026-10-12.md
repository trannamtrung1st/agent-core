# Existing Automation activation verification — 2026-10-12

The Chat request `enable the 60s hello schedule` now enables an existing owned Automation through deterministic supporting lookup and activation authorization. `automation.enable` is available under explicit grants; published definitions that already grant `automation.update` can use an enabled-only update. Published definition files and grants were not modified.

The change follows the existing Automation authoring path and P5 trigger boundaries. It does not introduce a scheduler implementation or claim acceptance of a new milestone. See the [implementation contract](../12-backend-implementation-spec.md#chat-activation-of-existing-automations), [tool contract](../14-api-and-realtime-protocol.md#existing-automation-activation-tool-contract) and [regression strategy](../16-testing-strategy.md#existing-automation-activation-regression).

## Executed use cases

`AutomationActivationJourneyTests` submits the exact request through a SignalR/MessagePack Chat connection to the Synthetic API fixture backed by temporary SQLite. A scripted conversation model chooses list → inspect → enabled-only update; the separate authorization classifier reports unavailable if invoked. Observed: zero classifier calls, the original Automation ID and settings retained, one stored Automation, no due occurrence before 60 seconds, one admission after 60 seconds, no duplicate admission at that instant, and one completed scheduled Run.

With two matching owned hello schedules, the model deliberately selects the first. Observed: `ambiguous_target`, both candidates remain disabled, and no replacement is created. This establishes Core enforcement independently of model cooperation.

The real Synthetic tool executor exercises dedicated enable, existing update compatibility, disable, missing/ambiguous/wrong/foreign targets, stale revisions, cancelled/completed/expired/exhausted records, an elapsed expiry, detached execution, missing grants and attempted content injection. Rejected operations preserve state and revision; successful activation preserves instructions, cadence, child revision, model requirements and destinations. An unavailable shared-authoring fallback rejects a confirmed content/status update without a partial write.

Related destination journeys also verify that a rejected one-shot occurrence remains completed and cannot be rewritten as disabled. Recurring suspended Automations can still be disabled while retaining their destination. Authorizer tests cover negation/history, supporting reads, refusal to create a replacement, and separate bounded diagnostics for malformed JSON, invalid decisions, truncation, incomplete output, incompatible channels, oversized output and provider failures.

## Local checks

All commands ran from the repository root with Synthetic fixtures and no provider credentials or inference calls.

| Command | Result |
| --- | --- |
| `dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --nologo -m:1` | 1,772 passed; 8 opt-in live checks skipped; zero failures |
| `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~AutomationActivationToolTests\|FullyQualifiedName~HostedTriggerToolTests' --nologo -m:1` | 19 passed; zero failures |
| `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter 'FullyQualifiedName~AutomationActivationJourneyTests\|FullyQualifiedName~ChatAutomationJourneyTests\|FullyQualifiedName~AutomationJourneyTests\|FullyQualifiedName~MultiTriggerAutomationJourneyTests\|FullyQualifiedName~AutomationDestinationJourneyTests' --nologo -m:1` | 50 passed; zero failures |
| `git diff --check` | Passed |

The initial sandboxed .NET attempt could not create MSBuild worker IPC. The practical fallback used approved local IPC/loopback access and one MSBuild worker. Intermediate checks caught an obsolete enable-as-create expectation and terminal-to-disabled assertions; these now assert the intended management semantics. New terminal-state fixtures were corrected to respect provenance timestamps and audit metadata before the final passing run.

This is backend integration evidence using controlled model choices, not a live-provider conversation or browser UI verification. No frontend code changed, and live Automations were not inspected or modified. Hosted CI is not awaited, as requested by the user.
