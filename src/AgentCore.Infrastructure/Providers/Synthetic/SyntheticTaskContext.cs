using AgentCore.Application.Context;
using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.Providers.Synthetic;

/// <summary>Script fixtures inspect the same typed task boundary as hosted requests.</summary>
internal static class SyntheticTaskContext
{
    internal static string Text(ModelRequest request)
    {
        if (request.Context?.Sections.FirstOrDefault(s => s.Id == ContextSectionId.RunTask) is { } task)
            return task.Message.Text + "\n" + request.Context.Text(ContextSectionId.Trigger);
        return (request.Interaction ?? request.Messages).LastOrDefault(m => m.Role == ModelRole.User
            && m.EvidenceKind != EvidenceMessageKind.Historical)?.Text ?? request.Context?.Text(ContextSectionId.Trigger) ?? "";
    }
}
