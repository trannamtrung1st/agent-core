using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Application.Workspaces;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantWorkspaceRules
{
    public static AccessScope Home(Guid tenant, Guid instance) => Scope(tenant, "home", instance);
    public static AccessScope Working(Guid tenant, Guid session) => Scope(tenant, "working", session);
    private static AccessScope Scope(Guid tenant, string kind, Guid id)
    {
        if (tenant == Guid.Empty || id == Guid.Empty) throw new AccessDeniedException("resource_unavailable");
        return AccessScope.ForResource(tenant, "workspace", kind + ":" + id.ToString("D"));
    }
    public static AccessScope Scope(TenantWorkspaceReference owner) => owner.SessionId is { } session ? Working(owner.TenantId, session) : Home(owner.TenantId, owner.InstanceId);
    public static bool TryParse(AccessScope scope, out bool working, out Guid id)
    {
        working = false; id = Guid.Empty;
        if (scope.Kind != AccessScopeKind.Resource || scope.TenantId is not { } tenant || tenant == Guid.Empty || scope.ResourceType != "workspace"
            || scope.ResourceId is not { } value) return false;
        var offset = value.StartsWith("working:", StringComparison.Ordinal) ? 8 : value.StartsWith("home:", StringComparison.Ordinal) ? 5 : 0;
        if (offset == 0 || !Guid.TryParseExact(value[offset..], "D", out id) || id == Guid.Empty || value[offset..] != id.ToString("D")) return false;
        working = offset == 8; return true;
    }
    public static bool BelongsToSession(AccessScope scope, Guid session) => TryParse(scope, out var working, out var id) && working && id == session;
    public static string Path(TenantWorkspaceReference owner, string path, bool file = false)
    {
        var normalized = owner.SessionId is null ? AgentHomePath.Normalize(path, file) : WorkspaceLogicalPath.Resolve(path, owner.SessionId.Value);
        if (normalized != path || owner.SessionId is not null && normalized != "/working" && !normalized.StartsWith("/working/", StringComparison.Ordinal)
            || file && normalized == owner.LogicalRoot) throw AgentCoreErrors.Validation("Use an exact absolute path in this workspace.");
        return normalized;
    }
}
