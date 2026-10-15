using System.Text;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Workspaces;

public static class AgentHomePath
{
    public static bool IsHome(string path) => path == "/home" || path.StartsWith("/home/", StringComparison.Ordinal);
    public static string Normalize(string path, bool file = true)
    {
        if (string.IsNullOrEmpty(path) || Encoding.UTF8.GetByteCount(path) > AgentWorkspaceLimits.MaxPathChars
            || !IsHome(path) || path.Contains('\\') || path.Contains('\0'))
            throw AgentCoreErrors.Validation("Use a logical path under /home.");
        if (path == "/home") return !file ? path : throw AgentCoreErrors.Validation("A home child path is required.");
        if (path[6..].Split('/').Any(p => p is "" or "." or ".." || Encoding.UTF8.GetByteCount(p) > 255))
            throw AgentCoreErrors.Validation("Home path contains an invalid segment.");
        return path;
    }
}
