using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantDefinitionAccess(IReadOnlyList<string> Permissions, long Revision, IReadOnlyDictionary<int, bool> CanFork);
public sealed partial class TenantDefinitionService
{
    public ValueTask<TenantDefinitionSummary> SummaryAsync(Guid actor, Guid tenant, Guid id, CancellationToken ct = default, HumanSessionReference? initiator = null) =>
        WithHumanRead<TenantDefinitionSummary>(actor, tenant, initiator, async () =>
        {
            await authorizer.RequireAsync(actor, "agent-definitions.read", Scope(tenant, id), ct).ConfigureAwait(false);
            var root = await definitions.FindDefinitionAsync(tenant, id, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
            var drafts = await definitions.ListDefinitionDraftsAsync(tenant, id, ct).ConfigureAwait(false);
            var versions = await definitions.ListDefinitionPublicationsAsync(tenant, id, ct).ConfigureAwait(false);
            var latest = versions.LastOrDefault();
            var name = latest is not null ? (await definitions.FindDefinitionPublicationAsync(tenant, id, latest.Version, ct).ConfigureAwait(false))?.Payload.Identity.Name : null;
            return new(root, drafts.Count, latest?.Version, name ?? drafts.OrderByDescending(d => d.UpdatedAt).FirstOrDefault()?.Candidate.Identity.Name ?? id.ToString("D"));
        }, ct);
    public ValueTask<TenantDefinitionAccess> AccessAsync(Guid actor, Guid tenant, Guid id, CancellationToken ct = default, HumanSessionReference? initiator = null) =>
        WithHumanRead<TenantDefinitionAccess>(actor, tenant, initiator, async () =>
        {
            var state = await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
            var scope = Scope(tenant, id);
            var read = await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get("agent-definitions.read"), scope, ct).ConfigureAwait(false);
            if (!read.Allowed) throw new AccessDeniedException(read.ReasonCode);
            var permissions = new List<string>();
            foreach (var key in new[] { "agent-definitions.read", "agent-definitions.update", "agent-definitions.publish", "agent-definitions.delete", "iam.roles.read", "iam.roles.assign" })
                if ((await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed) permissions.Add(key);
            var destination = await ResourceAllowed(state, actor, "resources.read", AccessScope.ForTenant(tenant), ct).ConfigureAwait(false)
                && await ResourceAllowed(state, actor, "resources.manage", AccessScope.ForTenant(tenant), ct).ConfigureAwait(false);
            var forks = new Dictionary<int, bool>();
            foreach (var publication in await definitions.ListDefinitionPublicationsAsync(tenant, id, ct).ConfigureAwait(false))
                forks[publication.Version] = permissions.Contains("agent-definitions.update") && (destination
                    || (await definitions.ListOwnedPublicationResourcesAsync(tenant, id, publication.Version, ct).ConfigureAwait(false)).Count == 0);
            return new(permissions, state.Tenant.Revision, forks);
        }, ct);
}
