using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public sealed record TenantAttachmentReadArguments(Guid AttachmentId, string? Sheet = null, string? Range = null,
    string? Cursor = null, int? Offset = null, int? Limit = null);

public static class TenantAttachmentReadContract
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public static bool Allows(AgentDefinition definition) => RolePermissions.AllowsTool(definition, ToolCatalog.AttachmentsRead);
    public static TenantAttachmentReadArguments Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > 4096) throw new ArgumentException("Use bounded exact attachment arguments.");
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("Use attachment arguments.");
        var fields = root.EnumerateObject().ToArray();
        if (fields.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length
            || fields.Any(f => f.Name is not ("attachmentId" or "sheet" or "range" or "cursor" or "offset" or "limit"))) throw new ArgumentException("Use closed attachment arguments.");
        string? Text(string key, int max) => !root.TryGetProperty(key, out var value) ? null
            : value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text && text.Length <= max ? text : throw new ArgumentException("Use bounded exact attachment parameters.");
        int? Number(string key, int min, int max) => !root.TryGetProperty(key, out var value) ? null
            : value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= min && number <= max ? number : throw new ArgumentException("Use bounded attachment pagination.");
        if (!Guid.TryParseExact(Text("attachmentId", 36), "D", out var id) || id == Guid.Empty) throw new ArgumentException("Use one exact attachment ID.");
        var range = Text("range", 32); var cursor = Text("cursor", 2048);
        if (range is not null && cursor is not null) throw new ArgumentException("A sparse cursor cannot be combined with an explicit range.");
        return new(id, Text("sheet", 31), range, cursor, Number("offset", 0, int.MaxValue), Number("limit", 1, 100));
    }
    public static string Canonical(TenantAttachmentReadArguments args) => JsonSerializer.Serialize(args, Json);
    public static void Window(TenantAttachmentReadArguments args, string contentType)
    {
        var type = AttachmentMedia.NormalizeContentType(contentType);
        if (type == OfficeLimits.DocumentMime ? args.Sheet is not null || args.Range is not null || args.Cursor is not null
            : type == OfficeLimits.SpreadsheetMime ? args.Offset is not null || args.Limit is not null
            : args.Sheet is not null || args.Range is not null || args.Cursor is not null || args.Offset is not null || args.Limit is not null)
            throw new ArgumentException("Use offset/limit only for DOCX and sheet/range/cursor only for XLSX.");
    }
    public static ModelToolDefinition ForModel(ModelToolDefinition tool) => tool with {
        Description = tool.Description + " Read one actual bound Attachment in this Session under independent current Agent Session/Attachment permissions, admitted/current tool capability and operation policy. No workspace copy or recipient download permission is granted. attachment_read_review_required means no content was read; exact read Review is not yet available on this tenant tool path. Do not repeat a refused read or switch identifiers to evade it."
    };
}
