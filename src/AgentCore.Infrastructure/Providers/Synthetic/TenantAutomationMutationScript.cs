using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
namespace AgentCore.Infrastructure.Providers.Synthetic;

// Key-free native fixture. The same tool boundary, task verifier, IAM, consent
// and owning transaction apply; this parser supplies no execution authority.
internal static class TenantAutomationMutationScript
{
    internal static IReadOnlyList<ModelGenerationEvent>? Generate(ModelRequest request)
    {
        const string prefix = "synthetic-tenant-automation-mutation: ";
        var prompt = request.Messages.LastOrDefault(m => m.Role == ModelRole.User && m.Text.StartsWith(prefix, StringComparison.Ordinal))?.Text;
        if (prompt is null || request.Tools?.Any(t => TenantAutomationMutationContract.IsTool(t.Name)) != true) return null;
        using var input = JsonDocument.Parse(prompt[prefix.Length..]); var root = input.RootElement; var tool = root.GetProperty("tool").GetString()!;
        if (!TenantAutomationMutationContract.IsTool(tool) || request.Tools.All(t => t.Name != tool)) return Finish("The selected Automation change is unavailable for this task.");
        var args = root.GetProperty("arguments"); _ = TenantAutomationMutationContract.Parse(tool, args.GetRawText());
        var consent = request.Messages.LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization);
        if (consent is not null)
        {
            using var result = JsonDocument.Parse(consent.Text);
            return Finish(result.RootElement.TryGetProperty("applied", out var applied) && applied.ValueKind == JsonValueKind.True ? tool == ToolCatalog.AutomationRun ? "One exact Automation occurrence was admitted. Its execution result is separate." : "The exact Automation change was saved once." : "The Automation change was withheld.");
        }
        var observation = request.Messages.LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == tool);
        if (observation is null) return Call(tool, args);
        using var saved = JsonDocument.Parse(observation.Text);
        if (saved.RootElement.TryGetProperty("decision", out var decision) && decision.GetString() == "requiresApproval") return Call(ToolCatalog.RequestAuthorization, new { operation = "mutate_automation", automation = new { tool, arguments = args } });
        return Finish(saved.RootElement.TryGetProperty("applied", out var changed) && changed.ValueKind == JsonValueKind.True ? tool == ToolCatalog.AutomationRun ? "One exact Automation occurrence was admitted. Its execution result is separate." : "The exact Automation change was saved once." : "The Automation change was withheld.");
    }
    private static IReadOnlyList<ModelGenerationEvent> Call(string tool, object args) => [new ModelToolCallEvent(new("synthetic-automation-mutation-" + tool, tool, JsonSerializer.Serialize(args))), new ModelCompleted(ModelStopReason.ToolCalls)];
    private static IReadOnlyList<ModelGenerationEvent> Finish(string text) => [new ModelSemanticResponseReady(new(text, new(ModelSpeechMode.None, null), [])), new ModelCompleted(ModelStopReason.Completed)];
}
