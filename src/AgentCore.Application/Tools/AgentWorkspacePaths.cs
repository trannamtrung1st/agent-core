using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Sessions;
using AgentCore.Application.Workspaces;

namespace AgentCore.Application.Tools;

/// <summary>V2 public paths are resolved once against a trusted mailbox snapshot before storage dispatch.</summary>
public static class AgentWorkspacePaths
{
    public const string Working = "/working";
    public static string Public(string path) => path == WorkspaceLogicalPath.WorkingDirectory
        || path.StartsWith(WorkspaceLogicalPath.WorkingDirectory + "/", StringComparison.Ordinal)
        ? Working + path[WorkspaceLogicalPath.WorkingDirectory.Length..] : path;

    public static string Resolve(string raw, Guid sessionId, string cwd = "/home", bool structural = false)
    {
        if (string.IsNullOrWhiteSpace(raw)) throw AgentCoreErrors.Validation("path is required.");
        var path = raw.Replace('\\', '/').Trim();
        if (structural && (WorkspaceLogicalPath.HasParentSegment(path) || path.IndexOfAny(['*', '?', '[', ']']) >= 0))
            throw AgentCoreErrors.Validation("Structural operations require concrete paths without globs or parent traversal.");
        if (!path.StartsWith('/')) path = cwd.TrimEnd('/') + "/" + path;
        var tokens = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (tokens.Count <= 1) throw AgentCoreErrors.Forbidden("Path cannot escape its logical root.");
                tokens.RemoveAt(tokens.Count - 1);
            }
            else tokens.Add(segment);
        }
        path = "/" + string.Join('/', tokens);
        if (path == Working || path.StartsWith(Working + "/", StringComparison.Ordinal))
            path = WorkspaceLogicalPath.WorkingDirectory + path[Working.Length..];
        else if (!AgentHomePath.IsHome(path) && path != "/agent" && !path.StartsWith("/agent/", StringComparison.Ordinal)
            && path != "/attachments" && !path.StartsWith("/attachments/", StringComparison.Ordinal))
            throw AgentCoreErrors.Forbidden("Use authorized /home or /working paths; /agent and /attachments are read-only.");
        if (WorkspaceLogicalPath.TryResolve(path, sessionId, out var canonical, out var code, out var message)) return canonical;
        throw code is "forbidden" or "path_outside_workspace" ? AgentCoreErrors.Forbidden(message) : AgentCoreErrors.Validation(message);
    }

    public static JsonElement Arguments(string tool, JsonElement args, Guid sessionId, string cwd)
    {
        ValidateObject(args);
        var structural = tool is ToolCatalog.WorkspaceMkdir or ToolCatalog.WorkspaceCopy or ToolCatalog.WorkspaceMove or ToolCatalog.WorkspaceDelete or ToolCatalog.WorkspaceBatch;
        var node = JsonNode.Parse(args.GetRawText())!.AsObject();
        ResolveFields(node);
        if (tool is ToolCatalog.WorkspaceList or ToolCatalog.WorkspaceSearch && !node.ContainsKey("path"))
            node["path"] = Resolve(".", sessionId, cwd);
        if (tool == ToolCatalog.WorkspaceBatch && node["operations"] is JsonArray operations)
            foreach (var operation in operations.OfType<JsonObject>()) ResolveFields(operation);
        return JsonSerializer.SerializeToElement(node);

        void ResolveFields(JsonObject obj)
        {
            foreach (var key in new[] { "path", "source", "destination" })
                if (obj[key] is JsonValue value && value.TryGetValue<string>(out var raw))
                    obj[key] = Resolve(raw, sessionId, cwd, structural);
        }
    }

    private static void ValidateObject(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw AgentCoreErrors.Validation("Duplicate workspace argument.");
                ValidateObject(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) ValidateObject(child);
    }

    // Only path-valued fields are mapped; user-authored file text is never rewritten.
    public static string Project(string json)
    {
        var node = JsonNode.Parse(json);
        Visit(node);
        return node?.ToJsonString() ?? json;
        static void Visit(JsonNode? node)
        {
            if (node is JsonArray array) { foreach (var child in array) Visit(child); }
            else if (node is JsonObject obj)
                foreach (var pair in obj.ToArray())
                {
                    if (pair.Key.ToLowerInvariant() is "path" or "logicalpath" or "source" or "destination" or "workspacelogicalpath"
                        && pair.Value is JsonValue value && value.TryGetValue<string>(out var path)) obj[pair.Key] = Public(path);
                    else Visit(pair.Value);
                }
        }
    }
}
