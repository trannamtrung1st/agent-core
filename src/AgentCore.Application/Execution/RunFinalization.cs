using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Execution;

internal static class RunFinalization
{
    internal const string BrowserLifecycleInstruction = "Native dialog_pending is a blocked decision, not browser unavailability. Inspect with authorized browser.dialog, resolve only when justified by the requested action, then observe the application before proceeding. Do not repeat the triggering click. A closed browser profile can retain authentication: sign-out requires independent application evidence. Report cleanup failures or uncertainty truthfully.";
    internal const string CleanupMarker = "Core browser cleanup phase.";
    internal const string CleanupInstruction = "The remaining browser work budget is reserved for requested cleanup and verification. Stop starting new substantive work. Resolve only dialogs justified by the action the user requested; never automatically accept an unknown confirmation. Verify sign-out with an application observation independently of browser closure. Close only if requested, then give a truthful final or partial result before the deadline.";
    internal static bool CleanupTool(string name) => name is ToolCatalog.BrowserFind or ToolCatalog.BrowserSnapshot or ToolCatalog.BrowserDialog or ToolCatalog.BrowserClick or ToolCatalog.BrowserClose or ToolCatalog.BrowserTabs;
    internal const string Marker = "Core finalization phase: ";
    internal const string Instruction = "The work phase has ended. Generate a final reply only from existing durable tool receipts. No tools or new external effects are permitted. Distinguish recorded actions from unverified task outcomes. A browser closure does not establish sign-out. Report incomplete or uncertain work accurately.";
    internal static string? Restore(IEnumerable<ModelMessage> messages) => messages
        .Where(m => m.Role == ModelRole.System && m.Text.StartsWith(Marker, StringComparison.Ordinal))
        .Select(m => m.Text[Marker.Length..]).FirstOrDefault(r => r is "runDeadline" or "providerTimeout" or "providerCancelled");
    internal static bool HasEvidence(IEnumerable<ModelMessage> messages) => messages.Any(m =>
    {
        if (m.Role != ModelRole.Tool) return false;
        try
        {
            using var doc = JsonDocument.Parse(m.Text);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object && (!root.TryGetProperty("error", out var error) || error.ValueKind == JsonValueKind.Null)
                && (root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String && status.GetString() is "ok" or "closed" or "already_closed"
                    || root.TryGetProperty("outcome", out var outcome) && outcome.ValueKind == JsonValueKind.String && outcome.GetString() == "sent");
        }
        catch (JsonException) { return false; }
    });
    internal static string? RecoveryReason(ProviderFailure failure) => failure.Code switch
    {
        ProviderErrorCode.Timeout => "providerTimeout",
        ProviderErrorCode.Cancelled => "providerCancelled",
        _ => null
    };
    internal static ProviderFailure DeadlineFailure() => new(ProviderErrorCode.Timeout, "Run deadline reached.", FailureReason: "runDeadline");
}
