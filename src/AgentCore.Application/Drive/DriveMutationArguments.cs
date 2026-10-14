using System.Text;
using System.Text.Json;
using AgentCore.Application.Sessions;

namespace AgentCore.Application.Drive;

public sealed record DriveTrashTarget(string FileId, string ExpectedVersion);

public static class DriveMutationArguments
{
    public const int MaxBatchFiles = 20;
    public static bool RequiresApproval(string tool) => tool is "drive.trash" or "drive.trash_batch" or "drive.rename" or "drive.move";
    public static string Text(JsonElement args, string key)
        => args.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw AgentCoreErrors.Validation(key + " is required.");
    public static long Epoch(JsonElement args)
        => args.TryGetProperty("authEpoch", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var epoch) && epoch > 0
            ? epoch : throw AgentCoreErrors.Validation("authEpoch must be a current positive epoch.");
    public static string Version(string value) => value is { Length: > 0 and <= 64 } && !value.Any(char.IsControl)
        ? value : throw AgentCoreErrors.Validation("expectedVersion must be bounded current metadata.");
    public static void Validate(string tool, JsonElement args)
    {
        using var schema = JsonDocument.Parse(DriveToolContracts.Descriptor(tool).ModelDefinition.ParametersJson);
        if (args.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(args.GetRawText()) > AgentCore.Domain.Conversation.AgentRunLimits.MaxDriveBatchPreparedActionBytes)
            throw AgentCoreErrors.Validation("Drive arguments exceed their bounded object contract.");
        var properties = schema.RootElement.GetProperty("properties");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in args.EnumerateObject())
        {
            if (!seen.Add(property.Name) || !properties.TryGetProperty(property.Name, out var rule))
                throw AgentCoreErrors.Validation("Drive arguments must match the closed schema.");
            var type = rule.GetProperty("type").GetString();
            if (type == "string")
            {
                if (property.Value.ValueKind != JsonValueKind.String) throw AgentCoreErrors.Validation("Drive argument type is invalid.");
                var text = property.Value.GetString()!;
                if (rule.TryGetProperty("maxLength", out var max) && text.Length > max.GetInt32()
                    || rule.TryGetProperty("minLength", out var min) && text.Length < min.GetInt32())
                    throw AgentCoreErrors.Validation("Drive argument length is invalid.");
            }
            else if (type == "integer" && (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt64(out _))
                || type == "array" && property.Value.ValueKind != JsonValueKind.Array)
                throw AgentCoreErrors.Validation("Drive argument type is invalid.");
        }
        foreach (var required in schema.RootElement.GetProperty("required").EnumerateArray())
            if (!args.TryGetProperty(required.GetString()!, out _)) throw AgentCoreErrors.Validation(required.GetString() + " is required.");
    }
    public static IReadOnlyList<DriveTrashTarget> Manifest(JsonElement args)
    {
        var files = args.GetProperty("files");
        if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() is < 1 or > MaxBatchFiles)
            throw AgentCoreErrors.Validation("Choose 1–20 distinct app-authorized files with exact versions.");
        var result = new List<DriveTrashTarget>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object || file.EnumerateObject().Count() != 2
                || file.EnumerateObject().Any(p => p.Name is not ("fileId" or "expectedVersion")))
                throw AgentCoreErrors.Validation("Each file requires only fileId and expectedVersion.");
            var id = DriveService.Id(Text(file, "fileId"));
            if (!ids.Add(id)) throw AgentCoreErrors.Validation("Batch file IDs must be distinct.");
            result.Add(new(id, Version(Text(file, "expectedVersion"))));
        }
        return result;
    }
}
