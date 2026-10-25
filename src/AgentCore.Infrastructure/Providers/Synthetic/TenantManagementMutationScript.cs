using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
namespace AgentCore.Infrastructure.Providers.Synthetic;
internal static class TenantManagementMutationScript
{
    internal static IReadOnlyList<ModelGenerationEvent>? Generate(ModelRequest request)
    {
        const string prefix = "synthetic-tenant-management-mutation: ";
        var prompt = request.Messages.LastOrDefault(m => m.Role == ModelRole.User)?.Text;
        // Historical management instructions never select the script for a new user task.
        if (prompt?.StartsWith(prefix, StringComparison.Ordinal) != true) return null;
        using var command = JsonDocument.Parse(prompt[prefix.Length..]);
        var tool = command.RootElement.GetProperty("tool").GetString()!; var arguments = command.RootElement.GetProperty("arguments");
        var consent = request.Messages.LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization);
        if (consent is not null)
        {
            using var value = JsonDocument.Parse(consent.Text);
            return Finish(value.RootElement.TryGetProperty("applied", out var applied) && applied.ValueKind == JsonValueKind.True
                ? "The exact Agent configuration change was recorded. Current Run settings stayed pinned."
                : "The Agent configuration change was withheld. No settings were changed by this request.");
        }
        if (!request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.CapabilitiesLoad))
            return request.Tools?.Any(t => t.Name == ToolCatalog.CapabilitiesLoad) == true ? Call(ToolCatalog.CapabilitiesLoad, new { query = tool, limit = 1 }) : Finish("Configuration discovery is unavailable.");
        if (request.Tools?.Any(t => t.Name == tool) != true) return Finish("This configuration command is unavailable in this task.");
        var result = request.Messages.LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == tool);
        if (result is null) return Call(tool, arguments);
        using var output = JsonDocument.Parse(result.Text);
        if (output.RootElement.TryGetProperty("decision", out var decision) && decision.GetString() == "requiresApproval")
            return Call(ToolCatalog.RequestAuthorization, new { operation = "mutate_management", change = new { tool, arguments } });
        return Finish(output.RootElement.TryGetProperty("applied", out var done) && done.ValueKind == JsonValueKind.True
            ? "The exact Agent configuration change was recorded. Current Run settings stayed pinned." : "The configuration command was unavailable. No settings were changed.");
    }
    private static IReadOnlyList<ModelGenerationEvent> Call(string tool, object arguments) =>
        [new ModelToolCallEvent(new("synthetic-management-mutation-" + tool, tool, JsonSerializer.Serialize(arguments))), new ModelCompleted(ModelStopReason.ToolCalls)];
    private static IReadOnlyList<ModelGenerationEvent> Finish(string message) => [new ModelTextDelta(message), new ModelCompleted(ModelStopReason.Completed)];
}
