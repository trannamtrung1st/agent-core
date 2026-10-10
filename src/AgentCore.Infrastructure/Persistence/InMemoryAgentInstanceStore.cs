using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Admin;
using AgentCore.Infrastructure.Definitions;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryAgentInstanceStore : IAgentInstanceStore
{
    private static AgentInstance CopyInstance(AgentInstance instance) => instance with { SettingsOverrides = instance.SettingsOverrides?.Copy() };
    public InMemoryCoreEventStore CoreEvents { get; set; } = new();

    private readonly object _gate = new();
    internal object CredentialGate => _gate;
    internal IDisposable HoldConfigurationGeneration(Guid id, long revision)
    {
        Monitor.Enter(_gate);
        if (!_instances.TryGetValue(id, out var owner) || owner.Revision != revision || owner.Lifecycle != AgentInstanceLifecycle.Active)
        {
            Monitor.Exit(_gate);
            throw AgentCoreErrors.Conflict("Instance configuration changed before Run admission. Retry the new activation.");
        }
        return new ConfigurationGateLease(_gate);
    }
    private sealed class ConfigurationGateLease(object gate) : IDisposable { public void Dispose() => Monitor.Exit(gate); }
    private readonly Dictionary<Guid, AgentInstance> _instances = [];

    internal InMemoryTriggerStore? TriggerStore { get; set; }

    internal InMemoryAdminEventStore? EventStore { get; set; }

    internal int CountByDefinition(string definitionId)
    {
        lock (_gate)
        {
            return _instances.Values.Count(item =>
                string.Equals(item.DefinitionId, definitionId, StringComparison.Ordinal));
        }
    }

    internal AgentInstance RemoveForDeletion(Guid instanceId, long expectedRevision)
    {
        lock (_gate)
        {
            if (!_instances.TryGetValue(instanceId, out var instance))
            {
                throw AgentCoreErrors.NotFound("Agent instance was not found.");
            }

            if (instance.Lifecycle != AgentInstanceLifecycle.Archived)
            {
                throw AgentCoreErrors.Validation("Archive this instance before deleting it.");
            }

            if (instance.Revision != expectedRevision)
            {
                throw AgentCoreErrors.Conflict("Agent instance revision is stale.");
            }

            _instances.Remove(instanceId);
            return instance;
        }
    }

    internal void Restore(AgentInstance instance)
    {
        lock (_gate)
        {
            _instances[instance.InstanceId] = CopyInstance(instance);
        }
    }

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
                .Select(CopyInstance).ToArray();
            return ValueTask.FromResult<IReadOnlyList<AgentInstance>>(items);
        }
    }

    public ValueTask<IReadOnlyList<AgentInstance>> ListMaintenancePageAsync(Guid? afterId, int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (_gate) return ValueTask.FromResult<IReadOnlyList<AgentInstance>>(_instances.Values
            .Where(r => afterId is null || string.CompareOrdinal(r.InstanceId.ToString("D"), afterId.Value.ToString("D")) > 0)
            .OrderBy(r => r.InstanceId.ToString("D"), StringComparer.Ordinal).Take(limit).Select(CopyInstance).ToArray());
    }

    public ValueTask<AgentInstance?> FindAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_instances.TryGetValue(instanceId, out var instance) ? CopyInstance(instance) : null);
        }
    }

    public ValueTask InsertAsync(AgentInstance instance, CancellationToken cancellationToken = default, IReadOnlyList<SkillSpec>? initialSkills = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_instances.ContainsKey(instance.InstanceId))
            {
                throw new AgentCoreException("Conflict", "Agent instance already exists.", 409);
            }

            ValidateSkillTransition(instance.InstanceId, initialSkills);
            _instances[instance.InstanceId] = CopyInstance(instance);
            InitializeSkills(instance.InstanceId, initialSkills, instance.UpdatedAt);
            return ValueTask.CompletedTask;
        }
    }

    public ValueTask<AgentInstance> InsertManagedWithHistoryAsync(
        AgentInstance instance,
        AdminEventAppend historyAppend,
        CancellationToken cancellationToken = default, IReadOnlyList<SkillSpec>? initialSkills = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (EventStore is null)
        {
            throw AgentCoreErrors.Validation("Admin managed instance history is not available.");
        }

        var operationGate = AdminOperationLockRegistry.For(historyAppend.OperationId);
        lock (operationGate)
        {
            var existingEvent = EventStore.TryGetByOperationIdAsync(historyAppend.OperationId)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            if (existingEvent is not null)
            {
                lock (_gate)
                {
                    return ValueTask.FromResult(
                        ResolveManagedInstanceFromEvent(existingEvent, historyAppend, instance));
                }
            }

            lock (_gate)
            {
                if (_instances.ContainsKey(instance.InstanceId))
                {
                    throw new AgentCoreException("Conflict", "Agent instance already exists.", 409);
                }

                ValidateSkillTransition(instance.InstanceId, initialSkills);
                _instances[instance.InstanceId] = CopyInstance(instance);
                try
                {
                    EventStore.AppendWithinLock(historyAppend);
                }
                catch
                {
                    _instances.Remove(instance.InstanceId);
                    throw;
                }

                InitializeSkills(instance.InstanceId, initialSkills, instance.UpdatedAt);
                return ValueTask.FromResult(instance);
            }
        }
    }

    private AgentInstance ResolveManagedInstanceFromEvent(
        AdminEvent existingEvent,
        AdminEventAppend historyAppend,
        AgentInstance instance)
    {
        if (existingEvent.Operation != AdminEventOperationKind.ManagedInstanceCreated)
        {
            throw AgentCoreErrors.Conflict("Operation id is already used for a different admin event.");
        }

        EnsureAgentInstanceHistoryTargetMatches(existingEvent, historyAppend, instance.InstanceId);

        if (!Guid.TryParse(existingEvent.TargetId, out var instanceId)
            || !_instances.TryGetValue(instanceId, out var existing))
        {
            throw AgentCoreErrors.Conflict("Managed instance history references a missing instance.");
        }

        return existing;
    }

    public ValueTask<AgentInstance> UpdateActiveVersionWithHistoryAsync(
        AgentInstanceRevisionUpdate update,
        DateTimeOffset updatedAt,
        AdminEventAppend historyAppend,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (EventStore is null)
        {
            throw AgentCoreErrors.Validation("Admin managed instance history is not available.");
        }

        var operationGate = AdminOperationLockRegistry.For(historyAppend.OperationId);
        lock (operationGate)
        {
            var existingEvent = EventStore.TryGetByOperationIdAsync(historyAppend.OperationId)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            if (existingEvent is not null)
            {
                lock (_gate)
                {
                    return ValueTask.FromResult(
                        ResolveInstanceDefinitionVersionChangedFromEvent(
                            existingEvent,
                            historyAppend,
                            update));
                }
            }

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

                if (update.ActiveVersion is not int activeVersion)
                {
                    throw AgentCoreErrors.Validation("Active version is required.");
                }

                ValidateSkillTransition(update.InstanceId, update.DefinitionSkills);
                var previous = instance;
                var next = instance with
                {
                    ActiveVersion = activeVersion,
                    HarnessManagement = update.HarnessManagement ?? instance.HarnessManagement,
                ExecutionBudgets = update.SetExecutionBudgets ? update.ExecutionBudgets : instance.ExecutionBudgets,
                SettingsOverrides = (update.SetSettingsOverrides ? update.SettingsOverrides : instance.SettingsOverrides)?.Copy(),
                    UpdatedAt = updatedAt,
                    Revision = instance.Revision + 1
                };
                void CommitOwner()
                {
                _instances[update.InstanceId] = next;
                try
                {
                    EventStore.AppendWithinLock(historyAppend);
                }
                catch
                {
                    _instances[update.InstanceId] = previous;
                    throw;
                }

                InitializeSkills(update.InstanceId, update.DefinitionSkills, updatedAt);
                foreach (var e in CoreEventPersistence.Instance(previous, next)) CoreEvents.Append(e);
                }
                if (TriggerStore is not null) TriggerStore.CommitConfigurationPolicy(update.InstanceId, update.AutomationPolicyChanges, updatedAt, CommitOwner);
                else if (update.AutomationPolicyChanges?.Count > 0) throw AgentCoreErrors.Persistence("Atomic Automation store is unavailable.");
                else CommitOwner();
                return ValueTask.FromResult(CopyInstance(next));
            }
        }
    }

    public ValueTask<AgentInstance> UpdatePersonaWithHistoryAsync(
        AgentInstanceRevisionUpdate update,
        DateTimeOffset updatedAt,
        AdminEventAppend historyAppend,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (EventStore is null)
        {
            throw AgentCoreErrors.Validation("Admin managed instance history is not available.");
        }

        var operationGate = AdminOperationLockRegistry.For(historyAppend.OperationId);
        lock (operationGate)
        {
            var existingEvent = EventStore.TryGetByOperationIdAsync(historyAppend.OperationId)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            if (existingEvent is not null)
            {
                lock (_gate)
                {
                    return ValueTask.FromResult(
                        ResolvePersonaChangedFromEvent(existingEvent, historyAppend, update));
                }
            }

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

                if (update.Persona is not AgentIdentity persona)
                {
                    throw AgentCoreErrors.Validation("Persona is required.");
                }

                if (update.ExpectedPersonaRevision is not long expectedPersonaRevision)
                {
                    throw new AgentCoreException(
                        "Validation",
                        "Expected persona revision is required for persona edits.",
                        400);
                }

                if (expectedPersonaRevision != instance.PersonaRevision)
                {
                    throw new AgentCoreException("Conflict", "Agent instance persona revision is stale.", 409);
                }

                ValidateSkillTransition(update.InstanceId, update.DefinitionSkills);
                var previous = instance;
                var next = instance with
                {
                    Persona = persona,
                    PersonaRevision = instance.PersonaRevision + 1,
                    UpdatedAt = updatedAt,
                    Revision = instance.Revision + 1
                };
                _instances[update.InstanceId] = next;
                try
                {
                    EventStore.AppendWithinLock(historyAppend);
                }
                catch
                {
                    _instances[update.InstanceId] = previous;
                    throw;
                }

                InitializeSkills(update.InstanceId, update.DefinitionSkills, updatedAt);
                foreach (var e in CoreEventPersistence.Instance(previous, next)) CoreEvents.Append(e);
                return ValueTask.FromResult(CopyInstance(next));
            }
        }
    }

    private AgentInstance ResolvePersonaChangedFromEvent(
        AdminEvent existingEvent,
        AdminEventAppend historyAppend,
        AgentInstanceRevisionUpdate update)
    {
        if (existingEvent.Operation != AdminEventOperationKind.PersonaChanged)
        {
            throw AgentCoreErrors.Conflict("Operation id is already used for a different admin event.");
        }

        EnsureAgentInstanceHistoryTargetMatches(existingEvent, historyAppend, update.InstanceId);
        AdminEventReplayPolicy.EnsurePersonaChangedReplayMatches(existingEvent, historyAppend, update);

        if (!Guid.TryParse(existingEvent.TargetId, out var targetId)
            || targetId != update.InstanceId
            || !_instances.TryGetValue(update.InstanceId, out var existing))
        {
            throw AgentCoreErrors.Conflict("Managed instance history references a missing instance.");
        }

        return existing;
    }

    public ValueTask<AgentInstance> UpdateLifecycleWithHistoryAsync(
        AgentInstanceRevisionUpdate update,
        DateTimeOffset updatedAt,
        AdminEventAppend historyAppend,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (EventStore is null)
        {
            throw AgentCoreErrors.Validation("Admin managed instance history is not available.");
        }

        var operationGate = AdminOperationLockRegistry.For(historyAppend.OperationId);
        lock (operationGate)
        {
            var existingEvent = EventStore.TryGetByOperationIdAsync(historyAppend.OperationId)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            if (existingEvent is not null)
            {
                lock (_gate)
                {
                    return ValueTask.FromResult(
                        ResolveInstanceLifecycleChangedFromEvent(existingEvent, historyAppend, update));
                }
            }

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

                if (update.Lifecycle is not AgentInstanceLifecycle lifecycle)
                {
                    throw AgentCoreErrors.Validation("Lifecycle is required.");
                }

                ValidateSkillTransition(update.InstanceId, update.DefinitionSkills);
                var previous = instance;
                var next = instance with
                {
                    Lifecycle = lifecycle,
                    UpdatedAt = updatedAt,
                    Revision = instance.Revision + 1
                };
                _instances[update.InstanceId] = next;
                try
                {
                    EventStore.AppendWithinLock(historyAppend);
                }
                catch
                {
                    _instances[update.InstanceId] = previous;
                    throw;
                }

                InitializeSkills(update.InstanceId, update.DefinitionSkills, updatedAt);
                foreach (var e in CoreEventPersistence.Instance(previous, next)) CoreEvents.Append(e);
                return ValueTask.FromResult(CopyInstance(next));
            }
        }
    }

    private AgentInstance ResolveInstanceLifecycleChangedFromEvent(
        AdminEvent existingEvent,
        AdminEventAppend historyAppend,
        AgentInstanceRevisionUpdate update)
    {
        if (existingEvent.Operation is not (AdminEventOperationKind.InstanceArchived or AdminEventOperationKind.InstanceUnarchived))
        {
            throw AgentCoreErrors.Conflict("Operation id is already used for a different admin event.");
        }

        EnsureAgentInstanceHistoryTargetMatches(existingEvent, historyAppend, update.InstanceId);
        AdminEventReplayPolicy.EnsureInstanceLifecycleReplayMatches(existingEvent, historyAppend, update);

        if (!Guid.TryParse(existingEvent.TargetId, out var targetId)
            || targetId != update.InstanceId
            || !_instances.TryGetValue(update.InstanceId, out var existing))
        {
            throw AgentCoreErrors.Conflict("Managed instance history references a missing instance.");
        }

        return existing;
    }

    private AgentInstance ResolveInstanceDefinitionVersionChangedFromEvent(
        AdminEvent existingEvent,
        AdminEventAppend historyAppend,
        AgentInstanceRevisionUpdate update)
    {
        if (existingEvent.Operation != AdminEventOperationKind.InstanceDefinitionVersionChanged)
        {
            throw AgentCoreErrors.Conflict("Operation id is already used for a different admin event.");
        }

        EnsureAgentInstanceHistoryTargetMatches(existingEvent, historyAppend, update.InstanceId);
        AdminEventReplayPolicy.EnsureInstanceDefinitionVersionChangedReplayMatches(
            existingEvent,
            historyAppend,
            update);

        if (!Guid.TryParse(existingEvent.TargetId, out var targetId)
            || targetId != update.InstanceId
            || !_instances.TryGetValue(update.InstanceId, out var existing))
        {
            throw AgentCoreErrors.Conflict("Managed instance history references a missing instance.");
        }

        return existing;
    }

    private static void EnsureAgentInstanceHistoryTargetMatches(
        AdminEvent existingEvent,
        AdminEventAppend historyAppend,
        Guid instanceId)
    {
        var expectedTargetId = instanceId.ToString("D");
        if (!string.Equals(historyAppend.TargetId, expectedTargetId, StringComparison.Ordinal)
            || !string.Equals(existingEvent.TargetId, expectedTargetId, StringComparison.Ordinal))
        {
            throw AgentCoreErrors.Conflict("Managed instance history target does not match the retried command.");
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
            foreach (var e in CoreEventPersistence.Instance(instance, _instances[instanceId])) CoreEvents.Append(e);
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

            ValidateSkillTransition(update.InstanceId, update.DefinitionSkills);
            var next = instance with
            {
                ActiveVersion = update.ActiveVersion ?? instance.ActiveVersion,
                Persona = persona,
                Lifecycle = update.Lifecycle ?? instance.Lifecycle,
                UpdatedAt = updatedAt,
                Revision = instance.Revision + 1,
                PersonaRevision = personaRevision,
                HarnessManagement = update.HarnessManagement ?? instance.HarnessManagement,
                ExecutionBudgets = update.SetExecutionBudgets ? update.ExecutionBudgets : instance.ExecutionBudgets,
                SettingsOverrides = (update.SetSettingsOverrides ? update.SettingsOverrides : instance.SettingsOverrides)?.Copy(),
                UnattendedModelCatalogKey = update.SetUnattendedModel
                    ? NullIfBlank(update.UnattendedModelCatalogKey)
                    : instance.UnattendedModelCatalogKey,
                UnattendedReasoningEffort = update.SetUnattendedModel
                    ? NullIfBlank(update.UnattendedReasoningEffort)
                    : instance.UnattendedReasoningEffort
            };
            void CommitOwner()
            {
            if (update.History is not null)
            {
                if (EventStore is null) throw AgentCoreErrors.Persistence("Admin history is unavailable.");
                EventStore.AppendWithinLock(update.History);
            }
            InitializeSkills(update.InstanceId, update.DefinitionSkills, updatedAt);
            _instances[update.InstanceId] = next;
            foreach (var e in CoreEventPersistence.Instance(instance, next)) CoreEvents.Append(e);
            }
            if (TriggerStore is not null) TriggerStore.CommitConfigurationPolicy(update.InstanceId, update.AutomationPolicyChanges, updatedAt, CommitOwner);
            else if (update.AutomationPolicyChanges?.Count > 0) throw AgentCoreErrors.Persistence("Atomic Automation store is unavailable.");
            else CommitOwner();
            return ValueTask.FromResult(CopyInstance(next));
        }
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
