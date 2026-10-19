using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public sealed record TenantWorkspaceMutationArguments(string Tool, string? Path, string? Content, IReadOnlyList<WorkspaceTextEdit> Edits,
    IReadOnlyList<WorkspaceStructuralOperation> Operations, string CanonicalArguments, string ParametersHash)
{
    public IEnumerable<(string Path, string Permission)> Targets => Tool switch {
        ToolCatalog.WorkspaceWrite => [(Path!, "workspaces.write")],
        ToolCatalog.WorkspacePatch => [(Path!, "workspaces.read"), (Path!, "workspaces.write")],
        _ => Operations.SelectMany(o => o.Op switch {
            "mkdir" => new[] { (o.Path!, "workspaces.write") }, "delete" => [(o.Path!, "workspaces.delete")],
            "copy" => [(o.Source!, "workspaces.read"), (o.Destination!, "workspaces.write")],
            _ => [(o.Source!, "workspaces.read"), (o.Source!, "workspaces.delete"), (o.Destination!, "workspaces.write")] }) };
}
public static class TenantWorkspaceMutationContract
{
    public static bool IsMutation(string tool) => tool is ToolCatalog.WorkspaceWrite or ToolCatalog.WorkspacePatch or ToolCatalog.WorkspaceMkdir
        or ToolCatalog.WorkspaceCopy or ToolCatalog.WorkspaceMove or ToolCatalog.WorkspaceDelete or ToolCatalog.WorkspaceBatch;
    public static bool Allows(AgentDefinition definition, string tool) => IsMutation(tool) && RolePermissions.AllowsTool(definition, tool);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string ActionHash(Guid session, string parameters) => Hash("workspace.mutation\n" + session.ToString("D") + "\n" + parameters);
    public static TenantWorkspaceMutationArguments Parse(Guid session, string tool, string json)
    {
        try { return ParseCore(session, tool, json); }
        catch (AgentCoreException) { throw new ArgumentException("Use exact bounded tenant workspace mutation arguments."); }
    }
    private static TenantWorkspaceMutationArguments ParseCore(Guid session, string tool, string json)
    {
        if (session == Guid.Empty || !IsMutation(tool) || string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > 65536) throw new ArgumentException("Use bounded workspace mutation arguments.");
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        string Read(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new ArgumentException("Use exact workspace fields.");
        string Path(string value, bool concrete = false)
        {
            if (string.IsNullOrEmpty(value) || Encoding.UTF8.GetByteCount(value) > 4095 || value != "/home" && !value.StartsWith("/home/", StringComparison.Ordinal)
                && value != "/working" && !value.StartsWith("/working/", StringComparison.Ordinal)) throw new ArgumentException("Use an exact absolute workspace child path.");
            var owner = new TenantWorkspaceReference(Guid.Empty, Guid.Empty, value.StartsWith("/working/", StringComparison.Ordinal) || value == "/working" ? session : null);
            TenantWorkspaceRules.Path(owner, value, file: true);
            if (concrete && value.IndexOfAny(['*', '?', '[', ']']) >= 0) throw new ArgumentException("Use concrete structural paths.");
            return value;
        }
        string? path = null, content = null; WorkspaceTextEdit[] edits = []; WorkspaceStructuralOperation[] operations = []; object canonical;
        if (tool == ToolCatalog.WorkspaceWrite)
        {
            TenantApprovalToolContract.Require(root, ["path", "content"]); path = Path(Read("path")); content = Read("content");
            if (Encoding.UTF8.GetByteCount(content) > 32768) throw new ArgumentException("Use at most 32 KiB UTF-8 content.");
            canonical = new { path, content };
        }
        else if (tool == ToolCatalog.WorkspacePatch)
        {
            TenantApprovalToolContract.Require(root, ["path", "edits"]); path = Path(Read("path"));
            var array = root.GetProperty("edits");
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > 32) throw new ArgumentException("Use 1–32 exact edits.");
            edits = array.EnumerateArray().Select(e => {
                TenantApprovalToolContract.Require(e, ["oldText", "newText"]);
                if (e.GetProperty("oldText").ValueKind != JsonValueKind.String || e.GetProperty("newText").ValueKind != JsonValueKind.String
                    || e.GetProperty("oldText").GetString() is not { Length: > 0 } old) throw new ArgumentException("Use exact nonempty oldText and literal newText.");
                return new WorkspaceTextEdit(old, e.GetProperty("newText").GetString()!);
            }).ToArray();
            canonical = new { path, edits };
        }
        else
        {
            operations = WorkspaceStructureArguments.Parse(tool, root).Select(o => o with { Path = o.Path is null ? null : Path(o.Path, true),
                Source = o.Source is null ? null : Path(o.Source, true), Destination = o.Destination is null ? null : Path(o.Destination, true) }).ToArray();
            if (JsonSerializer.SerializeToUtf8Bytes(operations).Length > WorkspaceStructureLimits.MaxRequestBytes) throw new ArgumentException("Use a bounded structural batch.");
            var paths = operations.SelectMany(o => new[] { o.Path, o.Source, o.Destination }).OfType<string>().ToArray();
            if (tool != ToolCatalog.WorkspaceCopy && paths.Select(p => p.StartsWith("/home/", StringComparison.Ordinal)).Distinct().Count() > 1)
                throw new ArgumentException("Only an individual copy may cross home/working roots.");
            var o = operations[0];
            canonical = tool == ToolCatalog.WorkspaceBatch ? new { operations } : o.Op switch {
                "mkdir" => (object)new { path = o.Path }, "delete" => new { path = o.Path, recursive = o.Recursive },
                _ => new { source = o.Source, destination = o.Destination } };
        }
        var text = JsonSerializer.Serialize(canonical, Json);
        if (Encoding.UTF8.GetByteCount(text) > 65536) throw new ArgumentException("Use bounded canonical workspace arguments.");
        return new(tool, path, content, edits, operations, text, Hash(tool + "\n" + text));
    }
    public static ModelToolDefinition ForModel(ModelToolDefinition tool) => tool with {
        Description = (tool.Name switch {
            ToolCatalog.WorkspaceWrite => "Write literal UTF-8 text, replacing present content with ordinary concurrent-writer semantics.",
            ToolCatalog.WorkspacePatch => "Apply 1–32 literal UTF-8 edits. Each oldText must match current source exactly once; overlapping ambiguity fails.",
            ToolCatalog.WorkspaceMkdir => "Create a directory and parents; an existing directory succeeds unchanged.",
            ToolCatalog.WorkspaceCopy => "Copy a file or tree, preserving empty folders and link identity, within or across owned home/working roots. Never overwrite or merge; failures may leave a partial tree.",
            ToolCatalog.WorkspaceMove => "Move a file or tree within one owned root. Never overwrite; cross-root moves are forbidden.",
            ToolCatalog.WorkspaceDelete => "Delete one concrete file or empty directory; a nonempty directory requires recursive:true. Roots are protected.",
            _ => "Execute 1–16 ordered mkdir/copy/move/delete operations (16 KiB canonical structural payload) in one root. Syntax/scopes are validated before mutation. Stop on failure; earlier or partial effects remain. No atomic rollback."
        }) + " Tenant execution requires exact absolute /home or /working child paths, current independent Agent IAM and policy. Use as the sole call in a batch. Core persists dispatch and a body-free receipt; an uncertain result must never be replayed. Policy-required or missing mutation authority returns no dispatch. This call supplies no approval authority. Writes accept at most 32 KiB UTF-8 content and 64 KiB arguments. A receipt grants no read or recipient download authority.",
        ParametersJson = ClosedSchema(tool)
    };
    private static string ClosedSchema(ModelToolDefinition tool)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(tool.ParametersJson)!;
        void Close(System.Text.Json.Nodes.JsonNode? node)
        {
            if (node is System.Text.Json.Nodes.JsonObject obj)
            {
                if (obj["type"]?.GetValue<string>() == "object") obj["additionalProperties"] = false;
                if (obj["type"]?.GetValue<string>() == "array") { obj["minItems"] = 1; obj["maxItems"] ??= 32; }
                foreach (var pair in obj.ToArray()) Close(pair.Value);
            }
            else if (node is System.Text.Json.Nodes.JsonArray array) foreach (var child in array) Close(child);
        }
        Close(root); return root.ToJsonString();
    }
}
