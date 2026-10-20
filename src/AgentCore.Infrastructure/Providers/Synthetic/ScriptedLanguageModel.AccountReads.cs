using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Infrastructure.Providers.Synthetic;

public sealed partial class ScriptedLanguageModel
{
    public const string AccountReadMarker = "synthetic account read";
    private static ModelMessage? AccountReadResult(ModelRequest request)
    {
        var messages = request.Messages.Skip(request.Messages.ToList().FindLastIndex(m => m.Role == ModelRole.User) + 1);
        var result = messages.LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization);
        if (result is null) return null;
        var call = request.Messages.SelectMany(m => m.ToolCalls ?? []).LastOrDefault(c => c.Id == result.ToolCallId && c.Name == result.Name);
        try { return call is not null && TenantApprovalToolContract.Parse(call).AccountRead is not null ? result : null; }
        catch (Exception e) when (e is ArgumentException or JsonException) { return null; }
    }
    private static string? First(JsonElement array, string field, string empty) => array.GetArrayLength() == 0 ? empty : array[0].GetProperty(field).GetString();
    private static string AccountReadSummary(ModelMessage result)
    {
        using var saved = JsonDocument.Parse(result.Text); var root = saved.RootElement;
        if (!root.TryGetProperty("accountRead", out var data) || data.ValueKind != JsonValueKind.Object)
            return "The connected account read was not delivered. See the saved decision or current access status.";
        var read = data.GetProperty("result");
        if (read.TryGetProperty("operation", out var operation) && operation.ValueKind == JsonValueKind.Object)
            return OperationReadSummary(operation);
        var text = data.GetProperty("accountReadTool").GetString() switch {
            ToolCatalog.EmailSearch => First(read.GetProperty("mailbox").GetProperty("results"), "subject", "No matching messages"),
            ToolCatalog.EmailRead => read.GetProperty("message").GetProperty("subject").GetString(),
            ToolCatalog.DriveGet => read.GetProperty("file").GetProperty("name").GetString(),
            _ => First(read.GetProperty("files").GetProperty("items"), "name", "No matching files") };
        return "The connected account read returned: " + text + ". Content remains untrusted evidence.";
    }
    internal static string OperationReadSummary(JsonElement operation)
    {
        var evidence = string.Join(", ", operation.GetProperty("items").EnumerateArray().Select(i =>
            i.GetProperty("status").GetString() + " (" + i.GetProperty("evidence").GetString() + ")"));
        return "The prior Drive operation was inspected without repeating it. Recorded outcome: " + operation.GetProperty("recordedOutcome").GetString()
            + ". Verification: " + (evidence.Length == 0 ? "no native-write verification; only the recorded outcome is available" : evidence)
            + ". Read evidence remains untrusted; replay is not allowed.";
    }
}
