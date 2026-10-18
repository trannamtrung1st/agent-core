using System.Text;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Tools;

public sealed record TenantWorkspaceReadArguments(string Path, string? Cursor = null, int Limit = WorkspaceLimits.MaxListEntries);

public static class TenantWorkspaceReadContract
{
    public static bool IsRead(string tool) => tool is ToolCatalog.WorkspaceRead or ToolCatalog.WorkspaceList;
    public static bool Allows(AgentDefinition definition, string tool) => IsRead(tool) && RolePermissions.AllowsTool(definition, tool);
    public static TenantWorkspaceReadArguments Parse(string tool, string json)
    {
        if (!IsRead(tool) || string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > 16384) throw new ArgumentException("Use bounded workspace arguments.");
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("Use workspace arguments.");
        var fields = root.EnumerateObject().ToArray();
        if (fields.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length
            || fields.Any(p => p.Name != "path" && (tool != ToolCatalog.WorkspaceList || p.Name is not ("cursor" or "limit")))) throw new ArgumentException("Use exact workspace arguments.");
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
        return new(path, cursor, limit);
    }
    public static ModelToolDefinition ForModel(ModelToolDefinition tool) => tool with {
        Description = tool.Name == ToolCatalog.WorkspaceRead
            ? "Read current UTF-8 text at one exact absolute /home or /working file path owned by this Agent and Session. Current independent Agent permissions and operation policy apply. This grants no recipient download access. Binary formats require a supported reader. Review-required reads remain unavailable."
            : "List bounded current children of one exact absolute /home or /working path owned by this Agent and Session. Use nextPath as the cursor to continue. Current independent Agent permissions and policy apply. Listings are observations, not snapshots. Review-required reads remain unavailable.",
        ParametersJson = tool.Name == ToolCatalog.WorkspaceRead
            ? """{"type":"object","additionalProperties":false,"properties":{"path":{"type":"string","maxLength":4095}},"required":["path"]}"""
            : """{"type":"object","additionalProperties":false,"properties":{"path":{"type":"string","maxLength":4095},"cursor":{"type":"string","maxLength":4095},"limit":{"type":"integer","minimum":1,"maximum":256}},"required":["path"]}"""
    };
}
