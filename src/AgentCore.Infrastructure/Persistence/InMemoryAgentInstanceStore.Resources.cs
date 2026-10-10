using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryAgentInstanceStore
{
    private readonly Dictionary<Guid, Dictionary<Guid, AgentInstanceResource>> _resources = [];
    private readonly Dictionary<Guid, Dictionary<Guid, AgentDefinitionResourceState>> _resourceStates = [];

    public ValueTask<InstanceResourceSnapshot> ReadResourcesAsync(Guid id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) return ValueTask.FromResult(new InstanceResourceSnapshot(
            _resourceStates.GetValueOrDefault(id)?.Values.ToArray() ?? [], _resources.GetValueOrDefault(id)?.Values.ToArray() ?? []));
    }
    public ValueTask MutateResourcesAsync(InstanceResourceMutation m, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var owner = _instances.GetValueOrDefault(m.InstanceId) ?? throw AgentCoreErrors.NotFound("Agent Instance was not found.");
            if (owner.Lifecycle != AgentInstanceLifecycle.Active) throw AgentCoreErrors.Validation("Archived instances are read-only.");
            if (owner.Revision != m.ExpectedInstanceRevision) throw AgentCoreErrors.Conflict("Instance revision is stale.");
            var resources = new Dictionary<Guid, AgentInstanceResource>(_resources.GetValueOrDefault(m.InstanceId) ?? []);
            var states = new Dictionary<Guid, AgentDefinitionResourceState>(_resourceStates.GetValueOrDefault(m.InstanceId) ?? []);
            var resourceId = m.DeleteResourceId ?? m.Resource?.ResourceId;
            if (resourceId is Guid id && resources.GetValueOrDefault(id)?.Revision != m.ExpectedResourceRevision)
                throw AgentCoreErrors.Conflict("Resource revision is stale.");
            if (m.Resource is { } resource)
            {
                if (resource.InstanceId != m.InstanceId || resource.ResourceId == Guid.Empty) throw AgentCoreErrors.Validation("Resource owner/identity is invalid.");
                resources[resource.ResourceId] = resource;
            }
            if (m.DeleteResourceId is Guid deleted && !resources.Remove(deleted)) throw AgentCoreErrors.NotFound("Instance resource was not found.");
            ValidateResourceCollection(resources.Values);
            if (m.DefinitionState is { } state)
            {
                if (state.InstanceId != m.InstanceId || state.ResourceId == Guid.Empty) throw AgentCoreErrors.Validation("Resource state owner/identity is invalid.");
                if (states.GetValueOrDefault(state.ResourceId)?.Revision != m.ExpectedStateRevision) throw AgentCoreErrors.Conflict("Resource state revision is stale.");
                states[state.ResourceId] = state;
            }
            if (m.History is not null) { if (EventStore is null) throw AgentCoreErrors.Persistence("Admin history is unavailable."); EventStore.AppendWithinLock(m.History); }
            _resources[m.InstanceId] = resources; _resourceStates[m.InstanceId] = states;
            _instances[m.InstanceId] = owner with { Revision = owner.Revision + 1, UpdatedAt = m.History?.OccurredAt ?? owner.UpdatedAt, HarnessManagement = m.HarnessManagement ?? owner.HarnessManagement };
        }
        return ValueTask.CompletedTask;
    }
    internal static void ValidateResourceCollection(IEnumerable<AgentInstanceResource> resources)
    {
        var items = resources.ToArray();
        if (items.Length > AgentResourceLimits.MaxItemsPerDraft || items.Sum(r => r.ByteLength) > AgentResourceLimits.MaxAggregateBytes)
            throw AgentCoreErrors.Validation("Instance resource quota exceeded.");
        if (items.Select(r => r.LogicalPath).Distinct(StringComparer.Ordinal).Count() != items.Length)
            throw AgentCoreErrors.Conflict("Resource logical path is already occupied in this Instance.");
    }
}
