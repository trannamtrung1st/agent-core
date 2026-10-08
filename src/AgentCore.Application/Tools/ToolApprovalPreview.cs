using System.Text.Json;

namespace AgentCore.Application.Tools;

public static class ToolApprovalPreview
{
    public static (string Summary, Dictionary<string, string> Details) Build(
        string toolName,
        JsonElement args)
    {
        if (BrowserToolCatalog.TryGet(toolName, out var browser)
            && browser.Effect is ToolEffect.SensitiveWrite or ToolEffect.Destructive)
        {
            using var schema = JsonDocument.Parse(browser.ParametersJson);
            var details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Browser effect"] = toolName,
                ["Scope"] = "Current policy-allowed browser/profile; host authority remains unchanged."
            };
            foreach (var property in args.EnumerateObject())
                if (schema.RootElement.GetProperty("properties").TryGetProperty(property.Name, out _))
                    details[property.Name] = BoundDetail(property.Value.ToString());
            return (Bound($"Approve browser change: {toolName}"), details);
        }

        if (toolName is ToolCatalog.WorkspaceDelete or ToolCatalog.WorkspaceBatch)
        {
            WorkspaceStructureArguments.Parse(toolName, args);
            return (toolName == ToolCatalog.WorkspaceDelete ? "Delete workspace content" : "Restructure workspace content",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Exact operations"] = args.GetRawText(),
                    ["Path scope"] = "Relative paths use /workspace/working; explicit /home uses this managed identity.",
                    ["Failure behavior"] = "Entire preflight before mutation. Earlier changes remain if execution stops; reload before retrying."
                });
        }

        if (ToolCatalog.IsIdentityMaintenance(toolName))
        {
            var details = new Dictionary<string, string>(StringComparer.Ordinal);
            details["Applies to"] = "Future learned-state retrieval; source history is retained.";
            if (toolName == ToolCatalog.MemoryForget)
            {
                details["Memory ID"] = ReadString(args, "memoryId") ?? "";
                return ("Forget this learned-memory item", details);
            }
            var sources = toolName == ToolCatalog.MemoryConsolidate ? "sourceMemoryIds" : "sourceExperienceIds";
            if (args.TryGetProperty(sources, out var ids)) details["Sources"] = ids.ToString();
            details["Replacement"] = toolName == ToolCatalog.MemoryConsolidate
                ? (ReadString(args, "subject") ?? "") + ": " + (ReadString(args, "content") ?? "")
                : args.ToString();
            if (details["Replacement"].Length > 8000) throw new ArgumentException("Maintenance approval exceeds its content bound.");
            return (toolName == ToolCatalog.MemoryConsolidate ? "Consolidate selected learned memories" : "Consolidate selected experiences", details);
        }

        if (HarnessChatTools.Operations.TryGetValue(toolName, out var operation))
        {
            var details = new Dictionary<string, string>(StringComparer.Ordinal);
            details["Change"] = operation.Kind is "knowledge.upsert" or "instructions.update" ? ReadString(args, "content") ?? ""
                : $"{operation.Kind}: {ReadString(args, "id") ?? "operating instructions"}; enabled={ReadValue(args, "enabled")}; unsupported readability={ReadValue(args, "allowUnreadUnsupportedTypes")}";
            AgentCore.Application.Admin.DefinitionResourcePolicies.RejectSecretsInTextualContent("text/plain", System.Text.Encoding.UTF8.GetBytes(details["Change"]));
            if (details["Change"].Length > 32000) throw new ArgumentException("Harness approval exceeds its content bound.");
            details["Applies to"] = "Future conversations; this Session stays pinned.";
            details["Active version"] = ReadValue(args, "expectedVersion");
            details["Policy revision"] = ReadValue(args, "policyRevision");
            if (ReadString(args, "source") is { } source) details["Source"] = source;
            if (ReadString(args, "limitation") is { } limit) details["Limitations"] = limit;
            return (Bound($"Save {operation.Kind} for future conversations"), details);
        }

        if (string.Equals(toolName, ToolCatalog.DemoSensitiveAction, StringComparison.Ordinal))
        {
            var label = ReadString(args, "label") ?? "Sensitive action";
            var summary = Bound($"Run sensitive demo action: {label}");
            var details = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(label))
            {
                details["label"] = BoundDetail(label);
            }

            return (summary, details);
        }

        if (string.Equals(toolName, ToolCatalog.EmailSend, StringComparison.Ordinal))
        {
            var draftId = ReadString(args, "draftId") ?? "draft";
            return (Bound($"Send email draft {draftId}"), new Dictionary<string, string>(StringComparer.Ordinal));
        }

        return (Bound($"Approve tool {toolName}"), new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private static string ReadValue(JsonElement args, string name) => args.TryGetProperty(name, out var value) ? value.ToString() : "unchanged";


    public static string BoundSummary(string value) => Bound(value);

    public static string BoundDetailValue(string value) => BoundDetail(value);

    private static string? ReadString(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Bound(string value) =>
        value.Length <= ToolApprovalLimits.MaxSummaryLength
            ? value
            : value[..ToolApprovalLimits.MaxSummaryLength];

    private static string BoundDetail(string value) =>
        value.Length <= ToolApprovalLimits.MaxDetailValueLength
            ? value
            : value[..ToolApprovalLimits.MaxDetailValueLength];
}
