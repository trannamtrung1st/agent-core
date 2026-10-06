using System.Text;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Workspaces;

public static class AgentHomePath
{
    public static bool IsHome(string path) => path == "/home" || path.StartsWith("/home/", StringComparison.Ordinal);

    public static string Normalize(string path, bool file = true)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > AgentWorkspaceLimits.MaxPathChars || path.Contains('\\')
            || !IsHome(path) || path != path.Normalize(NormalizationForm.FormC))
            throw AgentCoreErrors.Validation("Use a normalized logical path under /home.");
        if (path == "/home" && !file) return path;
        if (path == "/home") throw AgentCoreErrors.Validation("A home file path is required.");
        var parts = path[6..].Split('/');
        if (parts.Length > AgentWorkspaceLimits.MaxDepth || parts.Any(p => p.Length is 0 or > 128
            || p is "." or ".." || p.StartsWith('.') || p.EndsWith('.') || p != p.Trim()
            || p.Any(c => char.IsControl(c) || "<>:\"|?*".Contains(c))
            || Reserved(p)))
            throw AgentCoreErrors.Validation("Home path contains an invalid, reserved or excessive segment.");
        return path;
    }

    private static bool Reserved(string name)
    {
        var n = name.ToLowerInvariant();
        var stem = n.Split('.')[0];
        return stem is "con" or "prn" or "aux" or "nul" or "runtime" or "secrets" or "user-secrets"
            || (stem.Length == 4 && (stem.StartsWith("com") || stem.StartsWith("lpt")) && char.IsAsciiDigit(stem[3]))
            || n.StartsWith("appsettings.") || n.Contains(".env") || n.Contains(".git");
    }
}
