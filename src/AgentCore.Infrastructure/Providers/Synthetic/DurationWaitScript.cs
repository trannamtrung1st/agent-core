using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Infrastructure.Providers.Synthetic;

internal static class DurationWaitScript
{
    internal static IReadOnlyList<ModelGenerationEvent>? Generate(ModelRequest request)
    {
        var prompt = request.Messages.LastOrDefault(m => m.Role == ModelRole.User)?.Text ?? "";
        if (!prompt.Contains("[test:execution-wait]", StringComparison.Ordinal)) return null;
        if (request.Messages.Any(m => m.Role == ModelRole.Tool && m.ToolCallId == "duration-wait"))
            return [new ModelTextDelta("The requested duration elapsed on the same Run."), new ModelCompleted(ModelStopReason.Completed)];
        return [new ModelToolCallEvent(new("duration-wait", ToolCatalog.ExecutionWait, JsonSerializer.Serialize(new { mode = "duration", seconds = 12 }))), new ModelCompleted(ModelStopReason.ToolCalls)];
    }
}
