using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCore.Application.Office;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Tools;

// Office content remains plain data. The shared mutation owner derives authority and identity.
public static class TenantOfficeMutationContract
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private sealed record DocumentCreate(string Path, string? Title, IReadOnlyList<OfficeSection> Sections);
    private sealed record WorkbookCreate(string Path, IReadOnlyList<OfficeSheet> Sheets);
    private sealed record DocumentEdit(string Path, IReadOnlyList<OfficeDocumentEdit> Operations);
    private sealed record WorkbookEdit(string Path, IReadOnlyList<OfficeSpreadsheetEdit> Operations);
    public static bool IsMutation(string tool) => tool is ToolCatalog.OfficeDocumentCreate or ToolCatalog.OfficeDocumentEdit
        or ToolCatalog.OfficeSpreadsheetCreate or ToolCatalog.OfficeSpreadsheetEdit;
    public static bool IsEdit(string tool) => tool is ToolCatalog.OfficeDocumentEdit or ToolCatalog.OfficeSpreadsheetEdit;
    public static TenantWorkspaceMutationArguments Parse(string tool, JsonElement root)
    {
        if (!IsMutation(tool)) throw new ArgumentException("Use an exact Office mutation tool.");
        using var schema = JsonDocument.Parse(OfficeToolContracts.Descriptor(tool).ModelDefinition.ParametersJson);
        Validate(root, schema.RootElement);
        var path = root.GetProperty("path").GetString()!;
        if (path.IndexOfAny(['*', '?', '[', ']']) >= 0) throw new ArgumentException("Use one concrete Office file path.");
        // Reuse the closed owned-path and matching-extension checks of inspection.
        var reader = tool.StartsWith("office.document.", StringComparison.Ordinal) ? ToolCatalog.OfficeDocumentInspect : ToolCatalog.OfficeSpreadsheetInspect;
        using var target = JsonDocument.Parse(JsonSerializer.Serialize(new { path }));
        _ = TenantOfficeReadContract.Parse(reader, target.RootElement);
        object value = tool switch {
            ToolCatalog.OfficeDocumentCreate => root.Deserialize<DocumentCreate>(Json)!,
            ToolCatalog.OfficeSpreadsheetCreate => root.Deserialize<WorkbookCreate>(Json)!,
            ToolCatalog.OfficeDocumentEdit => root.Deserialize<DocumentEdit>(Json)!,
            _ => root.Deserialize<WorkbookEdit>(Json)!
        };
        var canonical = JsonSerializer.Serialize(value, Json);
        return new(tool, path, null, [], [], canonical, TenantWorkspaceMutationContract.Hash(tool + "\n" + canonical));
    }
    // Applies only the simple closed schema vocabulary used by the incumbent Office descriptors.
    private static void Validate(JsonElement value, JsonElement schema)
    {
        var type = schema.GetProperty("type").GetString();
        var matches = type switch {
            "object" => value.ValueKind == JsonValueKind.Object, "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String, "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number),
            _ => false };
        if (!matches) throw new ArgumentException("Use exact typed Office arguments.");
        if (schema.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(c => c.GetString() == value.GetString()))
            throw new ArgumentException("Use a supported Office value.");
        if (type == "object")
        {
            var fields = value.EnumerateObject().ToArray(); var properties = schema.GetProperty("properties");
            if (fields.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length)
                throw new ArgumentException("Duplicate Office fields are forbidden.");
            foreach (var field in fields)
            {
                if (!properties.TryGetProperty(field.Name, out var child)) throw new ArgumentException("Use closed Office arguments.");
                Validate(field.Value, child);
            }
            if (schema.TryGetProperty("required", out var required) && required.EnumerateArray().Any(r => !value.TryGetProperty(r.GetString()!, out _)))
                throw new ArgumentException("Required Office arguments are missing.");
        }
        else if (type == "array")
        {
            var count = value.GetArrayLength();
            if (schema.TryGetProperty("minItems", out var min) && count < min.GetInt32()
                || schema.TryGetProperty("maxItems", out var max) && count > max.GetInt32()) throw new ArgumentException("Use bounded Office collections.");
            foreach (var item in value.EnumerateArray()) Validate(item, schema.GetProperty("items"));
        }
        else if (type == "string")
        {
            var count = value.GetString()!.Length;
            if (schema.TryGetProperty("minLength", out var min) && count < min.GetInt32()
                || schema.TryGetProperty("maxLength", out var max) && count > max.GetInt32()) throw new ArgumentException("Use bounded Office text.");
        }
    }
    public static OfficeProcessedFile Process(IOfficeProcessor office, TenantWorkspaceMutationArguments args, ReadOnlyMemory<byte> source, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(args.CanonicalArguments); var root = json.RootElement;
        return args.Tool switch {
            ToolCatalog.OfficeDocumentCreate => CreateDocument(root.Deserialize<DocumentCreate>(Json)!),
            ToolCatalog.OfficeSpreadsheetCreate => office.CreateWorkbook(new(root.Deserialize<WorkbookCreate>(Json)!.Sheets), ct),
            ToolCatalog.OfficeDocumentEdit => office.EditDocument(source, root.Deserialize<DocumentEdit>(Json)!.Operations, ct),
            ToolCatalog.OfficeSpreadsheetEdit => office.EditWorkbook(source, root.Deserialize<WorkbookEdit>(Json)!.Operations, ct),
            _ => throw new ArgumentException("Use an exact Office mutation tool.")
        };
        OfficeProcessedFile CreateDocument(DocumentCreate input) => office.CreateDocument(new(input.Title, input.Sections.Select(s => s with { Paragraphs = s.Paragraphs ?? [] }).ToArray()), ct);
    }
    public static ModelToolDefinition ForModel(ModelToolDefinition tool) => tool with { Description = tool.Description
        + " Tenant calls use one exact absolute owned path and at most 64 KiB UTF-8 arguments, as the sole call in a batch. Create never replaces an occupied file; edit requires current independent source-read permission. Current IAM, capability and HomeAccess apply again at commit. If authorization is required, request mutate_workspace through approvals.request_authorization with mutation.tool and these exact mutation.arguments. A write grant never grants source reads. Core retains one body-free effect receipt; never repeat an uncertain or refused operation. No Artifact publication or recipient download authority is granted." };
}
