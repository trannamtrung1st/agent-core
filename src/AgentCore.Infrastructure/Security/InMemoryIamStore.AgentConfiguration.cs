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
        lock (_gate) lock (instances.CredentialGate) lock (isolationBacking.IsolationGate)
        {
            var agent = FindTenantAgentAsync(tenantId, instanceId, ct).Result;
            return ValueTask.FromResult(agent is null ? null : new TenantAgentConfigurationSnapshot(agent,
                instances.ReadTenantSkillsAsync(instanceId, ct).Result, instances.ReadTenantResourcesAsync(instanceId, ct).Result, Isolation(instanceId)));
        }
    }

    public ValueTask<TenantAgent?> TryChangeTenantAgentConfigurationAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantAgentConfigurationChange change, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next);
        IamStateRules.ValidateAgentWrite(next, expectedSecurityRevision, change.InstanceId, AgentInstanceLifecycle.Active, audit);
        lock (_gate) lock (instances.CredentialGate) lock (definitions.TenantGate) lock (isolationBacking.IsolationGate)
        {
            if (!_tenants.TryGetValue(next.Tenant.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult<TenantAgent?>(null);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next);
            ValidateAgentActor(before, audit.ActorPrincipalId); EnsureReferences(next, audit, ct: ct);
            var current = FindTenantAgentAsync(next.Tenant.TenantId, change.InstanceId, ct).Result;
            if (current is null || current.Instance.Revision != change.ExpectedInstanceRevision) return ValueTask.FromResult<TenantAgent?>(null);
            if (change.Isolation is { } isolation && Isolation(change.InstanceId) is var expectedMetadata
                && (expectedMetadata.ExperienceRevision != isolation.Expected.Experience || expectedMetadata.MaintenanceRevision != isolation.Expected.Maintenance))
                return ValueTask.FromResult<TenantAgent?>(null);
            _ = TenantAgentConfigurationRules.Apply(current.Instance, change, at);
            if (next.Assignments.Any(a => TenantAgentChildScopes.BelongsTo(a.Scope, change.InstanceId)))
            {
                var available = ChildCatalog(next.Tenant.TenantId, change.InstanceId, change, ct);
                if (next.Assignments.Any(a => TenantAgentChildScopes.BelongsTo(a.Scope, change.InstanceId) && !available.Contains(a.Scope)))
                    throw new ArgumentException("Retire grants for removed children in the same Instance transaction.");
            }
            if (change.ActiveVersion is { } version && Guid.TryParse(current.Instance.DefinitionId, out var definitionId))
            {
                var publication = definitions.TenantPublication(next.Tenant.TenantId, definitionId, version);
                if (publication?.Status != DefinitionPublicationStatus.Active || publication.MetadataRevision != change.ExpectedPublicationRevision)
                    throw new SecurityConflictException("resource_changed");
                change = change with { DefinitionSkills = publication.Payload.SkillList };
            }
            ct.ThrowIfCancellationRequested();
            if (change.Resource?.Resource is { } binding)
            {
                var content = definitions.ResourceStore?.TenantContent ?? throw new AccessDeniedException("resource_content_unavailable");
                _ = AgentCore.Application.Admin.DefinitionResourceStoredContent.RequireForBindingAsync(content, binding.ContentSha256, binding.ByteLength, binding.MediaType, ct).GetAwaiter().GetResult();
            }
            RequireAgentHuman(next.Tenant.TenantId, audit.ActorPrincipalId, change.Initiator);
            if (_tenants[next.Tenant.TenantId].Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult<TenantAgent?>(null);
            if (change.Skill is not null || change.Resource is not null)
            {
                if (change.Skill is { } skill) instances.MutateTenantSkillsAsync(skill, at, ct).GetAwaiter().GetResult();
                else instances.MutateTenantResourcesAsync(change.Resource!, at, ct).GetAwaiter().GetResult();
                _tenants[next.Tenant.TenantId] = next; _audit.Add(audit);
                return ValueTask.FromResult<TenantAgent?>(current with { Instance = instances.FindTenantBackingAsync(change.InstanceId, ct).Result! });
            }
            var updated = instances.ChangeTenantConfigurationAsync(new(change.InstanceId, change.ExpectedInstanceRevision,
                ActiveVersion: change.ActiveVersion, Persona: change.Persona, ExpectedPersonaRevision: change.ExpectedPersonaRevision,
                DefinitionSkills: change.DefinitionSkills, AutomationPolicyChanges: change.AutomationPolicyChanges,
                SetSettingsOverrides: change.SetSettingsOverrides, SettingsOverrides: change.SettingsOverrides,
                SetExecutionBudgets: change.SetExecutionBudgets, ExecutionBudgets: change.ExecutionBudgets,
                SetUnattendedModel: change.SetUnattendedModel, UnattendedModelCatalogKey: change.UnattendedModelCatalogKey,
                UnattendedReasoningEffort: change.UnattendedReasoningEffort, HarnessManagement: change.HarnessManagement), at, ct).Result;
            if (change.Isolation is { } mutation)
            {
                var metadata = Isolation(change.InstanceId);
                if (metadata.ExperienceEnabled != mutation.ExperienceEnabled)
                    isolationBacking.ConfigureAsync(change.InstanceId, metadata.ExperienceRevision, mutation.ExperienceEnabled).GetAwaiter().GetResult();
                if (metadata.AllowMaintenance != mutation.AllowMaintenance)
                    isolationBacking.ConfigureMaintenanceAsync(change.InstanceId, metadata.MaintenanceRevision, mutation.AllowMaintenance).GetAwaiter().GetResult();
            }
            foreach (var fact in AgentCore.Infrastructure.Persistence.CoreEventPersistence.Instance(current.Instance, updated,
                AgentCore.Domain.Triggers.TriggerOwner.ForTenant(next.Tenant.TenantId, updated.InstanceId, current.PrincipalId))) AppendTenantCore(fact);
            _tenants[next.Tenant.TenantId] = next; _audit.Add(audit);
            return ValueTask.FromResult<TenantAgent?>(current with { Instance = updated });
        }
    }
    private TenantIsolationMetadata Isolation(Guid id)
    {
        var learned = isolationBacking.SettingsAsync(id).Result;
        var maintenance = isolationBacking.MaintenanceSettingsAsync(id).Result;
        return new(learned.Revision, learned.Enabled, maintenance.Revision, maintenance.AllowAgentConsolidation);
    }
}
