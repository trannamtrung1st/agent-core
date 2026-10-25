using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private readonly Dictionary<(Guid Tenant, Guid Space, Guid Resource), SpaceFileResource> _spaceResources = [];
    public ValueTask<SpaceFileResource?> FindSpaceResourceAsync(Guid tenant, Guid space, Guid resource, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate)
            return ValueTask.FromResult(_spaces.GetValueOrDefault((tenant, space)) is { DeletedAt: null }
                && _spaceResources.GetValueOrDefault((tenant, space, resource)) is { DeletedAt: null } found ? found : null);
    }
    public ValueTask<SpaceResourcePage> ReadSpaceResourcesAsync(Guid tenant, Guid space, int page, int pageSize, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); SpaceStoreRules.Page(tenant, page, pageSize); lock (_gate)
        {
            var available = _spaces.GetValueOrDefault((tenant, space)) is { DeletedAt: null };
            var rows = available ? _spaceResources.Values.Where(r => r.TenantId == tenant && r.SpaceId == space && r.DeletedAt is null)
                .OrderBy(r => r.ResourceId.ToString("D"), StringComparer.Ordinal).ToArray() : [];
            return ValueTask.FromResult(new SpaceResourcePage(rows.Skip(page * pageSize).Take(pageSize).ToArray(), rows.Length, page, pageSize));
        }
    }
    public ValueTask<bool> TrySaveSpaceResourceAsync(TenantSecurityState next, long expectedSecurityRevision, long expectedSpaceRevision,
        SpaceFileResource value, long expectedResourceRevision, AuthorizationAudit audit, HumanSessionReference? human = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next);
        SpaceResourceStoreRules.Validate(next, expectedSecurityRevision, expectedSpaceRevision, value, expectedResourceRevision, audit);
        lock (_gate) lock (instances.CredentialGate)
        {
            if (!_tenants.TryGetValue(value.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision
                || _spaces.GetValueOrDefault((value.TenantId, value.SpaceId)) is not { DeletedAt: null } owner || owner.Revision != expectedSpaceRevision) return ValueTask.FromResult(false);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); ValidateAgentActor(before, audit.ActorPrincipalId);
            EnsureReferences(next, audit, ct: ct); RequireAgentHuman(value.TenantId, audit.ActorPrincipalId, human);
            var key = (value.TenantId, value.SpaceId, value.ResourceId); var current = _spaceResources.GetValueOrDefault(key);
            if (!SpaceResourceStoreRules.Matches(current, value, expectedResourceRevision)) return ValueTask.FromResult(false);
            if (value.DeletedAt is not null && _spaceMemberships.Values.Any(m => m.TenantId == value.TenantId && m.SpaceId == value.SpaceId
                && m.AddedBindings.Any(b => b.Kind == SpaceBindingKind.SpaceFile && b.ResourceKey == value.Key))) throw new SecurityConflictException("space_resource_in_use");
            SpaceResourceStoreRules.Limits(_spaceResources.Values.Where(r => r.TenantId == value.TenantId && r.SpaceId == value.SpaceId).ToArray(), value);
            _spaceResources[key] = value;
            _spaces[(value.TenantId, value.SpaceId)] = owner with { Revision = checked(owner.Revision + 1), UpdatedAt = value.UpdatedAt };
            _tenants[value.TenantId] = next; _audit.Add(audit); return ValueTask.FromResult(true);
        }
    }
}
