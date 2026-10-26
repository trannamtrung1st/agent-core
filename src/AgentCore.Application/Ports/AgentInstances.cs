using AgentCore.Domain.Triggers;
using AgentCore.Application.Admin;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Ports;

public interface IAgentInstanceStore
{
    ValueTask<InstanceResourceSnapshot> ReadResourcesAsync(Guid instanceId, CancellationToken ct = default) => throw new NotSupportedException();
    ValueTask MutateResourcesAsync(InstanceResourceMutation mutation, CancellationToken ct = default) => throw new NotSupportedException();
    ValueTask<InstanceSkillSnapshot> ReadSkillsAsync(Guid instanceId, CancellationToken ct = default) => throw new NotSupportedException();
    ValueTask MutateSkillsAsync(SkillMutation mutation, CancellationToken ct = default) => throw new NotSupportedException();

    ValueTask<IReadOnlyList<AgentInstance>> ListAsync(
        int limit,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AgentInstance>> ListMaintenancePageAsync(Guid? afterId, int limit,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    ValueTask<AgentInstance?> FindAsync(Guid instanceId, CancellationToken cancellationToken = default);

    /// <summary>Cleanup protection only: includes every retained backing Instance, regardless of
    /// tenant or lifecycle. This does not authorize access to its configuration or content.</summary>
    ValueTask<bool> HasBackingInstanceAsync(Guid instanceId, CancellationToken cancellationToken = default);

    ValueTask InsertAsync(AgentInstance instance, CancellationToken cancellationToken = default, IReadOnlyList<SkillSpec>? initialSkills = null);

    ValueTask<AgentInstance> InsertManagedWithHistoryAsync(
        AgentInstance instance,
        AdminEventAppend historyAppend,
        CancellationToken cancellationToken = default, IReadOnlyList<SkillSpec>? initialSkills = null);

    ValueTask<AgentInstance> UpdateActiveVersionWithHistoryAsync(
        AgentInstanceRevisionUpdate update,
        DateTimeOffset updatedAt,
        AdminEventAppend historyAppend,
        CancellationToken cancellationToken = default);

    ValueTask<AgentInstance> UpdatePersonaWithHistoryAsync(
        AgentInstanceRevisionUpdate update,
        DateTimeOffset updatedAt,
        AdminEventAppend historyAppend,
        CancellationToken cancellationToken = default);

    ValueTask<AgentInstance> UpdateLifecycleWithHistoryAsync(
        AgentInstanceRevisionUpdate update,
        DateTimeOffset updatedAt,
        AdminEventAppend historyAppend,
        CancellationToken cancellationToken = default);

    ValueTask UpdateActiveVersionAsync(
        Guid instanceId,
        int activeVersion,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    ValueTask<AgentInstance> UpdateWithExpectedRevisionAsync(
        AgentInstanceRevisionUpdate update,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);
}

public sealed record AgentInstanceRevisionUpdate(
    Guid InstanceId,
    long ExpectedRevision,
    int? ActiveVersion = null,
    AgentIdentity? Persona = null,
    AgentInstanceLifecycle? Lifecycle = null,
    long? ExpectedPersonaRevision = null,
    bool SetUnattendedModel = false,
    AgentCore.Domain.Models.ModelPreference? UnattendedModel = null,
    HarnessManagementState? HarnessManagement = null,
    AdminEventAppend? History = null, IReadOnlyList<SkillSpec>? DefinitionSkills = null,
    bool SetExecutionBudgets = false, ExecutionBudgetPolicy? ExecutionBudgets = null,
    bool SetSettingsOverrides = false, InstanceSettingsOverrides? SettingsOverrides = null,
    IReadOnlyList<InstanceAutomationPolicyChange>? AutomationPolicyChanges = null);



public sealed record InstanceSkillSnapshot(IReadOnlyList<AgentDefinitionSkillState> DefinitionStates, IReadOnlyList<AgentInstanceSkill> InstanceSkills);
public sealed record SkillMutation(Guid InstanceId, long ExpectedInstanceRevision,
    AgentDefinitionSkillState? DefinitionState = null, AgentInstanceSkill? InstanceSkill = null,
    string? DeleteSkillId = null, long? ExpectedSkillRevision = null, long? ExpectedStateRevision = null,
    AdminEventAppend? History = null);

public sealed record InstanceResourceSnapshot(IReadOnlyList<AgentDefinitionResourceState> DefinitionStates,
    IReadOnlyList<AgentInstanceResource> InstanceResources);
public sealed record InstanceResourceMutation(Guid InstanceId, long ExpectedInstanceRevision,
    AgentInstanceResource? Resource = null, AgentDefinitionResourceState? DefinitionState = null,
    Guid? DeleteResourceId = null, long? ExpectedResourceRevision = null, long? ExpectedStateRevision = null,
    AdminEventAppend? History = null, HarnessManagementState? HarnessManagement = null);
