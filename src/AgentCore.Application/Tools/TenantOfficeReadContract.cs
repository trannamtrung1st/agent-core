using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Tools;

public static class TenantOfficeReadContract
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public static TenantWorkspaceReadArguments Parse(string tool, JsonElement root)
    {
        var document = tool == ToolCatalog.OfficeDocumentInspect;
        var fields = root.EnumerateObject().ToArray();
        string[] allowed = document ? ["path", "offset", "limit"] : ["path", "sheet", "range", "cursor"];
        if (fields.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length || fields.Any(p => !allowed.Contains(p.Name, StringComparer.Ordinal)))
            throw new ArgumentException("Use closed Office inspection arguments.");
        string? Text(string key, int max) => !root.TryGetProperty(key, out var value) ? null : value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text && text.Length <= max
            ? text : throw new ArgumentException("Use bounded exact Office inspection parameters.");
        var path = Text("path", 512) ?? throw new ArgumentException("path is required.");
        if (TenantWorkspaceReadContract.IsResourcePath(path)) Security.TenantResourceSourceTarget.ValidatePath(path);
        if ((!path.StartsWith("/home/", StringComparison.Ordinal) && !path.StartsWith("/working/", StringComparison.Ordinal) && !TenantWorkspaceReadContract.IsResourcePath(path))
            || !path.EndsWith(document ? ".docx" : ".xlsx", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Use an exact owned home/working Office file path.");
        int Number(string key, int fallback, int min, int max) => !root.TryGetProperty(key, out var value) ? fallback
            : value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count) && count >= min && count <= max ? count : throw new ArgumentException("Use bounded Office pagination.");
        if (document) return new(path, Limit: Number("limit", 20, 1, 100), Offset: Number("offset", 0, 0, int.MaxValue));
        var range = Text("range", 32); var cursor = Text("cursor", 2048);
        if (range is not null && cursor is not null) throw new ArgumentException("A sparse cursor cannot be combined with an explicit range.");
        return new(path, Cursor: cursor, Sheet: Text("sheet", 31), Range: range);
    }
    public static string Canonical(string tool, TenantWorkspaceReadArguments args) => tool == ToolCatalog.OfficeDocumentInspect
        ? JsonSerializer.Serialize(new { path = args.Path, offset = args.Offset, limit = args.Limit })
        : JsonSerializer.Serialize(new { path = args.Path, sheet = args.Sheet, range = args.Range, cursor = args.Cursor }, Json);
    public static ModelToolDefinition ForModel(ModelToolDefinition tool) => tool with {
        Description = tool.Description + " Use one exact absolute owned /home or /working file path, or an admitted read-only /agent/resources/... or /agent/instance/resources/... file. Current independent Agent read permissions and policy apply, with no file changes, publication or recipient download authority. If workspace_read_review_required or resource_read_review_required is returned, request inspect_workspace through approvals.request_authorization with read.tool and these exact read.arguments. Consent binds this saved source and pagination only. Do not repeat a refused request."
    };
}
