# Automation descriptive-target resolution — 2026-10-12

The preceding [activation fix](automation-activation-verification-2026-10-12.md) repaired authorization and enabled-state mutation, but its contiguous name matcher rejected `enable the say hello to me 60s automation job again` against the stored name `Say hello every 60s`. Before this change, four of six expanded Synthetic Chat cases failed while the original two short-request cases passed.

The focused successor resolves owned candidates using literal descriptive words from a name or one non-negated instruction clause. Cadence and bounded reference wording are parsed separately, and word order can vary. Every descriptive word must match; there is no fuzzy ranking, synonym expansion, partial-word match or combining separate fields/clauses. Explicit cadence is enforced for named targets, IDs and trusted referents. Multiple candidates or conflicting cadences require clarification. The model-selected ID must be the resolved target; existing revision, terminal-state and policy checks still apply. Scheduler and authorization architecture are unchanged. The [backend specification](../12-backend-implementation-spec.md#chat-activation-of-existing-automations) owns these rules.

## Runtime observations

`AutomationActivationJourneyTests` submits the original short wording, the exact failed wording, reordered words and extra reference wording through the Synthetic SignalR/MessagePack Chat fixture using temporary SQLite. The model scripts list → inspect → enabled-only update; the separate authorization classifier is unavailable if called. Observed: zero classifier calls, original settings and child identity retained, exactly one Automation, one admission after 60 seconds, no repeat admission at that instant, and a completed scheduled Run. Duplicate matching schedules remain disabled with `ambiguous_target`, even when the model deliberately selects the first.

The Synthetic tool executor also checks instruction-only matches, a different-cadence candidate excluded from resolution, mismatched selected IDs, explicit ID/referent cadence mismatches, unresolved references, cadence-only ambiguity, partial/missing descriptive words, negated instructions, evidence split across clauses/fields, conflicting/equivalent cadences and numeric overflow. Rejections retain both candidates' disabled state and revision; successful resolution activates only the unique candidate. The earlier owner, grant, revision and terminal-state regressions remain included.

## Local checks

Commands ran from the repository root; fixtures used no live provider credentials or inference calls.

| Command | Result |
| --- | --- |
| `dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --nologo -m:1` | 1,772 passed; 8 opt-in live checks skipped |
| `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~AutomationActivationToolTests\|FullyQualifiedName~HostedTriggerToolTests' --nologo -m:1` | 37 passed |
| `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --filter 'FullyQualifiedName~AutomationActivationJourneyTests\|FullyQualifiedName~ChatAutomationJourneyTests\|FullyQualifiedName~AutomationJourneyTests\|FullyQualifiedName~MultiTriggerAutomationJourneyTests\|FullyQualifiedName~AutomationDestinationJourneyTests' --nologo -m:1` | 54 passed |
| `git diff --check` and affected Markdown file links | Passed |

These checks establish backend workflow behavior with controlled model decisions. Browser UI and live-provider interpretation were not exercised; no frontend code changed and no live Automation records were modified. This is a focused P5 behavior correction, not a new milestone acceptance claim. CI is not awaited, per the user's instruction.
