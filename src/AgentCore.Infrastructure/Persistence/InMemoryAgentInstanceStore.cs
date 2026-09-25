using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Infrastructure.Persistence;

public sealed class InMemoryAgentInstanceStore : IAgentInstanceStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, AgentInstance> _instances = [];

    public ValueTask<IReadOnlyList<AgentInstance>> ListAsync(int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        lock (_gate)
        {
            var items = _instances.Values
                .OrderBy(item => item.DefinitionId, StringComparer.Ordinal)
                .ThenBy(item => item.InstanceId)
                .Take(limit)
                .ToArray();
            return ValueTask.FromResult<IReadOnlyList<AgentInstance>>(items);
        }
    }

    public ValueTask<AgentInstance?> FindAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_instances.TryGetValue(instanceId, out var instance) ? instance : null);
        }
    }

    public ValueTask<AgentInstance?> FindCompatibilityAsync(string definitionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var found = _instances.Values.FirstOrDefault(item =>
                item.Compatibility && string.Equals(item.DefinitionId, definitionId, StringComparison.Ordinal));
            return ValueTask.FromResult(found);
        }
    }

    public ValueTask InsertAsync(AgentInstance instance, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_instances.ContainsKey(instance.InstanceId)
                || (instance.Compatibility && _instances.Values.Any(item =>
                    item.Compatibility && string.Equals(item.DefinitionId, instance.DefinitionId, StringComparison.Ordinal))))
            {
                throw new AgentCoreException("Conflict", "Agent instance already exists.", 409);
            }

            _instances[instance.InstanceId] = instance;
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask UpdateActiveVersionAsync(
        Guid instanceId,
        int activeVersion,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_instances.TryGetValue(instanceId, out var instance))
            {
                throw AgentCoreErrors.NotFound("Agent instance was not found.");
            }

            if (activeVersion <= instance.ActiveVersion)
            {
                return ValueTask.CompletedTask;
            }

            _instances[instanceId] = instance with
            {
                ActiveVersion = activeVersion,
                UpdatedAt = updatedAt,
                Revision = instance.Revision + 1
            };
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask<AgentInstance> UpdateWithExpectedRevisionAsync(
        AgentInstanceRevisionUpdate update,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_instances.TryGetValue(update.InstanceId, out var instance))
            {
                throw AgentCoreErrors.NotFound("Agent instance was not found.");
            }

            if (instance.Revision != update.ExpectedRevision)
            {
                throw new AgentCoreException("Conflict", "Agent instance revision is stale.", 409);
            }

            if (instance.Compatibility
                && (update.Persona is not null || update.Lifecycle is not null))
            {
                throw new AgentCoreException("Validation", "Compatibility instances cannot change persona or lifecycle.", 400);
            }

            var persona = update.Persona ?? instance.Persona;
            var personaRevision = instance.PersonaRevision;
            var personaChanging = update.Persona is not null && !update.Persona.Equals(instance.Persona);
            if (personaChanging)
            {
                if (update.ExpectedPersonaRevision is null)
                {
                    throw new AgentCoreException(
                        "Validation",
                        "Expected persona revision is required for persona edits.",
                        400);
                }

                if (update.ExpectedPersonaRevision != instance.PersonaRevision)
                {
                    throw new AgentCoreException("Conflict", "Agent instance persona revision is stale.", 409);
                }

                personaRevision++;
            }

            var next = instance with
            {
                ActiveVersion = update.ActiveVersion ?? instance.ActiveVersion,
                Persona = persona,
                Lifecycle = update.Lifecycle ?? instance.Lifecycle,
                UpdatedAt = updatedAt,
                Revision = instance.Revision + 1,
                PersonaRevision = personaRevision
            };
            _instances[update.InstanceId] = next;
            return ValueTask.FromResult(next);
        }
    }
}
