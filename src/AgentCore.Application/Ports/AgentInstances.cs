using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Ports;

public interface IAgentInstanceStore
{
    ValueTask<AgentInstance?> FindAsync(Guid instanceId, CancellationToken cancellationToken = default);

    ValueTask<AgentInstance?> FindCompatibilityAsync(string definitionId, CancellationToken cancellationToken = default);

    ValueTask InsertAsync(AgentInstance instance, CancellationToken cancellationToken = default);

    ValueTask UpdateActiveVersionAsync(
        Guid instanceId,
        int activeVersion,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);
}

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
        CancellationToken cancellationToken = default);

    ValueTask<AgentInstance> RequireAsync(Guid instanceId, CancellationToken cancellationToken = default);

    ValueTask BackfillAsync(CancellationToken cancellationToken = default);
}
