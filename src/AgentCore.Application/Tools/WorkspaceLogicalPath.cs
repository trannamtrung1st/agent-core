using AgentCore.Application.Agents;
using AgentCore.Application.Sessions;

namespace AgentCore.Application.Tools;

public static class WorkspaceLogicalPath
{
    public const string WorkingDirectory = "/workspace/working";
    public const string OutsideWorkspaceMessage = "Only approved session workspace paths can be accessed.";

    public static bool HasParentSegment(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        foreach (var token in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token == "..")
            {
                return true;
            }
        }

        return false;
    }

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
        if (trimmed is "/")
        {
            canonical = "/";
            return Finalize(sessionId, canonical, out errorCode, out message);
        }

        var explicitRoot = IsExplicitLogicalRoot(trimmed);
        if (!explicitRoot && trimmed.StartsWith('/'))
        {
            return Outside(out canonical, out errorCode, out message);
        }

        var segments = new List<string>();
        var anchor = explicitRoot ? 1 : 0;
        if (!explicitRoot)
        {
            segments.Add("workspace");
            segments.Add("working");
            anchor = segments.Count;
            while (trimmed.StartsWith("./", StringComparison.Ordinal))
            {
                trimmed = trimmed[2..];
            }

            if (trimmed is "" or ".")
            {
                canonical = WorkingDirectory;
                return Finalize(sessionId, canonical, out errorCode, out message);
            }
        }

        foreach (var segment in trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count <= anchor)
                {
                    return Outside(out canonical, out errorCode, out message);
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
        {
            return Outside(out canonical, out errorCode, out message);
        }

        var startedInWorking = !explicitRoot
            || trimmed.Equals(WorkingDirectory, StringComparison.Ordinal)
            || trimmed.StartsWith(WorkingDirectory + "/", StringComparison.Ordinal);
        var usedParent = HasParentSegment(trimmed);
        canonical = "/" + string.Join('/', segments);
        if (usedParent && startedInWorking
            && canonical != WorkingDirectory
            && !canonical.StartsWith(WorkingDirectory + "/", StringComparison.Ordinal))
        {
            return Outside(out canonical, out errorCode, out message);
        }

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

    private static bool Outside(out string canonical, out string errorCode, out string message)
    {
        canonical = "";
        errorCode = "path_outside_workspace";
        message = OutsideWorkspaceMessage;
        return false;
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
}
