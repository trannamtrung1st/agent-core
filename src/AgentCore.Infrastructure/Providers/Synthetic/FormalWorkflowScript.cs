using System.Text.Json;
using System.Text.RegularExpressions;
using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.Providers.Synthetic;

/// <summary>Key-free fixture through the ordinary model/tool/claimed-Run boundary.</summary>
internal static class FormalWorkflowScript
{
    internal static IReadOnlyList<ModelGenerationEvent>? Generate(ModelRequest request)
    {
        var text = request.Messages.LastOrDefault(m => m.Role == ModelRole.User)?.Text;
        if (text?.StartsWith("synthetic-workflow-start:", StringComparison.Ordinal) != true) return null;
        var match = Regex.Match(text ?? "", @"synthetic-workflow-start:([0-9a-fA-F-]{36})");
        if (!match.Success || !Guid.TryParseExact(match.Groups[1].Value, "D", out var id)) return null;
        var tools = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
        if (tools.All(m => m.Name != "workflows.list")) return Call("list", "workflows.list", new { pageSize = 5 });
        if (tools.All(m => m.Name != "workflows.inspect")) return Call("inspect", "workflows.inspect", new { workflowId = id, version = 1, section = "overview" });
        var starts = tools.Where(m => m.Name == "workflows.start").ToArray();
        if (starts.Length < 2) return Call("start-" + starts.Length, "workflows.start", new { workflowId = id, version = 1, inputs = new { }, requestKey = "synthetic-workflow-once" });
        if (tools.All(m => m.Name != "workflows.status"))
        {
            using var receipt = JsonDocument.Parse(starts[0].Text);
            if (receipt.RootElement.TryGetProperty("executionId", out var execution)) return Call("status", "workflows.status", new { executionId = execution.GetGuid() });
        }
        return [new ModelTextDelta("Synthetic formal workflow request recorded. Verified process completion remains a separate saved fact."), new ModelCompleted(ModelStopReason.Completed)];
    }
    private static IReadOnlyList<ModelGenerationEvent> Call(string key, string name, object arguments) =>
        [new ModelToolCallEvent(new("synthetic-workflow-" + key, name, JsonSerializer.Serialize(arguments))), new ModelCompleted(ModelStopReason.ToolCalls)];
}
