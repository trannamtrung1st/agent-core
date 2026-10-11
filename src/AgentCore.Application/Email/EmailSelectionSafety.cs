using AgentCore.Application.Ports;
using AgentCore.Application.Execution;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Email;

public static class EmailSelectionSafety
{
    public static bool CanContinue(AgentRun source, EmailAccountSelection request)
    {
        if (source.AgentRunId != request.SourceRunId || source.SessionId != request.SessionId || source.Owner.AgentInstanceId != request.AgentInstanceId
            || source.Status != AgentRunStatus.Completed || source.Admission.Activation.Kind != ActivationKind.UserTurn
            || source.SideEffect.Disposition is AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate
            || !AgentRunToolCallCheckpoint.TryReadState(source.Checkpoint, out var messages, out var uncertain, out var blocked)
            || uncertain || blocked is not null || messages is null || AgentRunToolCallCheckpoint.PendingCalls(messages).Count != 0) return false;
        var tools = messages.Where(m => m.Role == ModelRole.Tool).ToArray();
        return tools.Any(m => m.Name == ToolCatalog.EmailRequestAccount)
            && tools.All(m => ToolRegistry.TryGet(m.Name ?? "", out var descriptor) && descriptor.Effect == ToolEffect.ReadOnly);
    }
}
