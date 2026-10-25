using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
namespace AgentCore.Infrastructure.Providers.Synthetic;

// Offline native qualification. Every discovery, read and consent follows the real tenant owners.
internal static class TenantManagementReadScript
{
    internal static IReadOnlyList<ModelGenerationEvent>? Generate(ModelRequest request)
    {
        const string prefix = "synthetic-tenant-management-read: ";
        var prompt = request.Messages.LastOrDefault(m => m.Role == ModelRole.User)?.Text;
        // Historical management instructions never select the script for a new user task.
        if (prompt?.StartsWith(prefix, StringComparison.Ordinal) != true) return null;
        var selected = prompt[prefix.Length..].Trim();
        var childSelection = selected.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var child = childSelection.Length > 0 && TenantManagementReadContract.IsChildTool(childSelection[0]);
        var tool = child ? childSelection[0] : selected switch { "harness" => "harness.inspect", "configuration" => "configuration.inspect", "policy" => "management.inspect", "sections" => "settings.list", "preferences" => "automation.preferences.inspect", _ => "settings.inspect" };
        var section = child || selected is "harness" or "configuration" or "policy" or "sections" or "preferences" ? null : selected;
        if (section is not null && !AgentCore.Application.Agents.InstanceSettingsResolver.Sections.Contains(section)) return Finish("Select one exact setting section, harness, configuration, policy, sections or preferences.");
        object arguments = child && tool.EndsWith(".inspect", StringComparison.Ordinal) ? new { key = childSelection.Length > 1 ? childSelection[1] : "" }
            : section is null ? new { } : new { section };
        if (child) { try { _ = TenantManagementReadContract.Parse(tool, JsonSerializer.Serialize(arguments)); } catch (ArgumentException) { return Finish("Select an exact origin-qualified child key for inspection."); } }
        var consent = request.Messages.LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization);
        if (consent is not null)
        {
            using var result = JsonDocument.Parse(consent.Text);
            var observed = result.RootElement.TryGetProperty("managementReadObservation", out var observation) && observation.ValueKind == JsonValueKind.Object;
            var authorized = result.RootElement.TryGetProperty("readAuthorized", out var allowed) && allowed.ValueKind == JsonValueKind.True;
            return Finish(observed ? "The exact saved Agent configuration was inspected. No settings were changed."
                : authorized ? "The read was authorized, but its saved observation is unavailable. No settings were changed."
                : "The Agent configuration read was not authorized. No settings were changed.");
        }
        if (!request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.CapabilitiesLoad))
            return request.Tools?.Any(t => t.Name == ToolCatalog.CapabilitiesLoad) == true ? Call(ToolCatalog.CapabilitiesLoad, new { query = tool, limit = 1 })
                : Finish("Configuration discovery is unavailable in this task.");
        if (request.Tools?.Any(t => t.Name == tool) != true) return Finish("This configuration inspection is unavailable in this task.");
        var read = request.Messages.LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == tool);
        if (read is null) return Call(tool, arguments);
        using var value = JsonDocument.Parse(read.Text);
        if (value.RootElement.TryGetProperty("decision", out var decision) && decision.GetString() == "requiresApproval")
            return Call(ToolCatalog.RequestAuthorization, new { operation = "inspect_management", read = new { tool, arguments } });
        return Finish(value.RootElement.TryGetProperty("observation", out _) ? "The exact saved Agent configuration was inspected. No settings were changed."
            : "The configuration inspection was unavailable. No settings were changed.");
    }
    private static IReadOnlyList<ModelGenerationEvent> Call(string tool, object args) =>
        [new ModelToolCallEvent(new("synthetic-management-read-" + tool, tool, JsonSerializer.Serialize(args))), new ModelCompleted(ModelStopReason.ToolCalls)];
    private static IReadOnlyList<ModelGenerationEvent> Finish(string text) => [new ModelTextDelta(text), new ModelCompleted(ModelStopReason.Completed)];
}
