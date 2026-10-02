using System.Text.Json;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tools;

public static class EffectReceipts
{
    public static bool TryFromToolResult(string tool, string json, out EffectReceipt receipt)
    {
        receipt = null!;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (string.Equals(tool, ToolCatalog.BrowserClose, StringComparison.Ordinal)
                && TryString(document.RootElement, "status", out var status)
                && status is "closed" or "already_closed")
            {
                receipt = new EffectReceipt(tool, status, "Browser closed");
                return true;
            }

            if (string.Equals(tool, ToolCatalog.EmailSend, StringComparison.Ordinal)
                && TryString(document.RootElement, "outcome", out var outcome)
                && string.Equals(outcome, "sent", StringComparison.Ordinal))
            {
                receipt = new EffectReceipt(tool, outcome, "Email sent");
                return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static bool TryString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        return root.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && property.GetString() is { Length: > 0 } text
            && (value = text) is not null;
    }
}
