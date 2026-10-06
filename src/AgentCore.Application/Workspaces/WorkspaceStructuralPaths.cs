using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Workspaces;

public static class WorkspaceStructuralPaths
{
    public static IReadOnlyList<WorkspaceStructuralOperation> Normalize(Guid sessionId, IReadOnlyList<WorkspaceStructuralOperation> operations) =>
        operations.Select(op => op with { Path = ResolveFor(sessionId, op.Path), Source = ResolveFor(sessionId, op.Source), Destination = ResolveFor(sessionId, op.Destination) }).ToArray();

    private static string? ResolveFor(Guid sessionId, string? path)
    {
        if (path is null) return null;
        if (path.IndexOfAny(['*', '?', '[', ']']) >= 0)
            throw AgentCoreErrors.Validation("Structural workspace operations require concrete paths, without glob syntax.");
        if (WorkspaceLogicalPath.HasParentSegment(path.Replace('\\', '/')))
            throw AgentCoreErrors.Validation("Structural workspace paths cannot contain parent traversal.");
        return WorkspaceLogicalPath.Resolve(path, sessionId);
    }

}
