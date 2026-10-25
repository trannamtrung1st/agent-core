using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Infrastructure.Providers.Synthetic;

internal static class SessionCompletionScript
{
    internal static IReadOnlyList<ModelGenerationEvent>? Generate(ModelRequest request)
    {
        var prompt = SyntheticTaskContext.Text(request);
        if (!prompt.Contains("[test:session-complete]", StringComparison.Ordinal)) return null;
        var result = request.Messages.LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.SessionComplete);
        if (result is null && request.Tools?.Any(t => t.Name == ToolCatalog.SessionComplete) == true)
            return [new ModelToolCallEvent(new("complete-this-chat", ToolCatalog.SessionComplete,
                JsonSerializer.Serialize(new { reason = "The requested conversation is complete." }))), new ModelCompleted(ModelStopReason.ToolCalls)];
        var text = result is not null && result.Text.Contains("\"accepted\"", StringComparison.Ordinal)
            ? "The requested conversation is complete. Goodbye." : "Agent completion is not allowed for this conversation.";
        return [new ModelTextDelta(text), new ModelCompleted(ModelStopReason.Completed)];
    }
}
