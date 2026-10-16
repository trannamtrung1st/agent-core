using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Work;

public static class WorkCompletionRequest
{
    public const int MaxSummaryCharacters = 2000;
    public static readonly ModelToolDefinition Contract = new(ToolCatalog.WorkComplete,
        "Finish this bounded background AgentRun with a summary of 1 to 2000 characters. NoAction is successful and quiet. Response records completed work; NeedsAttention uses trusted-owner attention delivery. Never choose recipients or authority.",
        $$$"""{"type":"object","additionalProperties":false,"properties":{"summary":{"type":"string","description":"Concise plain-text result, at most 2000 characters. Preserve important findings and uncertainty within this bound.","minLength":1,"maxLength":{{{MaxSummaryCharacters}}}},"attentionRequired":{"type":"boolean"},"outcome":{"type":"string","enum":["NoAction","Response","NeedsAttention"]}},"required":["summary","attentionRequired","outcome"]}""");
    public static bool TryParse(JsonElement args, IReadOnlyList<ModelMessage> messages, out string result, out bool attention, out string rejection)
    {
        result = ""; attention = false; rejection = "Run completion requires exactly summary, outcome and attentionRequired.";
        if (args.ValueKind != JsonValueKind.Object || args.EnumerateObject().Count() != 3
            || !args.TryGetProperty("summary", out var summary) || summary.ValueKind != JsonValueKind.String
            || !args.TryGetProperty("outcome", out var outcome) || outcome.ValueKind != JsonValueKind.String
            || !args.TryGetProperty("attentionRequired", out var flag) || flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        if (summary.GetString() is not { Length: > 0 and <= MaxSummaryCharacters } text || string.IsNullOrWhiteSpace(text))
        { rejection = $"summary must contain 1 to {MaxSummaryCharacters} characters. Shorten the result while preserving important findings and uncertainty; do not repeat substantive tools."; return false; }
        var kind = outcome.GetString();
        if (kind is not ("NoAction" or "Response" or "NeedsAttention"))
        { rejection = "outcome must be NoAction, Response or NeedsAttention."; return false; }
        attention = flag.GetBoolean();
        if (attention != (kind == "NeedsAttention"))
        { rejection = "attentionRequired must be true only for NeedsAttention, and false for Response or NoAction."; return false; }
        if (kind == "NoAction" && messages.Where(m => m.Role == ModelRole.Tool)
            .Any(m => m.Name is { } name && ToolRegistry.TryGet(name, out var descriptor)
                && descriptor.Effect != ToolEffect.ReadOnly && !IsRejectedToolResult(m.Text)))
        { rejection = "A Run that performed an action cannot report NoAction."; return false; }
        result = JsonSerializer.Serialize(new { outcome = kind, summary = text.Trim() });
        return true;
    }
    private static bool IsRejectedToolResult(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && (
                root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(error.GetString())
                || root.TryGetProperty("saved", out var saved) && saved.ValueKind == JsonValueKind.False
                || root.TryGetProperty("changed", out var changed) && changed.ValueKind == JsonValueKind.False);
        }
        catch (JsonException) { return false; }
    }
    public static string Outcome(string text)
    { try { using var d = JsonDocument.Parse(text); return d.RootElement.GetProperty("outcome").GetString() ?? "Failed"; } catch { return "Failed"; } }
    public static string Summary(string text)
    { try { using var d = JsonDocument.Parse(text); return d.RootElement.GetProperty("summary").GetString() ?? text; } catch { return text; } }
}
