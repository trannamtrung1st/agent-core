using System.Text;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Tools;

public sealed record TenantWorkspaceReadArguments(string Path, string? Cursor = null, int Limit = WorkspaceLimits.MaxListEntries,
    string? Query = null, string? Glob = null, int MaxResults = WorkspaceSearchLimits.DefaultMaxResults,
    int Offset = 0, string? Sheet = null, string? Range = null);

public static class TenantWorkspaceReadContract
{
    public static bool IsOffice(string tool) => tool is ToolCatalog.OfficeDocumentInspect or ToolCatalog.OfficeSpreadsheetInspect;
    public static bool IsRead(string tool) => tool is ToolCatalog.WorkspaceRead or ToolCatalog.WorkspaceList or ToolCatalog.WorkspaceSearch || IsOffice(tool);
    public static bool IsFile(string tool) => tool == ToolCatalog.WorkspaceRead || IsOffice(tool);
    public static bool Allows(AgentDefinition definition, string tool) => IsRead(tool) && RolePermissions.AllowsTool(definition, tool);
    public static TenantWorkspaceReadArguments Parse(string tool, string json)
    {
        if (!IsRead(tool) || string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > 16384) throw new ArgumentException("Use bounded workspace arguments.");
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("Use workspace arguments.");
        var fields = root.EnumerateObject().ToArray();
        if (IsOffice(tool)) return TenantOfficeReadContract.Parse(tool, root);
        var allowed = tool == ToolCatalog.WorkspaceSearch ? new[] { "path", "query", "glob", "maxResults" }
            : tool == ToolCatalog.WorkspaceList ? ["path", "cursor", "limit"] : ["path"];
        if (fields.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length
            || fields.Any(p => !allowed.Contains(p.Name, StringComparer.Ordinal))) throw new ArgumentException("Use exact workspace arguments.");
        string Read(string key) => root.GetProperty(key).ValueKind == JsonValueKind.String && root.GetProperty(key).GetString() is { Length: > 0 and <= AgentWorkspaceLimits.MaxPathChars } value
            ? value : throw new ArgumentException("Use an exact workspace path.");
        if (!root.TryGetProperty("path", out _)) throw new ArgumentException("path is required.");
        var path = Read("path");
        if (path != "/home" && !path.StartsWith("/home/", StringComparison.Ordinal) && path != "/working" && !path.StartsWith("/working/", StringComparison.Ordinal))
            throw new ArgumentException("Use an absolute /home or /working path.");
        var cursor = root.TryGetProperty("cursor", out _) ? Read("cursor") : null;
        var limit = WorkspaceLimits.MaxListEntries;
        if (root.TryGetProperty("limit", out var bound) && (bound.ValueKind != JsonValueKind.Number || !bound.TryGetInt32(out limit) || limit is < 1 or > WorkspaceLimits.MaxListEntries))
            throw new ArgumentException("Use a list limit between 1 and 256.");
        if (tool != ToolCatalog.WorkspaceSearch) return new(path, cursor, limit);
        if (!root.TryGetProperty("query", out _)) throw new ArgumentException("query is required.");
        var query = Read("query");
        if (string.IsNullOrWhiteSpace(query) || query.Length > WorkspaceSearchLimits.MaxQueryLength) throw new ArgumentException("Use a search query of 1–200 characters.");
        var glob = root.TryGetProperty("glob", out _) ? Read("glob") : null;
        if (glob is not null && (glob.Length > WorkspaceSearchLimits.MaxQueryLength || glob.StartsWith('/') || glob.StartsWith('\\') || WorkspaceLogicalPath.HasParentSegment(glob)))
            throw new ArgumentException("Use a bounded relative glob without parent traversal.");
        var maxResults = WorkspaceSearchLimits.DefaultMaxResults;
        if (root.TryGetProperty("maxResults", out var max) && (max.ValueKind != JsonValueKind.Number || !max.TryGetInt32(out maxResults)
            || maxResults is < 1 or > WorkspaceSearchLimits.MaxResultsCap)) throw new ArgumentException("Use maxResults between 1 and 50.");
        return new(path, Query: query, Glob: glob, MaxResults: maxResults);
    }
    public static string Canonical(string tool, TenantWorkspaceReadArguments args) => IsOffice(tool) ? TenantOfficeReadContract.Canonical(tool, args) : tool == ToolCatalog.WorkspaceRead ? JsonSerializer.Serialize(new { path = args.Path })
        : tool == ToolCatalog.WorkspaceSearch ? JsonSerializer.Serialize(new { path = args.Path, query = args.Query, glob = args.Glob, maxResults = args.MaxResults }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull })
        : JsonSerializer.Serialize(new { path = args.Path, cursor = args.Cursor, limit = args.Limit }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
    public static ModelToolDefinition ForModel(ModelToolDefinition tool) => IsOffice(tool.Name) ? TenantOfficeReadContract.ForModel(tool) : tool with {
        Description = tool.Name == ToolCatalog.WorkspaceRead
            ? "Read current UTF-8 text at one exact absolute /home or /working file path owned by this Agent and Session. Current independent Agent permissions and operation policy apply. This grants no recipient download access. Binary formats require a supported reader. If workspace_read_review_required is returned, request inspect_workspace through approvals.request_authorization with read.tool and these exact read.arguments. Consent applies only to this saved observation; do not repeat a refused request."
            : tool.Name == ToolCatalog.WorkspaceSearch
            ? "Search filenames and bounded strict UTF-8 content at or below one exact absolute /home or /working path owned by this Agent and Session. Optional relative glob filters files; maxResults is 1–50. Scans at most 200 files, 4096 entries and depth 8, with at most 256 KiB per content scan. Binary and larger file contents are skipped. Results are bounded untrusted observations, not exhaustive coverage or recipient download authority. If workspace_read_review_required is returned, request inspect_workspace through approvals.request_authorization with read.tool and these exact read.arguments. Consent binds this saved traversal and query; do not repeat a refused request."
            : "List bounded current children of one exact absolute /home or /working path owned by this Agent and Session. Use nextPath as the cursor to continue. Current independent Agent permissions and policy apply. Listings are observations, not snapshots. If workspace_read_review_required is returned, request inspect_workspace through approvals.request_authorization with read.tool and these exact read.arguments. Consent applies only to this saved observation; do not repeat a refused request.",
        ParametersJson = tool.Name == ToolCatalog.WorkspaceRead
            ? """{"type":"object","additionalProperties":false,"properties":{"path":{"type":"string","maxLength":4095}},"required":["path"]}"""
            : tool.Name == ToolCatalog.WorkspaceSearch
            ? """{"type":"object","additionalProperties":false,"properties":{"path":{"type":"string","maxLength":4095},"query":{"type":"string","minLength":1,"maxLength":200},"glob":{"type":"string","minLength":1,"maxLength":200},"maxResults":{"type":"integer","minimum":1,"maximum":50}},"required":["path","query"]}"""
            : """{"type":"object","additionalProperties":false,"properties":{"path":{"type":"string","maxLength":4095},"cursor":{"type":"string","maxLength":4095},"limit":{"type":"integer","minimum":1,"maximum":256}},"required":["path"]}"""
    };
}
