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
            var identity = _tenants.GetValueOrDefault(tenantId)?.SystemIdentities.SingleOrDefault(i => i.AgentInstanceId == instanceId);
            var instance = identity is null ? null : instances.FindTenantBackingAsync(instanceId, ct).Result;
            return ValueTask.FromResult(instance is null || identity is null ? null : new TenantAgent(tenantId, identity.SystemIdentityId, identity.PrincipalId, instance));
        }
    }

    public ValueTask<TenantAgentPage> ListTenantAgentsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedInstanceIds, int page, int pageSize, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (tenantId == Guid.Empty || page < 0 || page > 100_000 || pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        lock (_gate) lock (instances.CredentialGate)
        {
            var allowed = allowedInstanceIds?.ToHashSet();
            var rows = (_tenants.GetValueOrDefault(tenantId)?.SystemIdentities ?? []).Where(i => i.Kind == SystemIdentityKind.Agent
                && i.AgentInstanceId is { } id && (allowed is null || allowed.Contains(id)))
                .Select(i => new TenantAgent(tenantId, i.SystemIdentityId, i.PrincipalId, instances.FindTenantBackingAsync(i.AgentInstanceId!.Value, ct).Result
                    ?? throw new InvalidOperationException("An owned agent lost its backing Instance.")))
                .OrderBy(i => i.Instance.InstanceId.ToString("D"), StringComparer.Ordinal).ToArray();
            return ValueTask.FromResult(new TenantAgentPage(rows.Skip(page * pageSize).Take(pageSize).ToArray(), rows.Length, page, pageSize));
        }
    }

    public ValueTask<bool> TryCreateTenantAgentAsync(TenantSecurityState next, long expectedSecurityRevision,
        AgentInstance instance, IReadOnlyList<SkillSpec> skills, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next);
        IamStateRules.ValidateAgentWrite(next, expectedSecurityRevision, instance.InstanceId, instance.Lifecycle, audit);
        if (instance.Revision != 1 || instance.Lifecycle != AgentInstanceLifecycle.Active) throw new ArgumentException("A new active Instance is required.");
        lock (_gate) lock (instances.CredentialGate)
        {
            if (!_tenants.TryGetValue(next.Tenant.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult(false);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next);
            ValidateAgentActor(before, audit.ActorPrincipalId);
            EnsureReferences(next, audit, instance.InstanceId);
            if (instances.FindTenantBackingAsync(instance.InstanceId, ct).Result is not null) return ValueTask.FromResult(false);
            instances.InsertAsync(instance, ct, skills).GetAwaiter().GetResult();
            _tenants[next.Tenant.TenantId] = next; MarkOwnedAgents(next); _audit.Add(audit);
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<TenantAgent?> TrySetTenantAgentLifecycleAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid instanceId, long expectedInstanceRevision, AgentInstanceLifecycle lifecycle, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next);
        IamStateRules.ValidateAgentWrite(next, expectedSecurityRevision, instanceId, lifecycle, audit);
        lock (_gate) lock (instances.CredentialGate)
        {
            if (!_tenants.TryGetValue(next.Tenant.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult<TenantAgent?>(null);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next);
            ValidateAgentActor(before, audit.ActorPrincipalId); EnsureReferences(next, audit);
            var current = instances.FindTenantBackingAsync(instanceId, ct).Result;
            if (!before.SystemIdentities.Any(i => i.AgentInstanceId == instanceId) || current is null || current.Revision != expectedInstanceRevision) return ValueTask.FromResult<TenantAgent?>(null);
            var updated = instances.SetTenantLifecycleAsync(instanceId, expectedInstanceRevision, lifecycle, at, ct).Result;
            _tenants[next.Tenant.TenantId] = next; _audit.Add(audit);
            var identity = next.SystemIdentities.Single(i => i.AgentInstanceId == instanceId);
            return ValueTask.FromResult<TenantAgent?>(new(next.Tenant.TenantId, identity.SystemIdentityId, identity.PrincipalId, updated));
        }
    }
    private void ValidateAgentActor(TenantSecurityState before, Guid actorId)
    {
        var actor = before.Principals.SingleOrDefault(p => p.PrincipalId == actorId && p.Status == IdentityStatus.Active)
            ?? throw new AccessDeniedException("principal_unavailable");
        if (actor.Kind == PrincipalKind.User && _accounts.GetValueOrDefault(actor.AccountId!.Value)?.Status != IdentityStatus.Active)
            throw new AccessDeniedException("account_unavailable");
    }
}
