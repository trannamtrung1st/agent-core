using System.Text.Json;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Email;

// Inspect only Core's top-level receipt classification, never untrusted mailbox content.
public static class EmailAttentionSafety
{
    public static bool RequiresOwnerAttention(string tool, string receipt)
    {
        if (tool is not (ToolCatalog.EmailAccounts or ToolCatalog.EmailRequestAccount or ToolCatalog.EmailSearch
            or ToolCatalog.EmailRead or ToolCatalog.EmailCreateDraft or ToolCatalog.EmailSend)) return false;
        try
        {
            using var json = JsonDocument.Parse(receipt); var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (tool == ToolCatalog.EmailAccounts && root.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String && status.GetString() == "no_eligible_account") return true;
            if (!root.TryGetProperty("error", out var error)) return false;
            string? code; JsonElement message;
            if (error.ValueKind == JsonValueKind.String) { code = error.GetString(); root.TryGetProperty("message", out message); }
            else if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var node) && node.ValueKind == JsonValueKind.String)
            { code = node.GetString(); error.TryGetProperty("message", out message); }
            else return false;
            if (code is "needs_attention" or "email_oauth_reconnect_required" or "email_oauth_not_configured" or "email_oauth_scope_missing"
                or "email_account_disabled" or "credential_unavailable" or "email_provider_unavailable" or "email_oauth_temporary_failure"
                or "smtp_disabled" or "email_send_disabled") return true;
            return message.ValueKind == JsonValueKind.String && message.GetString() is { } text
                && (text.StartsWith("no_eligible_account:", StringComparison.Ordinal) || text.StartsWith("mail_authority_revoked:", StringComparison.Ordinal));
        }
        catch (JsonException) { return false; }
    }
}
