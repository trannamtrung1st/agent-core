using AgentCore.Application.Admin;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Ports;

public interface IAgentInstanceStore
{
    ValueTask<IReadOnlyList<AgentInstance>> ListAsync(
        int limit,
        CancellationToken cancellationToken = default);

    ValueTask<AgentInstance?> FindAsync(Guid instanceId, CancellationToken cancellationToken = default);

    ValueTask<AgentInstance?> FindCompatibilityAsync(string definitionId, CancellationToken cancellationToken = default);

    ValueTask InsertAsync(AgentInstance instance, CancellationToken cancellationToken = default);

    ValueTask<AgentInstance> InsertManagedWithHistoryAsync(
        AgentInstance instance,
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
    long? ExpectedPersonaRevision = null);

public interface IAgentInstanceService
{
    ValueTask<AgentInstance> CreateAsync(
        string definitionId,
        int? version = null,
        CancellationToken cancellationToken = default);

    ValueTask<AgentInstance> ResolveCompatibilityAsync(
        AgentDefinition definition,
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

    ValueTask BackfillAsync(CancellationToken cancellationToken = default);
}
