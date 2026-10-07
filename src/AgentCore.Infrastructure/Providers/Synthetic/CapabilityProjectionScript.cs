using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Infrastructure.Providers.Synthetic;

internal static class CapabilityProjectionScript
{
    public static IReadOnlyList<ModelGenerationEvent>? Generate(ModelRequest request)
    {
        var last = request.Messages.ToList().FindLastIndex(m => m.Role == ModelRole.User);
        if (last < 0 || !request.Messages[last].Text.StartsWith("synthetic-capability-projection:", StringComparison.Ordinal)) return null;
        var names = request.Tools?.Select(t => t.Name).ToArray() ?? [];
        var results = request.Messages.Skip(last + 1).Where(m => m.Role == ModelRole.Tool).ToArray();
        var inspect = request.Messages[last].Text.EndsWith("inspect", StringComparison.Ordinal);
        if (inspect) return [new ModelTextDelta("Initial projected capabilities: " + string.Join(",", names)), new ModelCompleted(ModelStopReason.Completed)];
        var name = results.Length == 0 ? ToolCatalog.CapabilitiesLoad : results.Length == 1 ? ToolCatalog.WorkspaceWrite : null;
        if (name is null) return [new ModelTextDelta("Capability projection results: " + string.Join("\n", results.Select(r => r.Text))), new ModelCompleted(ModelStopReason.Completed)];
        if (!names.Contains(name)) return [new ModelTextDelta("Capability projection missing schema: " + name), new ModelCompleted(ModelStopReason.Completed)];
        object args = name == ToolCatalog.CapabilitiesLoad ? new { query = "workspace.write", limit = 1 } : new { path = "/working/loaded-capability.txt", content = "Loaded exact capability café\r\n" };
        return [new ModelToolCallEvent(new("capability-proof-" + results.Length, name, JsonSerializer.Serialize(args))), new ModelCompleted(ModelStopReason.ToolCalls)];
    }
}
