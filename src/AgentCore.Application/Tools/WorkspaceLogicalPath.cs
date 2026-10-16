using System.Text;
using AgentCore.Application.Agents;
using AgentCore.Application.Sessions;

namespace AgentCore.Application.Tools;

public static class WorkspaceLogicalPath
{
    public const string WorkingDirectory = "/working";
    public const string OutsideWorkspaceMessage = "Only approved session workspace paths can be accessed.";
    public static bool HasParentSegment(string? path) => path?.Split('/').Contains("..") == true;

    public static bool TryResolve(string? raw, Guid sessionId, out string canonical, out string errorCode, out string message)
    {
        canonical = ""; errorCode = "invalid"; message = "path is required.";
        if (string.IsNullOrEmpty(raw)) return false;
        if (raw.Contains('\\') || raw.Contains('\0') || Encoding.UTF8.GetByteCount(raw) > 4095) return false;
        var path = raw.StartsWith('/') ? raw : WorkingDirectory + "/" + raw;
        var parts = new List<string>();
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count <= 1) { errorCode = "path_outside_workspace"; message = OutsideWorkspaceMessage; return false; }
                parts.RemoveAt(parts.Count - 1);
            }
            else
            {
                if (Encoding.UTF8.GetByteCount(part) > 255) return false;
                parts.Add(part);
            }
        }
        canonical = "/" + string.Join('/', parts);
        if (!RolePermissions.AllowsLogicalPath(canonical, sessionId))
        { canonical = ""; errorCode = "path_outside_workspace"; message = OutsideWorkspaceMessage; return false; }
        errorCode = ""; message = ""; return true;
    }

    public static string Resolve(string raw, Guid sessionId)
    {
        if (TryResolve(raw, sessionId, out var path, out var code, out var message)) return path;
        throw code == "path_outside_workspace" ? AgentCoreErrors.Forbidden(message) : AgentCoreErrors.Validation(message);
    }
}
