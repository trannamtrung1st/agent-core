using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantAgent(Guid TenantId, Guid SystemIdentityId, Guid PrincipalId, AgentInstance Instance);
public sealed record TenantAgentPage(IReadOnlyList<TenantAgent> Items, int Total, int Page, int PageSize);
public sealed record TenantAgentConfigurationSnapshot(TenantAgent Agent, InstanceSkillSnapshot Skills, InstanceResourceSnapshot Resources, TenantIsolationMetadata Isolation);
public sealed record TenantAgentConfigurationChange(Guid InstanceId, long ExpectedInstanceRevision,
    AgentIdentity? Persona = null, long? ExpectedPersonaRevision = null, int? ActiveVersion = null,
    long? ExpectedPublicationRevision = null, IReadOnlyList<SkillSpec>? DefinitionSkills = null,
    IReadOnlyList<Triggers.InstanceAutomationPolicyChange>? AutomationPolicyChanges = null,
    bool SetSettingsOverrides = false, InstanceSettingsOverrides? SettingsOverrides = null,
    bool SetExecutionBudgets = false, ExecutionBudgetPolicy? ExecutionBudgets = null,
    bool SetUnattendedModel = false, string? UnattendedModelCatalogKey = null, string? UnattendedReasoningEffort = null,
    HarnessManagementState? HarnessManagement = null, SkillMutation? Skill = null, InstanceResourceMutation? Resource = null,
    HumanSessionReference? Initiator = null, TenantIsolationMutation? Isolation = null);

public sealed record TenantIsolationMetadata(long ExperienceRevision, bool ExperienceEnabled, long MaintenanceRevision, bool AllowMaintenance);
public sealed record TenantIsolationMutation(IsolationOwnerRevisions Expected, bool ExperienceEnabled, bool AllowMaintenance);

// Specialized transaction boundary: the existing Instance, its identity, security revision and audit
// are committed together. Reads filter authoritative identity ownership before ordering/pagination.
public interface ITenantAgentStore
{
    ValueTask<TenantAgentConfigurationSnapshot?> ReadTenantAgentConfigurationAsync(Guid tenantId, Guid instanceId, CancellationToken ct = default);
    ValueTask<TenantAgent?> TryChangeTenantAgentConfigurationAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantAgentConfigurationChange change, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<TenantAgent?> FindTenantAgentAsync(Guid tenantId, Guid instanceId, CancellationToken ct = default);
    ValueTask<TenantAgentPage> ListTenantAgentsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedInstanceIds,
        int page, int pageSize, CancellationToken ct = default);
    ValueTask<bool> TryCreateTenantAgentAsync(TenantSecurityState next, long expectedSecurityRevision,
        AgentInstance instance, IReadOnlyList<SkillSpec> skills, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null);
    ValueTask<TenantAgent?> TrySetTenantAgentLifecycleAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid instanceId, long expectedInstanceRevision, AgentInstanceLifecycle lifecycle, DateTimeOffset at,
        AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null);
}
