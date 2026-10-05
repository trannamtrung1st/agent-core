using System.Text.Json;

namespace AgentCore.Application.Tools;

public static class ToolApprovalPreview
{
    public static (string Summary, Dictionary<string, string> Details) Build(
        string toolName,
        JsonElement args)
    {
        if (HarnessChatTools.Operations.TryGetValue(toolName, out var operation))
        {
            var details = new Dictionary<string, string>(StringComparer.Ordinal);
            details["Change"] = operation.Kind is "knowledge.upsert" or "instructions.update" ? ReadString(args, "content") ?? ""
                : operation.Kind == "skill.upsert" && args.TryGetProperty("skill", out var skill) ? FormatSkill(skill)
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
    private static string FormatSkill(JsonElement skill) => string.Join("\n", new[] { "id", "name", "description", "procedure", "activationKeywords", "requiredCapabilities", "resourcePaths" }
        .Where(key => skill.TryGetProperty(key, out _)).Select(key => $"{key}: {skill.GetProperty(key)}"));

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
