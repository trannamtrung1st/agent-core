using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Sessions;
using AgentCore.Application.Workspaces;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantWorkspaceRules
{
    public static AccessScope Home(Guid tenant, Guid space) => Scope(tenant, "home", space);
    public static AccessScope Working(Guid tenant, Guid session) => Scope(tenant, "working", session);
    private static AccessScope Scope(Guid tenant, string kind, Guid id)
    {
        if (tenant == Guid.Empty || id == Guid.Empty) throw new AccessDeniedException("resource_unavailable");
        return AccessScope.ForResource(tenant, "workspace", kind + ":" + id.ToString("D"));
    }
    public static AccessScope Scope(TenantWorkspaceReference owner) => owner switch {
        TenantWorkspaceReference.SpaceHome home => Home(home.TenantId, home.SpaceId),
        TenantWorkspaceReference.SessionWorking working => Working(working.TenantId, working.SessionId),
        _ => throw new AccessDeniedException("resource_unavailable") };
    public static AgentCore.Domain.Spaces.RunSpaceWitness Context(AgentRun run) =>
        (run.Admission.Configuration?.SpaceContext ?? throw new AccessDeniedException("space_context_required")).Witness();
    public static AgentCore.Domain.Spaces.RunSpaceWitness Context(PreparedTenantOperation operation) =>
        operation.SpaceContext ?? throw new AccessDeniedException("space_context_required");
    public static TenantWorkspaceReference ForRun(AgentRun run, string path)
    {
        var tenant = run.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var pin = run.Admission.Configuration?.SpaceContext ?? throw new AccessDeniedException("space_context_required");
        return ForCaptured(tenant, run.SessionId, path, pin.Witness());
    }
    public static TenantWorkspaceReference ForCaptured(Guid tenant, Guid session, string path, AgentCore.Domain.Spaces.RunSpaceWitness context)
    {
        context.Validate(); if (context.TenantId != tenant) throw new AccessDeniedException("tenant_context_mismatch");
        if (path == "/working" || path.StartsWith("/working/", StringComparison.Ordinal) || path.StartsWith("/attachments/", StringComparison.Ordinal))
            return new TenantWorkspaceReference.SessionWorking(tenant, session);
        return path == "/private" || path.StartsWith("/private/", StringComparison.Ordinal)
            ? new TenantWorkspaceReference.SpaceHome(tenant, context.PrivateSpaceId, true)
            : new TenantWorkspaceReference.SpaceHome(tenant, context.SpaceId);
    }
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
    public static string LogicalRoot(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() switch {
        "home" => "/home", "private" => "/private", "working" => "/working",
        _ => throw AgentCoreErrors.Validation("Use an exact absolute workspace path.") };
    public static string ValidatePath(string path, bool file = false)
    {
        var root = LogicalRoot(path);
        if (root == "/working")
        {
            var normalized = WorkspaceLogicalPath.Resolve(path, Guid.Empty);
            if (normalized != path || file && path == root) throw AgentCoreErrors.Validation("Use an exact absolute workspace child path.");
        }
        else
        {
            var normalized = AgentHomePath.Normalize("/home" + path[root.Length..], file);
            if (root + normalized[5..] != path) throw AgentCoreErrors.Validation("Use an exact absolute workspace child path.");
        }
        return path;
    }
    public static string Path(TenantWorkspaceReference owner, string path, bool file = false)
    {
        string normalized;
        if (owner is TenantWorkspaceReference.SpaceHome home)
        {
            var input = home.PrivateAlias && (path == "/private" || path.StartsWith("/private/", StringComparison.Ordinal)) ? "/home" + path[8..] : path;
            normalized = AgentHomePath.Normalize(input, file);
            if (home.PrivateAlias) normalized = "/private" + normalized[5..];
        }
        else if (owner is TenantWorkspaceReference.SessionWorking working)
            normalized = WorkspaceLogicalPath.Resolve(path, working.SessionId);
        else throw new AccessDeniedException("resource_unavailable");
        if (normalized != path || owner.IsWorking && normalized != "/working" && !normalized.StartsWith("/working/", StringComparison.Ordinal)
            || file && normalized == owner.LogicalRoot) throw AgentCoreErrors.Validation("Use an exact absolute path in this workspace.");
        return normalized;
    }
}
