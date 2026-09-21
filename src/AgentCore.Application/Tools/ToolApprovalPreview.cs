using System.Text.Json;

namespace AgentCore.Application.Tools;

public static class ToolApprovalPreview
{
    public static (string Summary, Dictionary<string, string> Details) Build(
        string toolName,
        JsonElement args)
    {
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

        return (Bound($"Approve tool {toolName}"), new Dictionary<string, string>(StringComparer.Ordinal));
    }

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
