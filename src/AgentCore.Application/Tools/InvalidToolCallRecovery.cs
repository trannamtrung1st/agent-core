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

    internal bool Exhausted => _attempts.Values.Any(count => count >= 4);

    internal string? Refuse(ModelToolCall call)
    {
        var strategy = Strategy(call);
        if (_attempts.GetValueOrDefault(strategy) < 2) return null;
        return JsonSerializer.Serialize(new { error = "invalid_tool_strategy_blocked", message = "Two equivalent invalid attempts already failed. This strategy is blocked; use a different valid call or report the validation blocker. Do not repeat it." });
    }

    internal string Note(ModelToolCall call, string result, out bool exhausted)
    {
        exhausted = false;
        try
        {
            using var json = JsonDocument.Parse(result);
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.String || error.GetString() is not ("invalid" or "invalid_reference" or "ValidationError" or "invalid_tool_strategy_blocked")) return result;
            var strategy = Strategy(call);
            var attempt = Math.Min(ToolLimits.MaxSteps, _attempts.GetValueOrDefault(strategy) + 1);
            _attempts[strategy] = attempt;
            exhausted = attempt >= 4;
            var fields = json.RootElement.EnumerateObject().Where(p => p.Name != "invalidCallRecovery").ToDictionary(p => p.Name, p => (object)p.Value.Clone());
            if (call.Name == ToolCatalog.BrowserFind && error.GetString() != "invalid_tool_strategy_blocked" && !result.Contains(BrowserToolArguments.FindQueryGuidance, StringComparison.Ordinal))
                fields["message"] = "Invalid discovery arguments. " + BrowserToolArguments.FindQueryGuidance;
            fields["invalidCallRecovery"] = new
            {
                strategy, attempt,
                instruction = attempt == 1 ? "Correct the reported validation error using the offered schema."
                    : attempt == 2 ? "Second equivalent failure. Rebuild a minimal call from the schema; omit unused fields. The next equivalent invalid attempt will be blocked."
                    : "Repeated invalid strategy blocked. Report this validation failure accurately, or make a different valid attempt."
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
            var compact = JsonSerializer.Serialize(new
            {
                error = root.GetProperty("error").GetString(),
                invalidCallRecovery = new { strategy = recovery.GetProperty("strategy").GetString(), attempt = recovery.GetProperty("attempt").GetInt32() },
                message = call.Name == ToolCatalog.BrowserFind && recovery.GetProperty("attempt").GetInt32() < 3
                    ? "Use by/value; omit unused fields." : "Correct arguments or report the blocked strategy."
            });
            return Encoding.UTF8.GetByteCount(compact) <= budget ? compact : null;
        }
        catch (JsonException) { return null; }
    }

    private static string Strategy(ModelToolCall call)
    {
        string data;
        try
        {
            using var json = JsonDocument.Parse(call.ArgumentsJson);
            // Overloaded find calls differing only in guessed labels are the same malformed strategy.
            // Ref mistakes and other tools retain values so a corrected value remains permitted.
            if (json.RootElement.ValueKind != JsonValueKind.Object) data = "non_object:" + json.RootElement.ValueKind;
            else if (call.Name == ToolCatalog.BrowserFind && json.RootElement.ValueKind == JsonValueKind.Object
                && !BrowserToolArguments.TryRequest(Guid.Empty, call.Name, json.RootElement, out _, out var error) && error == "invalid")
                data = string.Join("|", json.RootElement.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => p.Name + ":" + p.Value.ValueKind));
            else data = Canonical(json.RootElement);
        }
        catch (JsonException) { data = "malformed_json"; }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(call.Name + "\n" + data)));
    }

    private static string Canonical(JsonElement value) => value.ValueKind == JsonValueKind.Object
        ? "{" + string.Join(",", value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Name) + ":" + Canonical(p.Value))) + "}"
        : value.ValueKind == JsonValueKind.Array ? "[" + string.Join(",", value.EnumerateArray().Select(Canonical)) + "]" : value.GetRawText();
}
