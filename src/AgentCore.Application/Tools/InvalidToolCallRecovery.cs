using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Tools;

// Per-Run worker state. Core-generated receipts persist the counters in existing checkpoints.
internal sealed class InvalidToolCallRecovery
{
    private readonly Dictionary<string, int> _attempts = new(StringComparer.Ordinal);
    internal InvalidToolCallRecovery(IEnumerable<ModelMessage> receipts)
    {
        foreach (var receipt in receipts.Where(m => m.Role == ModelRole.Tool))
        {
            try
            {
                using var json = JsonDocument.Parse(receipt.Text);
                if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("invalidCallRecovery", out var recovery)
                    && recovery.ValueKind == JsonValueKind.Object && recovery.TryGetProperty("strategy", out var key) && key.ValueKind == JsonValueKind.String && key.GetString() is { Length: 64 } strategy
                    && recovery.TryGetProperty("attempt", out var attempt) && attempt.ValueKind == JsonValueKind.Number && attempt.TryGetInt32(out var count) && count is >= 1 and <= ToolLimits.MaxSteps)
                    _attempts[strategy] = Math.Max(_attempts.GetValueOrDefault(strategy), count);
            }
            catch (JsonException) { }
        }
    }

    internal bool HasBlockedStrategies => _attempts.Values.Any(count => count >= 2);

    internal string? Refuse(ModelToolCall call)
    {
        var strategy = Strategy(call);
        if (_attempts.GetValueOrDefault(strategy) < 2 && _attempts.GetValueOrDefault(Strategy(call, discoveryGoalOnly: true)) < 2) return null;
        const string message = "Two equivalent attempts made no progress. This strategy is blocked; call an offered tool, use a different concrete goal or valid call, or report the blocker. Do not repeat it.";
        return call.Name == ToolCatalog.CapabilitiesLoad
            ? CapabilityLoadResult.Failure("invalid_tool_strategy_blocked", "load_strategy_blocked", message)
            : JsonSerializer.Serialize(new { error = "invalid_tool_strategy_blocked", message });
    }

    internal string Note(ModelToolCall call, string result, out bool exhausted)
    {
        exhausted = false;
        try
        {
            using var json = JsonDocument.Parse(result);
            if (json.RootElement.ValueKind != JsonValueKind.Object) return result;
            var invalid = json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                && error.GetString() is "invalid" or "invalid_target" or "invalid_frame" or "ValidationError" or "invalid_tool_strategy_blocked";
            JsonElement outcome = default;
            var ineffectiveDiscovery = call.Name == ToolCatalog.CapabilitiesLoad && json.RootElement.TryGetProperty("outcome", out outcome)
                && outcome.ValueKind == JsonValueKind.String && outcome.GetString() is "load_no_match" or "load_unavailable" or "load_already_projected" or "load_over_budget";
            if (!invalid && !ineffectiveDiscovery) return result;
            var strategy = Strategy(call, discoveryGoalOnly: ineffectiveDiscovery && outcome.GetString() != "load_already_projected");
            if (invalid && error.GetString() == "invalid_tool_strategy_blocked" && _attempts.GetValueOrDefault(strategy) < 2
                && _attempts.GetValueOrDefault(Strategy(call, discoveryGoalOnly: true)) >= 2)
                strategy = Strategy(call, discoveryGoalOnly: true);
            var attempt = Math.Min(ToolLimits.MaxSteps, _attempts.GetValueOrDefault(strategy) + 1);
            _attempts[strategy] = attempt;
            exhausted = attempt >= 4;
            var fields = json.RootElement.EnumerateObject().Where(p => p.Name != "invalidCallRecovery").ToDictionary(p => p.Name, p => (object)p.Value.Clone());
            if (call.Name == ToolCatalog.BrowserFind && error.GetString() != "invalid_tool_strategy_blocked" && !result.Contains(BrowserToolArguments.TargetGuidance, StringComparison.Ordinal))
                fields["message"] = "Invalid discovery arguments. " + BrowserToolArguments.TargetGuidance;
            fields["invalidCallRecovery"] = new
            {
                strategy, attempt,
                instruction = ineffectiveDiscovery ? "Follow nextStep: call offered tools or change the goal. Repeating this query cannot add a capability." : attempt == 1 ? "Correct the reported validation error using the offered schema."
                    : attempt == 2 ? "Second equivalent failure. Rebuild a minimal call from the schema; omit unused fields. The next equivalent invalid attempt will be blocked."
                    : "Repeated strategy blocked. Report this blocker accurately, or make a different valid attempt."
            };
            return JsonSerializer.Serialize(fields);
        }
        catch (JsonException) { return result; }
    }

    internal static string? FitReceipt(ModelToolCall call, string result, int budget)
    {
        try
        {
            using var json = JsonDocument.Parse(result);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("invalidCallRecovery", out var recovery)) return null;
            var fields = new Dictionary<string, object>
            {
                ["invalidCallRecovery"] = new { strategy = recovery.GetProperty("strategy").GetString(), attempt = recovery.GetProperty("attempt").GetInt32() }
            };
            if (root.TryGetProperty("error", out var error)) fields["error"] = error.Clone();
            if (root.TryGetProperty("outcome", out var outcome)) fields["outcome"] = outcome.Clone();
            if (call.Name == ToolCatalog.CapabilitiesLoad)
            {
                fields["truncated"] = true;
                fields["nextStep"] = "Use offered tools, a different goal, or report the blocker.";
            }
            else fields["message"] = call.Name == ToolCatalog.BrowserFind && recovery.GetProperty("attempt").GetInt32() < 3
                ? "Use target={by,value}; omit unused fields." : "Correct arguments or report the blocked strategy.";
            var compact = JsonSerializer.Serialize(fields);
            return Encoding.UTF8.GetByteCount(compact) <= budget ? compact : null;
        }
        catch (JsonException) { return null; }
    }

    private static string Strategy(ModelToolCall call, bool discoveryGoalOnly = false)
    {
        string data;
        try
        {
            using var json = JsonDocument.Parse(call.ArgumentsJson);
            // Overloaded find calls differing only in guessed labels are the same malformed strategy.
            // Native lookup failures retain values so a corrected accessible name remains permitted.
            if (json.RootElement.ValueKind != JsonValueKind.Object) data = "non_object:" + json.RootElement.ValueKind;
            else if (call.Name == ToolCatalog.BrowserSnapshot
                && !BrowserToolArguments.TryRequest(Guid.Empty, call.Name, json.RootElement, out _, out var frameError) && frameError == "invalid_frame")
                data = "frame_validation";
            else if (call.Name.StartsWith("browser.", StringComparison.Ordinal)
                && BrowserToolArguments.InvalidTargetReason(json.RootElement) is { } reason)
                data = "target_validation:" + reason;
            else if (call.Name == ToolCatalog.BrowserFind && json.RootElement.ValueKind == JsonValueKind.Object
                && !BrowserToolArguments.TryRequest(Guid.Empty, call.Name, json.RootElement, out _, out var error) && error == "invalid")
                data = string.Join("|", json.RootElement.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => p.Name + ":" + p.Value.ValueKind));
            else if (call.Name == ToolCatalog.CapabilitiesLoad && json.RootElement.TryGetProperty("query", out var query) && query.ValueKind == JsonValueKind.String
                && query.GetString() is { Length: > 0 and <= 200 } text && !string.IsNullOrWhiteSpace(text) && !text.Contains('*')
                && json.RootElement.EnumerateObject().Select(p => p.Name).Distinct().Count() == json.RootElement.EnumerateObject().Count()
                && json.RootElement.EnumerateObject().All(p => p.Name is "query" or "limit")
                && (!json.RootElement.TryGetProperty("limit", out var limit) || limit.ValueKind == JsonValueKind.Number && limit.TryGetInt32(out var count) && count is >= 1 and <= 8))
                data = System.Text.RegularExpressions.Regex.Replace(query.GetString()!.Trim().ToLowerInvariant(), @"\s+", " ")
                    + (discoveryGoalOnly ? "" : "\nlimit:" + (json.RootElement.TryGetProperty("limit", out var matchLimit) ? matchLimit.GetInt32() : 4));
            else data = Canonical(json.RootElement);
        }
        catch (JsonException) { data = "malformed_json"; }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(call.Name + "\n" + data)));
    }

    private static string Canonical(JsonElement value) => value.ValueKind == JsonValueKind.Object
        ? "{" + string.Join(",", value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonical(p.Value))) + "}"
        : value.ValueKind == JsonValueKind.Array ? "[" + string.Join(",", value.EnumerateArray().Select(Canonical)) + "]" : value.GetRawText();
}
