using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Agents;
using AgentCore.Application.Drive;
using AgentCore.Application.Security;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public sealed record TenantAccountActionIntent(string ToolName, string ArgumentsJson)
{
    public bool Matches(Domain.Security.PreparedTenantOperation operation) => operation.AccountAction is { } action && action.ToolName == ToolName && action.ArgumentsJson == ArgumentsJson;
}
public sealed record TenantAccountActionArguments(string ToolName, string Reference, CredentialPurpose Purpose, string Permission,
    string Json, string? Path = null, string? DraftId = null)
{
    public bool Upload => ToolName == ToolCatalog.DriveUpload;
    public bool Download => ToolName == ToolCatalog.DriveDownload;
}

// Model arguments express intent only. Account/source witnesses, exact draft content,
// generated remote IDs and effect reservation all come from the server owners.
public static class TenantAccountActionContract
{
    public const string Operation = "use_connected_account";
    public const int MaxArgumentBytes = 48 * 1024;
    public static IReadOnlyList<string> Tools { get; } = [ToolCatalog.EmailCreateDraft, ToolCatalog.EmailSend, ToolCatalog.DriveCreateFolder,
        ToolCatalog.DriveUpload, ToolCatalog.DriveDownload, ToolCatalog.DriveRename, ToolCatalog.DriveMove, ToolCatalog.DriveTrash, ToolCatalog.DriveTrashBatch];
    public static bool IsTool(string tool) => Tools.Contains(tool, StringComparer.Ordinal);
    public static IReadOnlyList<string> Allowed(AgentDefinition definition) => Tools.Where(t => RolePermissions.AllowsTool(definition, t)).ToArray();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static TenantAccountActionArguments Parse(Guid session, string tool, string arguments)
    {
        try { return ParseCore(session, tool, arguments); }
        catch (Sessions.AgentCoreException) { throw new ArgumentException("Use exact bounded account action arguments."); }
    }
    private static TenantAccountActionArguments ParseCore(Guid session, string tool, string arguments)
    {
        if (session == Guid.Empty || !IsTool(tool) || Encoding.UTF8.GetByteCount(arguments) > MaxArgumentBytes)
            throw new ArgumentException("Use one supported bounded account action.");
        using var doc = JsonDocument.Parse(arguments); var root = doc.RootElement;
        var keys = tool switch {
            ToolCatalog.EmailCreateDraft => new[] { "accountRef", "to", "cc", "bcc", "subject", "body" },
            ToolCatalog.EmailSend => ["accountRef", "draftId"],
            ToolCatalog.DriveCreateFolder => ["accountRef", "authEpoch", "name", "parentId"],
            ToolCatalog.DriveUpload => ["accountRef", "authEpoch", "path", "name", "folderId"],
            ToolCatalog.DriveDownload => ["accountRef", "fileId", "expectedVersion", "path"],
            ToolCatalog.DriveRename => ["accountRef", "authEpoch", "fileId", "expectedVersion", "name"],
            ToolCatalog.DriveMove => ["accountRef", "authEpoch", "fileId", "expectedVersion", "sourceParentId", "destinationParentId"],
            ToolCatalog.DriveTrashBatch => ["accountRef", "authEpoch", "files"],
            _ => ["accountRef", "authEpoch", "fileId", "expectedVersion"] };
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(p => !keys.Contains(p.Name, StringComparer.Ordinal))
            || root.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1)) throw new ArgumentException("Use only exact account-action fields.");
        string? Optional(string key) => !root.TryGetProperty(key, out var v) ? null : v.ValueKind == JsonValueKind.String
            ? v.GetString() : throw new ArgumentException("Account-action text fields cannot be null or another type.");
        string Required(string key) => Optional(key) ?? throw new ArgumentException(key + " is required.");
        var reference = CredentialRules.Reference(Required("accountRef"));
        string Path()
        {
            var path = Required("path");
            if (path.Length > 512 || !path.StartsWith("/home/", StringComparison.Ordinal) && !path.StartsWith("/private/", StringComparison.Ordinal) && !path.StartsWith("/working/", StringComparison.Ordinal)
                || path.IndexOfAny(['*', '?', '[', ']']) >= 0) throw new ArgumentException("Use an exact owned /home, /private or /working file path.");
            try { TenantWorkspaceRules.ValidatePath(path, file: true); }
            catch (Sessions.AgentCoreException) { throw new ArgumentException("Use an exact owned workspace child path."); }
            return path;
        }
        long Epoch() => root.TryGetProperty("authEpoch", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var epoch) && epoch > 0
            ? epoch : throw new ArgumentException("Use the current positive account authEpoch.");
        string Id(string key, string? fallback = null) => DriveService.Id(Optional(key) ?? fallback ?? throw new ArgumentException(key + " is required."));
        string Version() => DriveMutationArguments.Version(Required("expectedVersion"));
        string[] Addresses(string key, bool required = false)
        {
            if (!root.TryGetProperty(key, out var value)) return required ? throw new ArgumentException(key + " is required.") : [];
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > EmailToolLimits.MaxRecipientsPerField
                || required && value.GetArrayLength() == 0) throw new ArgumentException("Use bounded concrete recipients.");
            var result = new List<string>();
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || !EmailHeaderSafety.TryValidateAddress(item.GetString()!, out var address))
                    throw new ArgumentException("Use valid recipient addresses without header injection.");
                result.Add(address);
            }
            return result.ToArray();
        }
        object canonical; string? path = null, draftId = null;
        if (tool == ToolCatalog.EmailCreateDraft)
        {
            var to = Addresses("to", true); var cc = Addresses("cc"); var bcc = Addresses("bcc");
            var subject = Required("subject"); var body = Required("body");
            if (subject.Length > EmailToolLimits.MaxSubjectLength || body.Length > EmailToolLimits.MaxBodyLength
                || !EmailHeaderSafety.IsSafeSubject(subject) || !EmailHeaderSafety.IsSafeBody(body)) throw new ArgumentException("Draft content exceeds safe bounds.");
            var draft = EmailDraftNormalizer.Normalize(new("intent", to, cc, bcc, subject, body));
            canonical = new { accountRef = reference, to = draft.To, cc = draft.Cc, bcc = draft.Bcc, subject = draft.Subject, body = draft.Body };
        }
        else if (tool == ToolCatalog.EmailSend)
        {
            draftId = Required("draftId");
            if (draftId.Length is < 1 or > 200 || draftId.Any(char.IsControl)) throw new ArgumentException("Use one bounded immutable draft identity.");
            canonical = new { accountRef = reference, draftId };
        }
        else if (tool == ToolCatalog.DriveCreateFolder) canonical = new { accountRef = reference, authEpoch = Epoch(), name = DriveService.Name(Required("name")), parentId = Id("parentId", "root") };
        else if (tool == ToolCatalog.DriveUpload)
        {
            path = Path(); canonical = new { accountRef = reference, authEpoch = Epoch(), path, name = DriveService.Name(Optional("name") ?? System.IO.Path.GetFileName(path)), folderId = Id("folderId", "root") };
        }
        else if (tool == ToolCatalog.DriveDownload)
        {
            path = Path(); canonical = new { accountRef = reference, fileId = Id("fileId"), expectedVersion = Version(), path };
        }
        else if (tool == ToolCatalog.DriveRename) canonical = new { accountRef = reference, authEpoch = Epoch(), fileId = Id("fileId"), expectedVersion = Version(), name = DriveService.Name(Required("name")) };
        else if (tool == ToolCatalog.DriveMove)
        {
            var source = Id("sourceParentId"); var destination = Id("destinationParentId");
            if (destination == "root" || source == destination) throw new ArgumentException("Move requires distinct concrete app-authorized folders.");
            canonical = new { accountRef = reference, authEpoch = Epoch(), fileId = Id("fileId"), expectedVersion = Version(), sourceParentId = source, destinationParentId = destination };
        }
        else if (tool == ToolCatalog.DriveTrashBatch) canonical = new { accountRef = reference, authEpoch = Epoch(), files = DriveMutationArguments.Manifest(root) };
        else canonical = new { accountRef = reference, authEpoch = Epoch(), fileId = Id("fileId"), expectedVersion = Version() };
        var json = JsonSerializer.Serialize(canonical, Json);
        if (Encoding.UTF8.GetByteCount(json) > MaxArgumentBytes) throw new ArgumentException("Canonical account action exceeds bounds.");
        return new(tool, reference, tool is ToolCatalog.EmailCreateDraft or ToolCatalog.EmailSend ? CredentialPurpose.EmailAccount : CredentialPurpose.GoogleDriveAccount,
            tool switch { ToolCatalog.EmailCreateDraft => "email.draft", ToolCatalog.EmailSend => "email.send", ToolCatalog.DriveTrash or ToolCatalog.DriveTrashBatch => "drive.delete",
                ToolCatalog.DriveDownload => "workspaces.write", _ => "drive.write" }, json, path, draftId);
    }
    public static TenantAccountActionIntent ParseIntent(Guid session, JsonElement root)
    {
        TenantApprovalToolContract.Require(root, ["operation", "action"]);
        if (root.GetProperty("operation").ValueKind != JsonValueKind.String || root.GetProperty("operation").GetString() != Operation)
            throw new ArgumentException("Use the exact account action operation.");
        var action = root.GetProperty("action");
        TenantApprovalToolContract.Require(action, ["tool", "arguments"]);
        if (action.GetProperty("tool").ValueKind != JsonValueKind.String || action.GetProperty("arguments").ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Use one exact account action.");
        var tool = action.GetProperty("tool").GetString()!; return new(tool, Parse(session, tool, action.GetProperty("arguments").GetRawText()).Json);
    }
    public static JsonObject Schema(IReadOnlyList<string> allowed)
    {
        var branches = new JsonArray();
        foreach (var tool in allowed.Where(IsTool).Distinct(StringComparer.Ordinal))
        {
            var schema = JsonNode.Parse((tool.StartsWith("drive.", StringComparison.Ordinal) ? DriveToolContracts.Descriptor(tool) : ToolRegistry.Get(tool)).ModelDefinition.ParametersJson)!.AsObject();
            schema["additionalProperties"] = false;
            var required = schema["required"] as JsonArray;
            if (required is null) { required = new JsonArray(); schema["required"] = required; }
            if (!required.Any(v => v?.GetValue<string>() == "accountRef")) required.Add("accountRef");
            schema["properties"]!["accountRef"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 120 };
            branches.Add(new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("operation", "action"),
                ["properties"] = new JsonObject { ["operation"] = new JsonObject { ["const"] = Operation }, ["action"] = new JsonObject {
                    ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("tool", "arguments"),
                    ["properties"] = new JsonObject { ["tool"] = new JsonObject { ["const"] = tool }, ["arguments"] = schema } } } });
        }
        return new JsonObject { ["oneOf"] = branches };
    }
}
