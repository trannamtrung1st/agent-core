using AgentCore.Application.Sessions;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Workspaces;

/// <summary>Trusted ownership paths only. Logical home names never participate in physical blob paths.</summary>
public static class AgentWorkspacePhysicalPaths
{
    public static string AgentRoot(string root, Guid owner)
    {
        if (owner == Guid.Empty) throw AgentCoreErrors.Persistence("Session workspace ownership is invalid.");
        return Path.Combine(Path.GetFullPath(root), "agent-" + owner.ToString("N"));
    }

    public static string HomeBlobRoot(string root, Guid owner) => Path.Combine(AgentRoot(root, owner), "home", "blobs");
    public static string SessionRoot(string root, Guid owner, Guid session) =>
        Path.Combine(AgentRoot(root, owner), "sessions", "session-" + session.ToString("N"));
    public static string WorkingDirectory(string root, Guid owner, Guid session) => Path.Combine(SessionRoot(root, owner, session), "working");

    public static string ValidateRoot(string root)
    {
        AttachmentBlobKeys.EnsureSafeRoot(root);
        root = Path.GetFullPath(root);
        var obsoleteHome = Path.Combine(Path.GetDirectoryName(root)!, "agent-workspaces");
        if (Path.GetFileName(root) == "workspaces" && Directory.Exists(obsoleteHome) && Directory.EnumerateFileSystemEntries(obsoleteHome).Any()) ResetRequired();
        if (!Directory.Exists(root)) return root;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            AttachmentBlobKeys.EnsureSafeRoot(directory);
            var name = Path.GetFileName(directory);
            if (Guid.TryParse(name, out _) || name == "agent-workspaces") ResetRequired();
            if (!name.StartsWith("agent-", StringComparison.Ordinal)) continue;
            var sessions = Path.Combine(directory, "sessions");
            AttachmentBlobKeys.EnsureSafeRoot(sessions);
            if (!Directory.Exists(sessions)) continue;
            foreach (var session in Directory.EnumerateDirectories(sessions))
            {
                AttachmentBlobKeys.EnsureSafeRoot(session);
                if (Guid.TryParse(Path.GetFileName(session), out _) || Directory.Exists(Path.Combine(session, "workspace"))) ResetRequired();
            }
        }
        return root;
    }

    private static void ResetRequired() => throw AgentCoreErrors.Persistence(
        "Legacy workspace layout/data reset required: stop the API and explicitly back up/reset the database and data roots before starting this version. No data was moved or deleted.");
}
