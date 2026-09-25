using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Identity;

public sealed class AgentInstanceService(
    IAgentInstanceStore instances,
    IAgentDefinitionStore definitions,
    IMemoryStore sessions,
    IIdGenerator ids,
    TimeProvider time,
    ITriggerPolicyRecoveryService? policyRecovery = null) : IAgentInstanceService
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
            return await ForwardAlignCompatibilityAsync(existing, definition, cancellationToken).ConfigureAwait(false);
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
            var reloaded = await instances.FindCompatibilityAsync(definition.Id, cancellationToken).ConfigureAwait(false)
                ?? throw new AgentCoreException("Conflict", "Agent instance already exists.", 409);
            return await ForwardAlignCompatibilityAsync(reloaded, definition, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<AgentInstance> UpgradeAsync(
        Guid instanceId,
        int version,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var instance = await RequireManagedAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance.Revision != expectedRevision)
        {
            throw AgentCoreErrors.Conflict("Agent instance revision is stale.");
        }

        var definition = await definitions.GetAsync(instance.DefinitionId, version, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound($"Agent '{instance.DefinitionId}' version {version} was not found.");
        if (definition.Version == instance.ActiveVersion)
        {
            return instance;
        }

        var updatedAt = time.GetUtcNow();
        var updated = await instances.UpdateWithExpectedRevisionAsync(
                new AgentInstanceRevisionUpdate(instance.InstanceId, expectedRevision, ActiveVersion: definition.Version),
                updatedAt,
                cancellationToken)
            .ConfigureAwait(false);
        await NotifySchedulingEligibilityMayHaveImprovedAsync(instance.InstanceId, updatedAt, cancellationToken)
            .ConfigureAwait(false);
        return updated;
    }

    public async ValueTask<AgentInstance> UpdatePersonaAsync(
        Guid instanceId,
        AgentIdentity persona,
        long expectedRevision,
        long expectedPersonaRevision,
        CancellationToken cancellationToken = default)
    {
        _ = await RequireManagedAsync(instanceId, cancellationToken).ConfigureAwait(false);
        var updatedAt = time.GetUtcNow();
        return await instances.UpdateWithExpectedRevisionAsync(
                new AgentInstanceRevisionUpdate(
                    instanceId,
                    expectedRevision,
                    Persona: persona,
                    ExpectedPersonaRevision: expectedPersonaRevision),
                updatedAt,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<AgentInstance> SetLifecycleAsync(
        Guid instanceId,
        AgentInstanceLifecycle lifecycle,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        if (lifecycle is not (AgentInstanceLifecycle.Active or AgentInstanceLifecycle.Archived))
        {
            throw AgentCoreErrors.Validation("lifecycle must be Active or Archived.");
        }

        var instance = await RequireManagedAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance.Revision != expectedRevision)
        {
            throw AgentCoreErrors.Conflict("Agent instance revision is stale.");
        }

        if (instance.Lifecycle == lifecycle)
        {
            return instance;
        }

        var updatedAt = time.GetUtcNow();
        var updated = await instances.UpdateWithExpectedRevisionAsync(
                new AgentInstanceRevisionUpdate(instanceId, expectedRevision, Lifecycle: lifecycle),
                updatedAt,
                cancellationToken)
            .ConfigureAwait(false);
        if (lifecycle == AgentInstanceLifecycle.Active)
        {
            await NotifySchedulingEligibilityMayHaveImprovedAsync(instanceId, updatedAt, cancellationToken)
                .ConfigureAwait(false);
        }

        return updated;
    }

    public async ValueTask<AgentInstance> RequireAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        return await instances.FindAsync(instanceId, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Agent instance was not found.");
    }

    public async ValueTask<IReadOnlyList<AgentInstance>> ListChatEligibleAsync(
        CancellationToken cancellationToken = default)
    {
        const int limit = 256;
        var rows = await instances.ListAsync(limit, cancellationToken).ConfigureAwait(false);
        return rows
            .Where(item => !item.Compatibility && item.Lifecycle == AgentInstanceLifecycle.Active)
            .OrderBy(item => item.Persona.Name, StringComparer.Ordinal)
            .ThenBy(item => item.InstanceId)
            .ToArray();
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

    private async ValueTask<AgentInstance> ForwardAlignCompatibilityAsync(
        AgentInstance existing,
        AgentDefinition definition,
        CancellationToken cancellationToken)
    {
        var current = existing;
        if (definition.Version <= current.ActiveVersion)
        {
            return current;
        }

        var baselineVersion = current.ActiveVersion;
        while (current.ActiveVersion < definition.Version)
        {
            var updatedAt = time.GetUtcNow();
            try
            {
                await instances.UpdateActiveVersionAsync(
                        current.InstanceId,
                        definition.Version,
                        updatedAt,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (AgentCoreException ex) when (ex.Code == "Conflict")
            {
            }

            current = await instances.FindAsync(current.InstanceId, cancellationToken).ConfigureAwait(false)
                ?? throw AgentCoreErrors.NotFound("Agent instance was not found.");
        }

        if (current.ActiveVersion > baselineVersion)
        {
            await NotifySchedulingEligibilityMayHaveImprovedAsync(
                    current.InstanceId,
                    current.UpdatedAt,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return current;
    }

    private async ValueTask NotifySchedulingEligibilityMayHaveImprovedAsync(
        Guid agentInstanceId,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        if (policyRecovery is null)
        {
            return;
        }

        await policyRecovery.ReactivateSuspendedForAgentInstanceAsync(agentInstanceId, asOfUtc, cancellationToken)
            .ConfigureAwait(false);
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

    private async ValueTask<AgentInstance> RequireManagedAsync(
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        var instance = await RequireAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (instance.Compatibility)
        {
            throw AgentCoreErrors.Validation("Compatibility instances cannot be mutated through the managed API.");
        }

        return instance;
    }
}
