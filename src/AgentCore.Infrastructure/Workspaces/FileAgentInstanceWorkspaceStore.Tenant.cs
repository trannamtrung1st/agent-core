using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Workspaces;

public sealed partial class FileAgentInstanceWorkspaceStore : ITenantWorkspaceReadStore
{
    private async ValueTask<CanonicalWorkspaceFilesystem> TenantFiles(TenantWorkspaceReference owner, CancellationToken ct)
    {
        if (owner.TenantId == Guid.Empty || owner.InstanceId == Guid.Empty || owner.SessionId == Guid.Empty) throw new AccessDeniedException("resource_unavailable");
        var agent = await (tenantAgents ?? throw new AccessDeniedException("tenant_context_required")).FindTenantAgentAsync(owner.TenantId, owner.InstanceId, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("resource_unavailable");
        if (owner.SessionId is { } sessionId)
        {
            var session = await (tenantSessions ?? throw new AccessDeniedException("tenant_context_required")).FindTenantSessionAsync(owner.TenantId, sessionId, ct).ConfigureAwait(false)
                ?? throw new AccessDeniedException("resource_unavailable");
            if (session.Owner.AgentInstanceId != owner.InstanceId || session.Owner.ExecutorPrincipalId != agent.PrincipalId) throw new AccessDeniedException("resource_unavailable");
        }
        var root = owner.SessionId is { } id ? AgentWorkspacePhysicalPaths.WorkingDirectory(_root, owner.InstanceId, id)
            : AgentWorkspacePhysicalPaths.HomeDirectory(_root, owner.InstanceId);
        return new(root, owner.LogicalRoot);
    }
    public async ValueTask<AgentWorkspacePage> ListTenantAsync(TenantWorkspaceReference owner, string path, string? cursor, int limit, CancellationToken ct = default)
    {
        if (limit is < 1 or > AgentWorkspaceLimits.MaxPageItems) throw AgentCoreErrors.Validation("Invalid workspace page size.");
        path = TenantWorkspaceRules.Path(owner, path); if (cursor is not null) TenantWorkspaceRules.Path(owner, cursor);
        var files = await TenantFiles(owner, ct).ConfigureAwait(false);
        try
        {
            var entry = files.StatResolved(path); var page = entry.Directory ? files.List(path, cursor, limit) : new CanonicalWorkspaceFilesystem.Page([entry], null, false);
            CanonicalWorkspaceFilesystem.Usage? usage = null;
            try { usage = files.Measure(ct); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or AgentCoreException) { }
            return new(page.Entries.Select(e => TenantItem(owner, e)).ToArray(), usage?.Bytes, usage?.Entries, page.NextPath, usage?.Enforcement ?? "unknown", page.Truncated);
        }
        catch (AgentCoreException error) when (error.Code == "NotFound" && path == owner.LogicalRoot) { return new([], 0, 0, null); }
    }
    public async ValueTask<AgentWorkspacePage> SearchTenantAsync(TenantWorkspaceReference owner, string query, string? cursor, int limit, CancellationToken ct = default)
    {
        if (query is null || query.Length > 200 || limit is < 1 or > AgentWorkspaceLimits.MaxPageItems) throw AgentCoreErrors.Validation("Invalid workspace search bound.");
        if (cursor is not null) TenantWorkspaceRules.Path(owner, cursor.TrimEnd('/'));
        var files = await TenantFiles(owner, ct).ConfigureAwait(false);
        try
        {
            var page = files.FindFiles(query, cursor, limit, ct);
            return new(page.Entries.Select(e => TenantItem(owner, e)).ToArray(), null, null, page.NextPath, "unknown", page.Truncated);
        }
        catch (AgentCoreException error) when (error.Code == "NotFound") { return new([], 0, 0, null); }
    }
    public async ValueTask<AgentWorkspaceContent> InspectTenantAsync(TenantWorkspaceReference owner, string path, int maxContentBytes, CancellationToken ct = default)
    {
        if (maxContentBytes < 0 || maxContentBytes > maxFileBytes) throw AgentCoreErrors.Validation("Invalid workspace content bound.");
        path = TenantWorkspaceRules.Path(owner, path, file: true);
        var files = await TenantFiles(owner, ct).ConfigureAwait(false); var entry = files.StatResolved(path);
        if (entry.Directory) throw AgentCoreErrors.Validation("Read requires a file; list the directory instead.");
        var bytes = maxContentBytes == 0 ? [] : await files.ReadAsync(path, maxContentBytes, ct).ConfigureAwait(false);
        return new(TenantItem(owner, entry), bytes);
    }
    private static AgentWorkspaceItem TenantItem(TenantWorkspaceReference owner, CanonicalWorkspaceFilesystem.Entry entry)
    {
        var target = entry.LinkTarget;
        if (target is not null)
        {
            var candidate = target.StartsWith('/') ? target : entry.Path[..entry.Path.LastIndexOf('/')] + "/" + target;
            if (!WorkspaceLogicalPath.TryResolve(candidate, owner.SessionId ?? Guid.Empty, out var path, out _, out _)
                || path != owner.LogicalRoot && !path.StartsWith(owner.LogicalRoot + "/", StringComparison.Ordinal)) target = null;
        }
        return Item(owner.InstanceId, entry) with { LinkTarget = target };
    }
}
