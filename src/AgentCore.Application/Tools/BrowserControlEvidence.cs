using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;

namespace AgentCore.Application.Tools;

// Reconstruct control facts only from the exact call and Core's settled consent receipt.
// Page text and provider data remain untrusted and never confer execution authority.
internal static class BrowserControlEvidence
{
    internal static IEnumerable<(ModelToolCall Call, string Json)> Read(IEnumerable<ModelMessage> messages)
    {
        var calls = new Dictionary<string, ModelToolCall>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            foreach (var call in message.ToolCalls ?? []) calls[call.Id] = call;
            if (message.Role != ModelRole.Tool || message.ToolCallId is not { } id || !calls.TryGetValue(id, out var source) || source.Name != message.Name) continue;
            if (ToolCatalog.IsBrowserTool(source.Name)) { yield return (source, message.Text); continue; }
            if (TryRead(source, message.Text, out var command, out var json)) yield return (command, json);
        }
    }

    internal static bool TryRead(ModelToolCall source, string text, out ModelToolCall command, out string json)
    {
        command = null!; json = "";
        if (!TryCommand(source, out var browser)) return false;
        try
        {
            var receipt = JsonNode.Parse(text) as JsonObject;
            if (receipt?["approvalStatus"]?.GetValue<string>() != "approved" || receipt["outcome"]?.GetValue<string>() != "completed"
                || receipt["commandCompleted"]?.GetValue<bool>() != true || receipt["replayAllowed"]?.GetValue<bool>() != false
                || receipt["browser"] is not JsonObject native) return false;
            var observed = native["observation"] as JsonObject;
            var result = (JsonObject)(observed ?? native["data"] as JsonObject ?? new JsonObject()).DeepClone();
            foreach (var key in new[] { "error", "status", "effectAttempted", "effectConfirmedBySdk", "applicationOutcomeVerified" })
                if (native[key] is { } value) result[key] = value.DeepClone();
            if (observed?["intervention"] is { } intervention
                && intervention.Deserialize<BrowserInterventionKind>() is not BrowserInterventionKind.None)
                result["error"] = "user_intervention_required";
            command = browser; json = result.ToJsonString(); return true;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException) { return false; }
    }

    internal static bool TryCommand(ModelToolCall source, out ModelToolCall command)
    {
        command = null!;
        if (source.Name != ToolCatalog.RequestAuthorization) return false;
        try
        {
            if (TenantApprovalToolContract.Parse(source).Browser is not { } browser) return false;
            command = source with { Name = browser.Tool, ArgumentsJson = browser.ArgumentsJson }; return true;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException) { return false; }
    }

    internal static ModelToolDefinition? RestrictAuthorization(ModelToolDefinition tool, Func<string, bool> allowed)
    {
        if (tool.Name != ToolCatalog.RequestAuthorization) return tool;
        var schema = JsonNode.Parse(tool.ParametersJson)!;
        if (schema["oneOf"] is not JsonArray branches) return tool;
        foreach (var branch in branches.ToArray())
        {
            if (branch?["properties"]?["operation"]?["enum"]?.AsArray().Any(v => v?.GetValue<string>() == "run_browser_command") != true) continue;
            var commands = branch["properties"]!["command"]!["oneOf"]!.AsArray();
            foreach (var command in commands.ToArray())
                if (command?["properties"]?["tool"]?["enum"]?[0]?.GetValue<string>() is not { } name || !allowed(name)) commands.Remove(command);
            if (commands.Count == 0) branches.Remove(branch);
        }
        return branches.Count == 0 ? null : tool with { ParametersJson = schema.ToJsonString() };
    }
}
