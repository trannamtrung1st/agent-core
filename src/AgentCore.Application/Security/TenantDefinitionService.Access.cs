using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantDefinitionAccess(IReadOnlyList<string> Permissions, long Revision);
public sealed partial class TenantDefinitionService
{
    public async ValueTask<TenantDefinitionSummary> SummaryAsync(Guid actor, Guid tenant, Guid id, CancellationToken ct = default)
    {
        await authorizer.RequireAsync(actor, "agent-definitions.read", Scope(tenant, id), ct).ConfigureAwait(false);
        var root = await definitions.FindDefinitionAsync(tenant, id, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var drafts = await definitions.ListDefinitionDraftsAsync(tenant, id, ct).ConfigureAwait(false);
        var versions = await definitions.ListDefinitionPublicationsAsync(tenant, id, ct).ConfigureAwait(false);
        var latest = versions.LastOrDefault();
        var name = latest is not null ? (await definitions.FindDefinitionPublicationAsync(tenant, id, latest.Version, ct).ConfigureAwait(false))?.Payload.Identity.Name : null;
        return new(root, drafts.Count, latest?.Version, name ?? drafts.OrderByDescending(d => d.UpdatedAt).FirstOrDefault()?.Candidate.Identity.Name ?? id.ToString("D"));
    }
    public async ValueTask<TenantDefinitionAccess> AccessAsync(Guid actor, Guid tenant, Guid id, CancellationToken ct = default)
    {
        var state = await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        var scope = Scope(tenant, id);
        var read = await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get("agent-definitions.read"), scope, ct).ConfigureAwait(false);
        if (!read.Allowed) throw new AccessDeniedException(read.ReasonCode);
        var permissions = new List<string>();
        foreach (var key in new[] { "agent-definitions.read", "agent-definitions.update", "agent-definitions.publish", "agent-definitions.delete", "iam.roles.read", "iam.roles.assign" })
            if ((await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed) permissions.Add(key);
        return new(permissions, state.Tenant.Revision);
    }
}
