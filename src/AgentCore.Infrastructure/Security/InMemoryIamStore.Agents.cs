using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<TenantAgent?> FindTenantAgentAsync(Guid tenantId, Guid instanceId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) lock (instances.CredentialGate)
        {
            var identity = _tenants.GetValueOrDefault(tenantId)?.SystemIdentities.SingleOrDefault(i => i.AgentInstanceId == instanceId && i.Status != IdentityStatus.Removed);
            var instance = identity is null ? null : instances.FindTenantBackingAsync(instanceId, ct).Result;
            return ValueTask.FromResult(instance is null || identity is null ? null : new TenantAgent(tenantId, identity.SystemIdentityId, identity.PrincipalId, instance, identity.ManagedKind));
        }
    }

    public ValueTask<TenantAgentPage> ListTenantAgentsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedInstanceIds, int page, int pageSize, CancellationToken ct = default, TenantAgentChatFilter? chat = null)
    {
        ct.ThrowIfCancellationRequested();
        if (tenantId == Guid.Empty || page < 0 || page > 100_000 || pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        lock (_gate) lock (instances.CredentialGate)
        {
            var allowed = allowedInstanceIds?.ToHashSet();
            var rows = (_tenants.GetValueOrDefault(tenantId)?.SystemIdentities ?? []).Where(i => i.Kind == SystemIdentityKind.Agent && i.Status != IdentityStatus.Removed
                && i.AgentInstanceId is { } id && (allowed is null || allowed.Contains(id)))
                .Select(i => new TenantAgent(tenantId, i.SystemIdentityId, i.PrincipalId, instances.FindTenantBackingAsync(i.AgentInstanceId!.Value, ct).Result
                    ?? throw new InvalidOperationException("An owned agent lost its backing Instance."), i.ManagedKind))
                .Where(i => chat is null || i.Instance.ShowInChat && i.Instance.Lifecycle == AgentInstanceLifecycle.Active
                    && (chat.InstanceId is null || chat.InstanceId == i.Instance.InstanceId) && chat.Matches(i.Instance.Persona))
                .OrderByDescending(i => i.ManagedKind != null).ThenBy(i => i.Instance.InstanceId.ToString("D"), StringComparer.Ordinal).ToArray();
            return ValueTask.FromResult(new TenantAgentPage(rows.Skip(page * pageSize).Take(pageSize).ToArray(), rows.Length, page, pageSize));
        }
    }

    public ValueTask<bool> TryCreateTenantAgentAsync(TenantSecurityState next, long expectedSecurityRevision,
        AgentInstance instance, IReadOnlyList<SkillSpec> skills, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null) =>
        CreateTenantAgentCoreAsync(next, expectedSecurityRevision, instance, skills, audit, false, ct, initiator);

    public ValueTask<bool> TryProvisionGeneralAssistantAsync(TenantSecurityState next, long expectedSecurityRevision,
        AgentInstance instance, IReadOnlyList<SkillSpec> skills, AuthorizationAudit audit, CancellationToken ct = default) =>
        CreateTenantAgentCoreAsync(next, expectedSecurityRevision, instance, skills, audit, true, ct);

    private ValueTask<bool> CreateTenantAgentCoreAsync(TenantSecurityState next, long expectedSecurityRevision,
        AgentInstance instance, IReadOnlyList<SkillSpec> skills, AuthorizationAudit audit, bool managedProvision, CancellationToken ct = default, HumanSessionReference? initiator = null)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next);
        IamStateRules.ValidateAgentWrite(next, expectedSecurityRevision, instance.InstanceId, instance.Lifecycle, audit);
        ManagedGeneralAssistant.ValidateProvision(next, instance, audit, managedProvision);
        if (instance.Revision != 1 || instance.Lifecycle != AgentInstanceLifecycle.Active || instance.BrowserProfileReset is not null) throw new ArgumentException("A new active Instance is required.");
        lock (_gate) lock (instances.CredentialGate)
        {
            if (!_tenants.TryGetValue(next.Tenant.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult(false);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next);
            ManagedGeneralAssistant.ValidateMarkerAdditions(before, next, managedProvision ? instance.InstanceId : null);
            if (!managedProvision) ValidateAgentActor(before, audit.ActorPrincipalId);
            EnsureReferences(next, audit, instance.InstanceId, ct);
            if (instances.FindTenantBackingAsync(instance.InstanceId, ct).Result is not null) return ValueTask.FromResult(false);
            RequireAgentHuman(next.Tenant.TenantId, audit.ActorPrincipalId, initiator);
            instances.InsertAsync(instance, ct, skills).GetAwaiter().GetResult();
            ProvisionPrivateSpace(next.Tenant.TenantId, instance);
            if (managedProvision)
            {
                var owner = next.SystemIdentities.Single(i => i.AgentInstanceId == instance.InstanceId);
                var space = _spaces.Values.Single(s => s.TenantId == next.Tenant.TenantId && s.OwnerInstanceId == instance.InstanceId && s.DeletedAt is null).SpaceId;
                var role = next.Roles.Single(r => r.Key == ManagedGeneralAssistant.FilesRole);
                next = IamStateRules.Freeze(next with { Assignments = next.Assignments.Append(new RoleAssignment(Guid.NewGuid(), next.Tenant.TenantId,
                    owner.PrincipalId, role.RoleId, TenantWorkspaceRules.Home(next.Tenant.TenantId, space), Guid.Empty, instance.CreatedAt)).Append(new RoleAssignment(Guid.NewGuid(), next.Tenant.TenantId,
                    owner.PrincipalId, role.RoleId, AccessScope.ForResource(next.Tenant.TenantId, "space", space.ToString("D")), Guid.Empty, instance.CreatedAt)).ToArray() });
            }
            _tenants[next.Tenant.TenantId] = next; MarkOwnedAgents(next); _audit.Add(audit);
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<TenantAgent?> TrySetTenantAgentLifecycleAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid instanceId, long expectedInstanceRevision, AgentInstanceLifecycle lifecycle, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next);
        IamStateRules.ValidateAgentWrite(next, expectedSecurityRevision, instanceId, lifecycle, audit);
        lock (_gate) lock (instances.CredentialGate)
        {
            if (!_tenants.TryGetValue(next.Tenant.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult<TenantAgent?>(null);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next);
            if (before.SystemIdentities.Any(i => i.AgentInstanceId == instanceId && i.ManagedKind is not null)) throw new SecurityConflictException("system_managed_instance");
            ValidateAgentActor(before, audit.ActorPrincipalId); EnsureReferences(next, audit, ct: ct);
            var current = instances.FindTenantBackingAsync(instanceId, ct).Result;
            if (!before.SystemIdentities.Any(i => i.AgentInstanceId == instanceId && i.Status != IdentityStatus.Removed) || current is null || current.Revision != expectedInstanceRevision) return ValueTask.FromResult<TenantAgent?>(null);
            RequireAgentHuman(next.Tenant.TenantId, audit.ActorPrincipalId, initiator);
            var updated = instances.SetTenantLifecycleAsync(instanceId, expectedInstanceRevision, lifecycle, at, ct).Result;
            _tenants[next.Tenant.TenantId] = next; _audit.Add(audit);
            var identity = next.SystemIdentities.Single(i => i.AgentInstanceId == instanceId && i.Status != IdentityStatus.Removed);
            return ValueTask.FromResult<TenantAgent?>(new(next.Tenant.TenantId, identity.SystemIdentityId, identity.PrincipalId, updated, identity.ManagedKind));
        }
    }
    private void RequireAgentHuman(Guid tenant, Guid actor, HumanSessionReference? initiator)
    {
        if (initiator is null) return;
        if (initiator.PrincipalId != actor) throw new AccessDeniedException("initiator_unavailable");
        try { RequireHumanSession(tenant, initiator); }
        catch (AccessDeniedException) { throw new AccessDeniedException("initiator_unavailable"); }
    }
    private void ValidateAgentActor(TenantSecurityState before, Guid actorId)
    {
        var actor = before.Principals.SingleOrDefault(p => p.PrincipalId == actorId && p.Status == IdentityStatus.Active)
            ?? throw new AccessDeniedException("principal_unavailable");
        if (actor.Kind == PrincipalKind.User && _accounts.GetValueOrDefault(actor.AccountId!.Value)?.Status != IdentityStatus.Active)
            throw new AccessDeniedException("account_unavailable");
    }
}
