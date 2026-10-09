using AgentCore.Application.Admin;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Ports;

public interface IAgentInstanceStore
{
    ValueTask<InstanceSkillSnapshot> ReadSkillsAsync(Guid instanceId, CancellationToken ct = default) => throw new NotSupportedException();
    ValueTask MutateSkillsAsync(SkillMutation mutation, CancellationToken ct = default) => throw new NotSupportedException();

    ValueTask<IReadOnlyList<AgentInstance>> ListAsync(
        int limit,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AgentInstance>> ListMaintenancePageAsync(Guid? afterId, int limit,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    ValueTask<AgentInstance?> FindAsync(Guid instanceId, CancellationToken cancellationToken = default);


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
    string? UnattendedModelCatalogKey = null,
    string? UnattendedReasoningEffort = null,
    HarnessManagementState? HarnessManagement = null,
    AdminEventAppend? History = null, IReadOnlyList<SkillSpec>? DefinitionSkills = null,
    bool SetExecutionBudgets = false, ExecutionBudgetPolicy? ExecutionBudgets = null);

public interface IAgentInstanceService
{
    ValueTask<AgentInstance> CreateAsync(
        string definitionId,
        int? version = null,
        CancellationToken cancellationToken = default);


    ValueTask<AgentInstance> UpgradeAsync(
        Guid instanceId,
        int version,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    ValueTask<AgentInstance> UpdatePersonaAsync(
        Guid instanceId,
        AgentIdentity persona,
        long expectedRevision,
        long expectedPersonaRevision,
        CancellationToken cancellationToken = default);

    ValueTask<AgentInstance> SetLifecycleAsync(
        Guid instanceId,
        AgentInstanceLifecycle lifecycle,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    ValueTask<AgentInstance> RequireAsync(Guid instanceId, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AgentInstance>> ListChatEligibleAsync(
        CancellationToken cancellationToken = default);

}

public sealed record InstanceSkillSnapshot(IReadOnlyList<AgentDefinitionSkillState> DefinitionStates, IReadOnlyList<AgentInstanceSkill> InstanceSkills);
public sealed record SkillMutation(Guid InstanceId, long ExpectedInstanceRevision,
    AgentDefinitionSkillState? DefinitionState = null, AgentInstanceSkill? InstanceSkill = null,
    string? DeleteSkillId = null, long? ExpectedSkillRevision = null, long? ExpectedStateRevision = null,
    AdminEventAppend? History = null);
