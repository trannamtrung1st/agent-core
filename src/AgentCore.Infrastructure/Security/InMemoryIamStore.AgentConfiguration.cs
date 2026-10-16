using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<TenantAgentConfigurationSnapshot?> ReadTenantAgentConfigurationAsync(Guid tenantId, Guid instanceId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) lock (instances.CredentialGate)
        {
            var agent = FindTenantAgentAsync(tenantId, instanceId, ct).Result;
            return ValueTask.FromResult(agent is null ? null : new TenantAgentConfigurationSnapshot(agent,
                instances.ReadSkillsAsync(instanceId, ct).Result, instances.ReadResourcesAsync(instanceId, ct).Result));
        }
    }

    public ValueTask<TenantAgent?> TryChangeTenantAgentConfigurationAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantAgentConfigurationChange change, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next);
        IamStateRules.ValidateAgentWrite(next, expectedSecurityRevision, change.InstanceId, AgentInstanceLifecycle.Active, audit);
        lock (_gate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            if (!_tenants.TryGetValue(next.Tenant.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult<TenantAgent?>(null);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next);
            ValidateAgentActor(before, audit.ActorPrincipalId); EnsureReferences(next, audit);
            var current = FindTenantAgentAsync(next.Tenant.TenantId, change.InstanceId, ct).Result;
            if (current is null || current.Instance.Revision != change.ExpectedInstanceRevision) return ValueTask.FromResult<TenantAgent?>(null);
            _ = TenantAgentConfigurationRules.Apply(current.Instance, change, at);
            if (change.ActiveVersion is { } version && Guid.TryParse(current.Instance.DefinitionId, out var definitionId))
            {
                var publication = definitions.TenantPublication(next.Tenant.TenantId, definitionId, version);
                if (publication?.Status != DefinitionPublicationStatus.Active || publication.MetadataRevision != change.ExpectedPublicationRevision)
                    throw new SecurityConflictException("resource_changed");
                change = change with { DefinitionSkills = publication.Payload.SkillList };
            }
            ct.ThrowIfCancellationRequested();
            var updated = instances.ChangeTenantConfigurationAsync(new(change.InstanceId, change.ExpectedInstanceRevision,
                ActiveVersion: change.ActiveVersion, Persona: change.Persona, ExpectedPersonaRevision: change.ExpectedPersonaRevision,
                DefinitionSkills: change.DefinitionSkills, AutomationPolicyChanges: change.AutomationPolicyChanges), at, ct).Result;
            _tenants[next.Tenant.TenantId] = next; _audit.Add(audit);
            return ValueTask.FromResult<TenantAgent?>(current with { Instance = updated });
        }
    }
}
