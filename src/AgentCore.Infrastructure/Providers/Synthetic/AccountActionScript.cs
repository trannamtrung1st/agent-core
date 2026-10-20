using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Infrastructure.Providers.Synthetic;

// Offline plans exercise the ordinary offered tools, durable approval and receipt
// paths. Step arguments are intent, never server identity or authorization evidence.
internal static class AccountActionScript
{
    internal const string Marker = "synthetic account actions";
    internal static IReadOnlyList<ModelGenerationEvent>? Generate(ModelRequest request)
    {
        var prompt = SyntheticTaskContext.Text(request); var start = prompt.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0 && request.Messages.Any(m => m.Role == ModelRole.Tool && m.ToolCallId?.StartsWith("synthetic-account-action-", StringComparison.Ordinal) == true))
        {
            // Approval continuations have a new immediate task; the original plan
            // remains in the same task's saved model transcript.
            prompt = request.Messages.LastOrDefault(m => m.Role == ModelRole.User && m.Text.Contains(Marker, StringComparison.OrdinalIgnoreCase))?.Text ?? "";
            start = prompt.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        }
        if (start < 0) return null;
        IReadOnlyList<ModelGenerationEvent> Finish(string summary, bool attention = false) => request.Tools?.Any(t => t.Name == ToolCatalog.WorkComplete) == true
            ? Call("synthetic-account-actions-finish", ToolCatalog.WorkComplete, new { summary, attentionRequired = attention, outcome = attention ? "NeedsAttention" : "Response" })
            : [new ModelTextDelta(summary), new ModelCompleted(ModelStopReason.Completed)];
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(prompt[(start + Marker.Length)..].Trim()));
            using var document = JsonDocument.ParseValue(ref reader); var plan = document.RootElement.GetProperty("steps");
            if (plan.ValueKind != JsonValueKind.Array || plan.GetArrayLength() is < 1 or > 24) return Finish("Use one bounded Synthetic account-action plan.", true);
            var results = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
            string? draft = null; Guid? operationId = null; var summary = "No account action was applied.";
            for (var index = 0; index < plan.GetArrayLength(); index++)
            {
                var step = plan[index]; var tool = step.GetProperty("tool").GetString()!;
                if (!TenantAccountActionContract.IsTool(tool) && !TenantAccountReadContract.IsTool(tool)) return Finish("This account operation is not supported.", true);
                var callId = "synthetic-account-action-" + index;
                if (results.LastOrDefault(m => m.ToolCallId == callId && m.Name == ToolCatalog.RequestAuthorization) is { } saved)
                {
                    using var observed = JsonDocument.Parse(saved.Text); var root = observed.RootElement;
                    if (root.GetProperty("outcome").GetString() != "completed")
                        return Finish(root.TryGetProperty("reasonCode", out var reason) && reason.GetString() == "account_draft_already_sent"
                            ? "This immutable draft already has a confirmed send receipt. No additional message was sent."
                            : "The exact account action was not completed. See its saved decision or uncertain outcome; it will not run again.", true);
                    if (TenantAccountReadContract.IsTool(tool))
                    {
                        if (!root.TryGetProperty("accountRead", out var read) || read.ValueKind != JsonValueKind.Object) return Finish("The approved account source was not delivered under current access.", true);
                        var result = read.GetProperty("result");
                        summary = result.TryGetProperty("operation", out var inspected) && inspected.ValueKind == JsonValueKind.Object
                            ? ScriptedLanguageModel.OperationReadSummary(inspected) : "The exact connected-account source was read under current access.";
                    }
                    else
                    {
                        operationId = root.GetProperty("operationId").GetGuid();
                        summary = tool == ToolCatalog.EmailCreateDraft ? "The exact email draft was saved. No message was sent. Sending requires separate consent."
                            : tool == ToolCatalog.EmailSend ? "The provider confirmed acceptance of the exact saved email draft."
                            : "The exact Drive action completed. File reads and later actions require separate current authority.";
                        if (tool == ToolCatalog.EmailCreateDraft && root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
                            draft = result.GetProperty("DraftId").GetString();
                    }
                    continue;
                }
                var offered = request.Tools?.SingleOrDefault(t => t.Name == ToolCatalog.RequestAuthorization);
                if (offered is null || !offered.ParametersJson.Contains('"' + tool + '"', StringComparison.Ordinal)) return Finish("The requested account action is not offered for this task.", true);
                var arguments = JsonNode.Parse(step.GetProperty("arguments").GetRawText())!.AsObject();
                if (tool == ToolCatalog.EmailSend && arguments["draftId"]?.GetValue<string>() == "$draft")
                {
                    if (draft is null) return Finish("The immutable draft identity is unavailable. No send was requested.", true);
                    arguments["draftId"] = draft;
                }
                if (tool == ToolCatalog.DriveGet && arguments["operationId"]?.GetValue<string>() == "$operation")
                {
                    if (operationId is null) return Finish("The previous canonical operation identity is unavailable. No inspection was requested.", true);
                    arguments["operationId"] = operationId.Value.ToString("D");
                }
                return TenantAccountActionContract.IsTool(tool)
                    ? Call(callId, ToolCatalog.RequestAuthorization, new { operation = TenantAccountActionContract.Operation, action = new { tool, arguments } })
                    : Call(callId, ToolCatalog.RequestAuthorization, new { operation = TenantAccountReadContract.Operation, read = new { tool, arguments } });
            }
            return Finish(summary);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException)
        { return Finish("The Synthetic account-action plan or retained result is unavailable.", true); }
    }
    private static IReadOnlyList<ModelGenerationEvent> Call(string id, string name, object value) =>
        [new ModelToolCallEvent(new(id, name, JsonSerializer.Serialize(value))), new ModelCompleted(ModelStopReason.ToolCalls)];
}
