using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Identity;

public sealed class AgentInstanceService(
    IAgentInstanceStore instances,
    IAgentDefinitionStore definitions,
    IIdGenerator ids,
    TimeProvider time,
    ITriggerInstancePolicyReconciliationService? policyReconciliation = null,
    Admin.AdminAgentInstanceService? adminInstances = null) : IAgentInstanceService
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
            now);
        await instances.InsertAsync(created, cancellationToken, definition.SkillList).ConfigureAwait(false);
        return created;
    }

    public async ValueTask<AgentInstance> UpgradeAsync(
        Guid instanceId,
        int version,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        if (adminInstances is not null)
            return await adminInstances.ReassociateActiveVersionAsync(instanceId, version, expectedRevision, cancellationToken);
        var instance = await RequireAsync(instanceId, cancellationToken).ConfigureAwait(false);
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
                new AgentInstanceRevisionUpdate(instance.InstanceId, expectedRevision, ActiveVersion: definition.Version, DefinitionSkills: definition.SkillList),
                updatedAt,
                cancellationToken)
            .ConfigureAwait(false);
        await ReconcileTriggerPolicyAsync(instance.InstanceId, updatedAt, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async ValueTask<AgentInstance> UpdatePersonaAsync(
        Guid instanceId,
        AgentIdentity persona,
        long expectedRevision,
        long expectedPersonaRevision,
        CancellationToken cancellationToken = default)
    {
        _ = await RequireAsync(instanceId, cancellationToken).ConfigureAwait(false);
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

        var instance = await RequireAsync(instanceId, cancellationToken).ConfigureAwait(false);
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
        await ReconcileTriggerPolicyAsync(instanceId, updatedAt, cancellationToken).ConfigureAwait(false);
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
            .Where(item => item.Lifecycle == AgentInstanceLifecycle.Active)
            .OrderBy(item => item.Persona.Name, StringComparer.Ordinal)
            .ThenBy(item => item.InstanceId)
            .ToArray();
    }

    private async ValueTask ReconcileTriggerPolicyAsync(
        Guid agentInstanceId,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        if (policyReconciliation is null)
        {
            return;
        }

        await policyReconciliation.ReconcileAgentInstanceAsync(agentInstanceId, asOfUtc, cancellationToken)
            .ConfigureAwait(false);
    }

}
