using System.Text.Json;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tools;

public static class EffectReceipts
{
    public static bool TryFromToolResult(string tool, string json, out EffectReceipt receipt)
    {
        receipt = null!;
        if (ToolCatalog.EffectOf(tool) is not (ToolEffect.Write or ToolEffect.SensitiveWrite or ToolEffect.Destructive))
        {
            return false;
        }

        if (!TryStatus(json, out var status))
        {
            return false;
        }

        var label = string.Equals(tool, ToolCatalog.BrowserClose, StringComparison.Ordinal)
            && status is "closed" or "already_closed"
            ? "Browser closed"
            : string.Equals(tool, ToolCatalog.EmailSend, StringComparison.Ordinal)
                ? "Email sent"
                : null;
        if (label is null)
        {
            return false;
        }

        receipt = new EffectReceipt(tool, status, label);
        return true;
    }

    private static bool TryStatus(string json, out string status)
    {
        status = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (document.RootElement.TryGetProperty("error", out _))
            {
                return false;
            }

            if (document.RootElement.TryGetProperty("status", out var statusEl)
                && statusEl.ValueKind == JsonValueKind.String
                && statusEl.GetString() is { Length: > 0 } value)
            {
                status = value;
                return status is "closed" or "already_closed" or "sent";
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }
}
