using AgentCore.Application.Ports;
using AgentCore.Application.Triggers;

namespace AgentCore.Application.Tools;

/// <summary>
/// Provider-neutral tool execution outcome. <see cref="Text"/> is the bounded JSON/text payload;
/// optional <see cref="Parts"/> are request-local model content and are not counted as textual tool output.
/// </summary>
public sealed record ToolExecutionResult(
    string Text,
    IReadOnlyList<ModelContentPart>? Parts = null,
    bool ReplaceTriggerProposal = false,
    PendingTriggerProposal? TriggerProposal = null,
    string? WorkspaceCwd = null,
    bool OutcomeUnknown = false, bool RecoveryRequired = false, string? SafeFailureSummary = null, bool EffectObserved = false)
{
    public static ToolExecutionResult FromText(string text) => new(text);
}
