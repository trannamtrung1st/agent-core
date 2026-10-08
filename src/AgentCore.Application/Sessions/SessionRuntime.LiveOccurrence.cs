using AgentCore.Application.Ports;
using AgentCore.Application.Triggers;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private readonly ITriggerStore? _triggerOccurrences;

    private async Task CompleteQuietLiveOccurrenceAsync(AgentTrigger trigger, CancellationToken ct)
    {
        if (_triggerOccurrences is null || !ToolResources.IsOccurrence(trigger.Kind)
            || _boundAgentRun?.Admission.Activation.TriggerOccurrenceId == trigger.EventId) return;
        await _triggerOccurrences.CompleteLiveEvaluationAsync(trigger.EventId, SessionId, _time.GetUtcNow(), ct)
            .ConfigureAwait(false);
    }
}
