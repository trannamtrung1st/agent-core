using AgentCore.Application.Agents;
using AgentCore.Application.Sessions;

namespace AgentCore.Application.Tools;

public static class WorkspaceLogicalPath
{
    public const string WorkingDirectory = "/workspace/working";

    public static bool TryResolve(string? raw, Guid sessionId, out string canonical, out string errorCode, out string message)
    {
        canonical = "";
        errorCode = "";
        message = "";
        if (string.IsNullOrWhiteSpace(raw))
        {
            errorCode = "invalid";
            message = "path is required.";
            return false;
        }

        var trimmed = raw.Replace('\\', '/').Trim();
        while (trimmed.StartsWith("./", StringComparison.Ordinal))
        {
            trimmed = trimmed[2..];
        }

        if (trimmed is "" or ".")
        {
            canonical = WorkingDirectory;
            return Finalize(sessionId, canonical, out errorCode, out message);
        }

        if (IsExplicitLogicalRoot(trimmed))
        {
            canonical = NormalizeAbsolute(trimmed);
            return Finalize(sessionId, canonical, out errorCode, out message);
        }

        if (trimmed.StartsWith('/'))
        {
            errorCode = "path_outside_workspace";
            message = "Only files inside the session workspace can be modified.";
            return false;
        }

        if (trimmed.Contains("..", StringComparison.Ordinal))
        {
            errorCode = "path_outside_workspace";
            message = "Only files inside the session workspace can be modified.";
            return false;
        }

        trimmed = trimmed.TrimStart('/');
        canonical = trimmed.Length == 0 ? WorkingDirectory : $"{WorkingDirectory}/{trimmed}";
        canonical = NormalizeAbsolute(canonical);
        return Finalize(sessionId, canonical, out errorCode, out message);
    }

    public static string Resolve(string raw, Guid sessionId)
    {
        if (!TryResolve(raw, sessionId, out var canonical, out var errorCode, out var message))
        {
            throw errorCode switch
            {
                "path_outside_workspace" => AgentCoreErrors.Forbidden(message),
                _ => AgentCoreErrors.Validation(message)
            };
        }

        return canonical;
    }

    private static bool Finalize(Guid sessionId, string canonical, out string errorCode, out string message)
    {
        if (!RolePermissions.AllowsLogicalPath(canonical, sessionId))
        {
            errorCode = "forbidden";
            message = "Path is not permitted for this role.";
            return false;
        }

        errorCode = "";
        message = "";
        return true;
    }

    private static bool IsExplicitLogicalRoot(string path) =>
        path.StartsWith("/agent", StringComparison.Ordinal)
        || path.StartsWith("/attachments", StringComparison.Ordinal)
        || path.StartsWith("/workspace", StringComparison.Ordinal);

    private static string NormalizeAbsolute(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        if (!normalized.StartsWith('/'))
        {
            normalized = "/" + normalized;
        }

        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        if (normalized.Length > 1)
        {
            normalized = normalized.TrimEnd('/');
        }

        return normalized;
    }
}
