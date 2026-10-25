using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private readonly Dictionary<(Guid Tenant, Guid Space), Space> _spaces = [];
    private readonly Dictionary<(Guid Tenant, Guid Space, Guid Instance), SpaceMembership> _spaceMemberships = [];

    public ValueTask<Space?> FindSpaceAsync(Guid tenant, Guid space, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(_spaces.GetValueOrDefault((tenant, space)) is { DeletedAt: null } found ? found : null); }
    public ValueTask<Space?> FindPrivateSpaceAsync(Guid tenant, Guid instance, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(_spaces.Values.SingleOrDefault(s => s.TenantId == tenant && s.OwnerInstanceId == instance && s.DeletedAt is null)); }
    public ValueTask<SpacePage> ListSpacesAsync(Guid tenant, IReadOnlyList<Guid>? allowedSpaces, int page, int pageSize, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); SpaceStoreRules.Page(tenant, page, pageSize);
        lock (_gate)
        {
            var values = _spaces.Values.Where(s => s.TenantId == tenant && s.DeletedAt is null && (allowedSpaces is null || allowedSpaces.Contains(s.SpaceId)))
                .OrderBy(s => s.SpaceId.ToString("D"), StringComparer.Ordinal).ToArray();
            return ValueTask.FromResult(new SpacePage(values.Skip(page * pageSize).Take(pageSize).ToArray(), values.Length, page, pageSize));
        }
    }
    public ValueTask<IReadOnlyList<SpaceMembership>> ReadMembershipsAsync(Guid tenant, Guid space, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult<IReadOnlyList<SpaceMembership>>(
            _spaceMemberships.Values.Where(m => m.TenantId == tenant && m.SpaceId == space).OrderBy(m => m.InstanceId).Select(m => m.Freeze()).ToArray());
    }
    public ValueTask<bool> TryChangeSpaceAsync(TenantSecurityState next, long expectedSecurityRevision, SpaceChange change,
        AuthorizationAudit audit, HumanSessionReference? initiator = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next); SpaceStoreRules.Validate(next, expectedSecurityRevision, change, audit);
        var member = change.Membership?.Freeze(); var space = change.Space; var key = (space.TenantId, space.SpaceId);
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (sandboxBacking.TenantGate)
        {
            if (!_tenants.TryGetValue(space.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult(false);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); ValidateAgentActor(before, audit.ActorPrincipalId);
            EnsureReferences(next, audit, ct: ct); RequireAgentHuman(space.TenantId, audit.ActorPrincipalId, initiator);
            _spaces.TryGetValue(key, out var current);
            if (!SpaceStoreRules.Matches(current, change)) return ValueTask.FromResult(false);
            if (member is not null)
            {
                foreach (var binding in member.AddedBindings.Where(b => b.Kind == SpaceBindingKind.SpaceFile))
                    if (!AgentCore.Domain.Spaces.SpaceResourceKeys.TryParse(binding.ResourceKey, out var sourceSpace, out var sourceId) || sourceSpace != space.SpaceId
                        || FindSpaceResourceAsync(space.TenantId, sourceSpace, sourceId, ct).Result is null) throw new AccessDeniedException("resource_unavailable");
                var existing = _spaceMemberships.GetValueOrDefault((space.TenantId, space.SpaceId, member.InstanceId));
                if (change.RemoveMembership && existing is null || (existing?.Revision ?? 0) != change.ExpectedMembershipRevision || !before.SystemIdentities.Any(i => i.AgentInstanceId == member.InstanceId && i.Status == IdentityStatus.Active)) return ValueTask.FromResult(false);
                if (space.Kind == SpaceKind.Private && (change.RemoveMembership || member.InstanceId != space.OwnerInstanceId || existing is null))
                throw new AccessDeniedException("private_membership_managed");
                if (change.RemoveMembership) RequireSpaceMemberQuiescent(space.TenantId, space.SpaceId, member.InstanceId);
            }
            if (change.Delete)
            {
                if (_spaceMemberships.Keys.Any(k => k.Tenant == space.TenantId && k.Space == space.SpaceId)) throw new SecurityConflictException("space_has_memberships");
                var retiredProfiles = _environments.Values.Where(e => e.TenantId == space.TenantId && e.SpaceId == space.SpaceId && e.DeletedAt is null)
                    .Select(e => e with { Revision = checked(e.Revision + 1), DeletedAt = audit.At, UpdatedAt = audit.At }).ToArray();
                var removedVariables = _variables.Where(v => v.Value.TenantId == space.TenantId && v.Value.SpaceId == space.SpaceId).Select(v => v.Key).ToArray();
                _spaces[key] = current! with { DeletedAt = audit.At, UpdatedAt = audit.At, Revision = space.Revision };
                foreach (var profile in retiredProfiles) _environments[(profile.TenantId, profile.SpaceId, profile.EnvironmentId)] = profile;
                foreach (var variable in removedVariables) _variables.Remove(variable);
            }
            else _spaces[key] = space;
            if (member is not null)
            {
                var memberKey = (space.TenantId, space.SpaceId, member.InstanceId);
                if (change.RemoveMembership) _spaceMemberships.Remove(memberKey); else _spaceMemberships[memberKey] = member;
            }
            _tenants[space.TenantId] = next; _audit.Add(audit); return ValueTask.FromResult(true);
        }
    }
    private void ProvisionPrivateSpace(Guid tenant, AgentCore.Domain.Definitions.AgentInstance instance)
    {
        var at = instance.CreatedAt;
        var space = new Space(tenant, Guid.NewGuid(), SpaceKind.Private, instance.InstanceId, "Private Space", "", "", false, 1, at, at);
        space.Validate(); _spaces.Add((tenant, space.SpaceId), space);
        _spaceMemberships.Add((tenant, space.SpaceId, instance.InstanceId), new(tenant, space.SpaceId, instance.InstanceId, "", "", [], [], 1, at, at));
    }
}
