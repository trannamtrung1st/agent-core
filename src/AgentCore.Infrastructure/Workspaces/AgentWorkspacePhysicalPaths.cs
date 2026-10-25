using AgentCore.Application.Sessions;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Workspaces;

/// <summary>Trusted ownership roots only. Logical names are resolved beneath these roots by directory handles.</summary>
public static class AgentWorkspacePhysicalPaths
{
    public static string AgentRoot(string root, Guid owner)
    {
        if (owner == Guid.Empty) throw AgentCoreErrors.Persistence("Session workspace ownership is invalid.");
        return Path.Combine(Path.GetFullPath(root), "agent-" + owner.ToString("N"));
    }

    public static string SpaceHomeDirectory(string root, Guid tenant, Guid space)
    {
        if (tenant == Guid.Empty || space == Guid.Empty) throw AgentCoreErrors.Persistence("Space workspace ownership is invalid.");
        return Path.Combine(Path.GetFullPath(root), "tenant-" + tenant.ToString("N"), "spaces", "space-" + space.ToString("N"), "home");
    }
    public static string SessionRoot(string root, Guid owner, Guid session) =>
        session == Guid.Empty ? throw AgentCoreErrors.Persistence("Session workspace ownership is invalid.")
            : Path.Combine(AgentRoot(root, owner), "sessions", "session-" + session.ToString("N"));
    public static string WorkingDirectory(string root, Guid owner, Guid session) => Path.Combine(SessionRoot(root, owner, session), "working");

    public static string ValidateRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || Path.GetFullPath(root) == Path.GetPathRoot(Path.GetFullPath(root)))
            throw new ArgumentException("A dedicated absolute canonical workspace root is required.");
        root = Path.GetFullPath(root);
        new CanonicalWorkspaceFilesystem(root, "/working").EnsureLayout();
        return root;
    }
}
