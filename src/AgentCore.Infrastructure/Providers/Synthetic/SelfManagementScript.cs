using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Infrastructure.Providers.Synthetic;

/// <summary>Offline acceptance journeys exercise real discovery, approvals, owner services and fresh Run admission.</summary>
internal static class SelfManagementScript
{
    public static IReadOnlyList<ModelGenerationEvent>? Generate(ModelRequest request)
    {
        var user = request.Messages.LastOrDefault(m => m.Role == ModelRole.User)?.Text ?? "";
        if (user == "List my workspace after enabling workspace listing." || user.StartsWith("Continue verified configuration task:", StringComparison.Ordinal))
            return ConfigurationTask(request, user);
        if (user is not ("Inspect my configuration." or "Always answer briefly." or "Turn off user memory retrieval." or "Turn on user memory retrieval." or "Turn off session memory." or "Freeze self-management." or "Verify my saved configuration.")) return null;
        var results = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
        bool Offered(string name) => request.Tools?.Any(t => t.Name == name) == true;
        var freeze = user == "Freeze self-management.";
        var name = freeze ? "management.freeze" : "settings.patch";
        if (results.LastOrDefault(m => m.Name == name) is { } changed)
        {
            using var json = JsonDocument.Parse(changed.Text);
            return Answer(json.RootElement.TryGetProperty("outcome", out var outcome) && outcome.GetString() is "applied" or "unchanged" or "frozen"
                ? freeze ? "Self-management frozen. Normal tasks remain available." : "Saved for my next Run. The current Run remains unchanged."
                : "Nothing was saved. Inspect the current revision and policy before retrying.");
        }
        if (!Offered("configuration.inspect"))
            return Offered(ToolCatalog.CapabilitiesLoad) && !results.Any(m => m.Name == ToolCatalog.CapabilitiesLoad)
                ? Call(ToolCatalog.CapabilitiesLoad, new { query = "configuration.inspect settings.patch management.freeze management.inspect", limit = 4 })
                : Answer("Self-management is unavailable under the current policy. Use Admin to enable it.");
        var inspection = results.LastOrDefault(m => m.Name == "configuration.inspect");
        if (inspection is null) return Call("configuration.inspect", new { });
        using var state = JsonDocument.Parse(inspection.Text);
        if (!state.RootElement.TryGetProperty("instanceRevision", out var revision)) return Answer("Configuration inspection failed. Nothing was saved.");
        if (user is "Inspect my configuration." or "Verify my saved configuration.")
        {
            var conversation = state.RootElement.GetProperty("settings").EnumerateArray().FirstOrDefault(s => s.GetProperty("section").GetString() == "conversationPolicy");
            return Answer(conversation.ValueKind == JsonValueKind.Undefined ? "No Settings management area is granted." : "Current response length: " + conversation.GetProperty("effective").GetProperty("responseLength").GetString() + ". Configuration applies to this fresh Run.");
        }
        if (!Offered(name)) return Answer("This change is outside the admitted authority. Nothing was saved.");
        if (freeze) return Call(name, new { expectedInstanceRevision = revision.GetInt64() });
        return user == "Always answer briefly."
            ? Call(name, new { expectedInstanceRevision = revision.GetInt64(), section = "conversationPolicy", set = new { responseLength = "concise" }, clear = Array.Empty<string>() })
            : user == "Turn off session memory."
                ? Call(name, new { expectedInstanceRevision = revision.GetInt64(), section = "memoryPolicy", set = new { sessionMemory = false }, clear = Array.Empty<string>() })
                : Call(name, new { expectedInstanceRevision = revision.GetInt64(), section = "memoryPolicy", set = new { userRetrieval = user == "Turn on user memory retrieval." }, clear = Array.Empty<string>() });
    }
    private static IReadOnlyList<ModelGenerationEvent> ConfigurationTask(ModelRequest request, string user)
    {
        var results = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
        bool Offered(string name) => request.Tools?.Any(t => t.Name == name) == true;
        var continuing = user.StartsWith("Continue verified configuration task:", StringComparison.Ordinal);
        var target = ToolCatalog.WorkspaceList;
        if (continuing)
        {
            if (results.LastOrDefault(m => m.Name == target) is { } observed)
            {
                using var result = JsonDocument.Parse(observed.Text);
                return Answer(result.RootElement.TryGetProperty("error", out _) ? "Workspace inspection failed in the fresh Run." : "Workspace inspected in a fresh Run.");
            }
            return Offered(target) ? Call(target, new { path = "/home" })
                : Call(ToolCatalog.CapabilitiesLoad, new { query = target });
        }
        if (results.LastOrDefault(m => m.Name == "configuration.propose") is { } changed)
        {
            using var receipt = JsonDocument.Parse(changed.Text);
            return Answer(receipt.RootElement.TryGetProperty("outcome", out var outcome) && outcome.GetString() == "applied"
                ? "Configuration saved. Core will continue the pending task in a fresh Run."
                : "Nothing was saved. The pending task remains blocked.");
        }
        if (!Offered("configuration.inspect") || !Offered("configuration.propose"))
            return results.Any(m => m.Name == ToolCatalog.CapabilitiesLoad) ? Answer("Self-management is unavailable.")
                : Call(ToolCatalog.CapabilitiesLoad, new { query = "configuration.inspect configuration.propose", limit = 2 });
        if (results.LastOrDefault(m => m.Name == "configuration.inspect") is not { } inspected)
            return Call("configuration.inspect", new { goal = user, target });
        using var state = JsonDocument.Parse(inspected.Text);
        var option = state.RootElement.GetProperty("remediationOptions").EnumerateArray().FirstOrDefault();
        return option.ValueKind == JsonValueKind.Undefined ? Answer("No safe configuration remedy is available.")
            : Call("configuration.propose", new { option = option.GetProperty("option").GetString(), target, goal = user,
                expectedInstanceRevision = state.RootElement.GetProperty("instanceRevision").GetInt64(),
                section = option.GetProperty("section").GetString(), set = option.GetProperty("set"), clear = Array.Empty<string>() });
    }
    private static IReadOnlyList<ModelGenerationEvent> Call(string name, object args) => [new ModelToolCallEvent(new("self-" + name, name, JsonSerializer.Serialize(args))), new ModelCompleted(ModelStopReason.ToolCalls)];
    private static IReadOnlyList<ModelGenerationEvent> Answer(string text) => [new ModelSemanticResponseReady(new(text, new(ModelSpeechMode.Same, null), [])), new ModelCompleted(ModelStopReason.Completed)];
}
