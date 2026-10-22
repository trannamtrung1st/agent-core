using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
namespace AgentCore.Infrastructure.Providers.Synthetic;

// Key-free native fixture; all reads and consent use the canonical tenant owners.
internal static class TenantAutomationReadScript
{
    internal static IReadOnlyList<ModelGenerationEvent>? Generate(ModelRequest request)
    {
        const string prefix = "synthetic-tenant-automation-read: ";
        var prompt = request.Messages.LastOrDefault(m => m.Role == ModelRole.User && m.Text.StartsWith(prefix, StringComparison.Ordinal))?.Text;
        if (prompt is null) return null;
        var target = prompt[prefix.Length..].Trim(); var list = target == "list";
        if (!list && (!Guid.TryParseExact(target, "D", out var id) || id == Guid.Empty)) return Finish("Select one exact Automation or list.");
        var tool = list ? ToolCatalog.AutomationList : ToolCatalog.AutomationInspect;
        object arguments = list ? new { } : new { automationId = Guid.Parse(target) };
        if (request.Tools?.Any(t => t.Name == tool) != true) return Finish("Automation inspection is unavailable for this task.");
        var consent = request.Messages.LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization);
        if (consent is not null)
        {
            using var receipt = JsonDocument.Parse(consent.Text);
            if (receipt.RootElement.TryGetProperty("automationReadObservation", out var saved) && saved.ValueKind == JsonValueKind.Object)
                return Finish("The exact Automation metadata was inspected. No Automation was changed.");
            return Finish("Automation inspection was not authorized. No Automation was changed.");
        }
        var observation = request.Messages.LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == tool);
        if (observation is null) return Call(tool, arguments);
        using var value = JsonDocument.Parse(observation.Text);
        if (value.RootElement.TryGetProperty("decision", out var decision) && decision.GetString() == "requiresApproval")
            return Call(ToolCatalog.RequestAuthorization, new { operation = "inspect_automations", read = new { tool, arguments } });
        return Finish(value.RootElement.TryGetProperty("observation", out _) ? "The exact Automation metadata was inspected. No Automation was changed."
            : "Automation inspection was unavailable. No Automation was changed.");
    }
    private static IReadOnlyList<ModelGenerationEvent> Call(string tool, object args) =>
        [new ModelToolCallEvent(new("synthetic-automation-read-" + tool, tool, JsonSerializer.Serialize(args))), new ModelCompleted(ModelStopReason.ToolCalls)];
    private static IReadOnlyList<ModelGenerationEvent> Finish(string text) => [new ModelTextDelta(text), new ModelCompleted(ModelStopReason.Completed)];
}
