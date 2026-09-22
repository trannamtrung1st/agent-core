using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Identity;

public sealed class AgentInstanceService(
    IAgentInstanceStore instances,
    IAgentDefinitionStore definitions,
    IMemoryStore sessions,
    IIdGenerator ids,
    TimeProvider time) : IAgentInstanceService
{
    public async ValueTask<AgentInstance> CreateAsync(
        string definitionId,
        int? version = null,
        CancellationToken cancellationToken = default)
    {
        var definition = await definitions.GetAsync(definitionId, version, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound($"Agent '{definitionId}' was not found.");
        var now = time.GetUtcNow();
        var created = new AgentInstance(
            ids.NewId(),
            definition.Id,
            definition.Version,
            definition.Identity,
            AgentInstanceLifecycle.Active,
            now,
            now,
            Compatibility: false);
        await instances.InsertAsync(created, cancellationToken).ConfigureAwait(false);
        return created;
    }

    public async ValueTask<AgentInstance> ResolveCompatibilityAsync(
        AgentDefinition definition,
        CancellationToken cancellationToken = default)
    {
        var existing = await instances.FindCompatibilityAsync(definition.Id, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var now = time.GetUtcNow();
        var created = new AgentInstance(
            AgentInstance.CompatibilityFor(definition.Id),
            definition.Id,
            definition.Version,
            definition.Identity,
            AgentInstanceLifecycle.Active,
            now,
            now,
            Compatibility: true);
        try
        {
            await instances.InsertAsync(created, cancellationToken).ConfigureAwait(false);
            return created;
        }
        catch (AgentCoreException ex) when (ex.Code == "Conflict")
        {
            return await instances.FindCompatibilityAsync(definition.Id, cancellationToken).ConfigureAwait(false)
                ?? throw new AgentCoreException("Conflict", "Agent instance already exists.", 409);
        }
    }

    public async ValueTask<AgentInstance> UpgradeAsync(
        Guid instanceId,
        int version,
        CancellationToken cancellationToken = default)
    {
        var instance = await RequireAsync(instanceId, cancellationToken).ConfigureAwait(false);
        var definition = await definitions.GetAsync(instance.DefinitionId, version, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound($"Agent '{instance.DefinitionId}' version {version} was not found.");
        if (definition.Version == instance.ActiveVersion)
        {
            return instance;
        }

        var updatedAt = time.GetUtcNow();
        await instances.UpdateActiveVersionAsync(instance.InstanceId, definition.Version, updatedAt, cancellationToken)
            .ConfigureAwait(false);
        return instance with { ActiveVersion = definition.Version, UpdatedAt = updatedAt };
    }

    public async ValueTask<AgentInstance> RequireAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        return await instances.FindAsync(instanceId, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Agent instance was not found.");
    }

    public async ValueTask BackfillAsync(CancellationToken cancellationToken = default)
    {
        var missing = await sessions.ListMissingInstanceAsync(cancellationToken).ConfigureAwait(false);
        foreach (var group in missing
            .Where(item => item.AgentInstanceId is null)
            .GroupBy(item => item.Definition.Id, StringComparer.Ordinal))
        {
            var instance = await instances.FindCompatibilityAsync(group.Key, cancellationToken).ConfigureAwait(false)
                ?? await CreateCompatibilityAsync(group, cancellationToken).ConfigureAwait(false);
            foreach (var snapshot in group)
            {
                await sessions.AssignInstanceAsync(
                    snapshot.SessionId,
                    instance.InstanceId,
                    snapshot.PinnedPersona ?? snapshot.Definition.Identity,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<AgentInstance> CreateCompatibilityAsync(
        IEnumerable<SessionSnapshot> sessionsForDefinition,
        CancellationToken cancellationToken)
    {
        var rows = sessionsForDefinition.ToArray();
        var definitionId = rows[0].Definition.Id;
        var highest = rows.Max(item => item.Definition.Version);
        var baseline = rows
            .Where(item => item.Definition.Version == highest)
            .OrderByDescending(item => item.UpdatedAt)
            .ThenBy(item => item.SessionId)
            .First();
        var now = time.GetUtcNow();
        var created = new AgentInstance(
            AgentInstance.CompatibilityFor(definitionId),
            definitionId,
            highest,
            baseline.Definition.Identity,
            AgentInstanceLifecycle.Active,
            now,
            now,
            Compatibility: true);
        try
        {
            await instances.InsertAsync(created, cancellationToken).ConfigureAwait(false);
            return created;
        }
        catch (AgentCoreException ex) when (ex.Code == "Conflict")
        {
            return await instances.FindCompatibilityAsync(definitionId, cancellationToken).ConfigureAwait(false)
                ?? throw new AgentCoreException("Conflict", "Agent instance already exists.", 409);
        }
    }
}
