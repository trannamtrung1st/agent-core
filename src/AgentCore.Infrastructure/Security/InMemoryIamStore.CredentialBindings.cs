using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<TenantCredentialBindingSnapshot?> ReadTenantCredentialBindingsAsync(Guid tenantId, Guid instanceId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (instances.CredentialGate)
        {
            var parent = FindTenantAgentAsync(tenantId, instanceId, ct).Result;
            return ValueTask.FromResult(parent is null ? null : new TenantCredentialBindingSnapshot(parent, CredentialBacking().ReadTenantBindings(tenantId, instanceId)));
        }
    }
    public ValueTask<TenantAgent?> TryBindTenantCredentialAsync(TenantSecurityState next, long expectedSecurityRevision,
        AgentCredentialBinding binding, long expectedInstanceRevision, long expectedCredentialRevision, AuthorizationAudit audit, CancellationToken ct = default) =>
        ValueTask.FromResult(ChangeCredentialBinding(next, expectedSecurityRevision, binding.AgentInstanceId, expectedInstanceRevision, binding.UpdatedAtUtc,
            audit, () => CredentialBacking().PrepareTenantBind(next.Tenant.TenantId, binding, expectedCredentialRevision), ct));
    public ValueTask<TenantAgent?> TryUnbindTenantCredentialAsync(TenantSecurityState next, long expectedSecurityRevision, Guid instanceId,
        Guid bindingId, long expectedInstanceRevision, long expectedBindingRevision, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default) =>
        ValueTask.FromResult(ChangeCredentialBinding(next, expectedSecurityRevision, instanceId, expectedInstanceRevision, at,
            audit, () => CredentialBacking().PrepareTenantUnbind(next.Tenant.TenantId, instanceId, bindingId, expectedBindingRevision), ct));
    private TenantAgent? ChangeCredentialBinding(TenantSecurityState next, long expectedSecurityRevision, Guid instanceId, long expectedInstanceRevision,
        DateTimeOffset at, AuthorizationAudit audit, Func<Action?> prepare, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next);
        IamStateRules.ValidateAgentWrite(next, expectedSecurityRevision, instanceId, AgentInstanceLifecycle.Active, audit);
        lock (_gate) lock (instances.CredentialGate)
        {
            if (!_tenants.TryGetValue(next.Tenant.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision) return null;
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); ValidateAgentActor(before, audit.ActorPrincipalId); EnsureReferences(next, audit, ct: ct);
            var parent = FindTenantAgentAsync(next.Tenant.TenantId, instanceId, ct).Result;
            if (parent is null || parent.Instance.Revision != expectedInstanceRevision) return null;
            if (parent.Instance.Lifecycle != AgentInstanceLifecycle.Active) throw new AccessDeniedException("agent_archived");
            var apply = prepare(); if (apply is null) return null;
            ct.ThrowIfCancellationRequested();
            var updated = instances.ChangeTenantConfigurationAsync(new(instanceId, expectedInstanceRevision), at, ct).Result;
            apply(); _tenants[next.Tenant.TenantId] = next; _audit.Add(audit);
            return parent with { Instance = updated };
        }
    }
}
