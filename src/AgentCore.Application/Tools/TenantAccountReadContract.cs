using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tools;

public sealed record TenantAccountReadIntent(string ToolName, string ArgumentsJson)
{
    public bool Matches(PreparedTenantOperation operation) => operation.AccountRead is { } read && read.ToolName == ToolName && read.ArgumentsJson == ArgumentsJson;
}
public sealed record TenantAccountReadArguments(string Reference, CredentialPurpose Purpose, ConnectedAccountRead Request, string Json, Guid? OperationId = null);

public static class TenantAccountReadContract
{
    public const string Operation = "read_connected_account";
    public static readonly IReadOnlyList<string> Tools = [ToolCatalog.EmailSearch, ToolCatalog.EmailRead, ToolCatalog.DriveList, ToolCatalog.DriveSearch, ToolCatalog.DriveGet];
    public static bool IsTool(string name) => Tools.Contains(name, StringComparer.Ordinal);
    public static IReadOnlyList<string> Allowed(AgentDefinition definition) => Tools.Where(t => RolePermissions.AllowsTool(definition, t)).ToArray();
    public static TenantAccountReadArguments Parse(string tool, string arguments)
    {
        if (!IsTool(tool) || Encoding.UTF8.GetByteCount(arguments) > 8192) throw new ArgumentException("Use a supported bounded account read.");
        using var doc = JsonDocument.Parse(arguments); var root = doc.RootElement;
        var keys = tool switch {
            ToolCatalog.EmailSearch => new[] { "accountRef", "query", "limit" },
            ToolCatalog.EmailRead => ["accountRef", "messageId"],
            ToolCatalog.DriveGet => ["accountRef", "fileId", "operationId"],
            _ => ["accountRef", "folderId", "query", "cursor", "limit"] };
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(p => !keys.Contains(p.Name, StringComparer.Ordinal))
            || root.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1)) throw new ArgumentException("Use only the exact account-read arguments.");
        string? Text(string key) => !root.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null ? null
            : v.ValueKind == JsonValueKind.String ? v.GetString() : throw new ArgumentException("Use text account-read fields.");
        var reference = CredentialRules.Reference(Text("accountRef") ?? "");
        if (tool == ToolCatalog.DriveGet && root.TryGetProperty("operationId", out var operation))
        {
            if (root.TryGetProperty("fileId", out _) || operation.ValueKind != JsonValueKind.String || !Guid.TryParseExact(operation.GetString(), "D", out var id) || id == Guid.Empty)
                throw new ArgumentException("Inspect one exact operationId instead of fileId.");
            var canonical = JsonSerializer.Serialize(new { accountRef = reference, operationId = id });
            return new(reference, CredentialPurpose.GoogleDriveAccount, new(ConnectedAccountReadKind.DriveFile), canonical, id);
        }

        var purpose = tool is ToolCatalog.EmailSearch or ToolCatalog.EmailRead ? CredentialPurpose.EmailAccount : CredentialPurpose.GoogleDriveAccount;
        var kind = tool switch { ToolCatalog.EmailSearch => ConnectedAccountReadKind.EmailSearch, ToolCatalog.EmailRead => ConnectedAccountReadKind.EmailMessage,
            ToolCatalog.DriveGet => ConnectedAccountReadKind.DriveFile, _ => ConnectedAccountReadKind.DriveList };
        var limit = root.TryGetProperty("limit", out var size) && size.ValueKind != JsonValueKind.Null
            ? size.ValueKind == JsonValueKind.Number && size.TryGetInt32(out var count) ? count : throw new ArgumentException("Use an integer read limit.") : 20;
        if (tool == ToolCatalog.EmailSearch && limit > 25) throw new ArgumentException("Search at most 25 messages.");
        var read = new ConnectedAccountRead(kind, Text("query"), Text(tool == ToolCatalog.EmailRead ? "messageId" : "fileId"), Text("folderId"), Text("cursor"), limit);
        if (tool == ToolCatalog.DriveSearch && read.Query is not { Length: > 0 }) throw new ArgumentException("Use an exact Drive search query.");
        TenantConnectedAccountRules.Validate(read);
        var json = tool switch {
            ToolCatalog.EmailSearch => JsonSerializer.Serialize(new { accountRef = reference, query = read.Query ?? "", limit }),
            ToolCatalog.EmailRead => JsonSerializer.Serialize(new { accountRef = reference, messageId = read.Id }),
            ToolCatalog.DriveGet => JsonSerializer.Serialize(new { accountRef = reference, fileId = read.Id }),
            _ => JsonSerializer.Serialize(new { accountRef = reference, folderId = read.Folder, query = read.Query, cursor = read.Cursor, limit }) };
        return new(reference, purpose, read, json);
    }
    public static JsonObject Schema(IReadOnlyList<string> allowed)
    {
        var branches = new JsonArray();
        foreach (var tool in allowed.Where(IsTool).Distinct(StringComparer.Ordinal))
        {
            if (tool == ToolCatalog.DriveGet)
                branches.Add(JsonNode.Parse("""{"type":"object","required":["operation","read"],"additionalProperties":false,"properties":{"operation":{"const":"read_connected_account"},"read":{"type":"object","required":["tool","arguments"],"additionalProperties":false,"properties":{"tool":{"const":"drive.get"},"arguments":{"type":"object","required":["accountRef","operationId"],"additionalProperties":false,"properties":{"accountRef":{"type":"string","minLength":1,"maxLength":120},"operationId":{"type":"string","format":"uuid"}}}}}}}""")!);
            var properties = new JsonObject { ["accountRef"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 120 } };
            var required = new JsonArray("accountRef");
            if (tool is ToolCatalog.EmailRead or ToolCatalog.DriveGet)
            {
                var key = tool == ToolCatalog.EmailRead ? "messageId" : "fileId";
                properties[key] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = tool == ToolCatalog.EmailRead ? 512 : 160 }; required.Add(key);
            }
            else
            {
                properties["query"] = new JsonObject { ["type"] = "string", ["maxLength"] = tool == ToolCatalog.EmailSearch ? 2000 : 200 };
                if (tool == ToolCatalog.DriveSearch) { properties["query"]!["minLength"] = 1; required.Add("query"); }
                properties["limit"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = tool == ToolCatalog.EmailSearch ? 25 : 100 };
                if (tool != ToolCatalog.EmailSearch)
                {
                    properties["folderId"] = new JsonObject { ["type"] = "string", ["maxLength"] = 160 };
                    properties["cursor"] = new JsonObject { ["type"] = "string", ["maxLength"] = 2048 };
                }
            }
            branches.Add(new JsonObject { ["type"] = "object", ["required"] = new JsonArray("operation", "read"), ["additionalProperties"] = false,
                ["properties"] = new JsonObject { ["operation"] = new JsonObject { ["const"] = Operation }, ["read"] = new JsonObject {
                    ["type"] = "object", ["required"] = new JsonArray("tool", "arguments"), ["additionalProperties"] = false,
                    ["properties"] = new JsonObject { ["tool"] = new JsonObject { ["const"] = tool }, ["arguments"] = new JsonObject {
                        ["type"] = "object", ["required"] = required, ["additionalProperties"] = false, ["properties"] = properties } } } } });
        }
        return new JsonObject { ["oneOf"] = branches };
    }
    public static TenantAccountReadIntent ParseIntent(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(p => p.Name is not ("operation" or "read")) || root.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() > 1)
            || !root.TryGetProperty("read", out var read) || read.ValueKind != JsonValueKind.Object
            || read.EnumerateObject().Any(p => p.Name is not ("tool" or "arguments")) || read.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() > 1)
            || !read.TryGetProperty("tool", out var name) || name.ValueKind != JsonValueKind.String
            || !read.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Use one exact account read intent.");
        var tool = name.GetString() ?? ""; var args = Parse(tool, arguments.GetRawText());
        return new(tool, args.Json);
    }
}
