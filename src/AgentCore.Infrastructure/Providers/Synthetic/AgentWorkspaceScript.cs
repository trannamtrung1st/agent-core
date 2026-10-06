using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Infrastructure.Providers.Synthetic;

// Deterministic integration fixture using the ordinary model/tool loop and Artifact publication boundary.
internal static class AgentWorkspaceScript
{
    public const string Marker = "synthetic-agent-workspace:";
    public static IReadOnlyList<ModelGenerationEvent>? Generate(ModelRequest request)
    {
        var last = request.Messages.ToList().FindLastIndex(m => m.Role == ModelRole.User);
        if (last < 0 || !request.Messages[last].Text.StartsWith(Marker, StringComparison.Ordinal)) return null;
        var tools = request.Messages.Skip(last + 1).Where(m => m.Role == ModelRole.Tool).ToArray();
        var command = request.Messages[last].Text[Marker.Length..].Trim();
        var path = command == "publish" ? "/workspace/working/store-review.md" : "/home/reports/store-review.md";
        if (tools.Length == 0)
        {
            var name = command == "publish" ? ToolCatalog.ArtifactsCreateFromWorkspace : command == "search" ? ToolCatalog.WorkspaceSearch : ToolCatalog.WorkspaceRead;
            if (!request.Tools!.Any(t => t.Name == name)) return [new ModelTextDelta("Workspace capability unavailable."), new ModelCompleted(ModelStopReason.Completed)];
            var args = command == "publish" ? JsonSerializer.Serialize(new { path, displayName = "store-review.md" })
                : command == "search" ? JsonSerializer.Serialize(new { path = "/home", query = "store-review" }) : JsonSerializer.Serialize(new { path });
            return [new ModelToolCallEvent(new("agent-home-proof", name, args)), new ModelCompleted(ModelStopReason.ToolCalls)];
        }
        if (command == "publish")
        {
            using var json = JsonDocument.Parse(tools[^1].Text);
            if (json.RootElement.TryGetProperty("artifactId", out var artifact))
                return [new ModelTextDelta($"Revised report. [[artifact:{artifact.GetString()}]]"), new ModelCompleted(ModelStopReason.Completed)];
        }
        return [new ModelTextDelta("Workspace result: " + tools[^1].Text), new ModelCompleted(ModelStopReason.Completed)];
    }
}
