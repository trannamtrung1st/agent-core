using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
namespace AgentCore.Infrastructure.Persistence;
public sealed partial class InMemoryAgentInstanceStore
{
    private readonly Dictionary<Guid, Dictionary<string, AgentDefinitionSkillState>> _definitionSkillStates = [];
    private readonly Dictionary<Guid, Dictionary<string, AgentInstanceSkill>> _instanceSkills = [];
    private void ValidateSkillTransition(Guid id, IReadOnlyList<SkillSpec>? skills)
    {
        if (skills is null) return;
        var states = _definitionSkillStates.GetValueOrDefault(id);
        try { SkillPolicy.ValidateAlwaysBudget(skills.Select(s => (s.Projection, states?.GetValueOrDefault(s.Id)?.EnabledOverride ?? s.DefaultEnabled, s.Procedure))
            .Concat(((IEnumerable<AgentInstanceSkill>?)_instanceSkills.GetValueOrDefault(id)?.Values ?? []).Select(s => (s.Projection, s.Enabled, s.Procedure)))); }
        catch (ArgumentException e) { throw AgentCoreErrors.Validation(e.Message); }
    }
    private void InitializeSkills(Guid id, IReadOnlyList<SkillSpec>? skills, DateTimeOffset at)
    {
        if (skills is null) return;
        if (!_definitionSkillStates.TryGetValue(id, out var states)) _definitionSkillStates[id] = states = [];
        foreach (var s in skills) states.TryAdd(s.Id, new(id, s.Id, null, 1, at));
    }
    internal void PurgeSkills(Guid instanceId)
    {
        lock (_gate) { _definitionSkillStates.Remove(instanceId); _instanceSkills.Remove(instanceId); _resources.Remove(instanceId); _resourceStates.Remove(instanceId); }
    }
    public ValueTask<InstanceSkillSnapshot> ReadSkillsAsync(Guid instanceId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) return ValueTask.FromResult(new InstanceSkillSnapshot(
            _definitionSkillStates.GetValueOrDefault(instanceId)?.Values.ToArray() ?? [],
            _instanceSkills.GetValueOrDefault(instanceId)?.Values.ToArray() ?? []));
    }
    public ValueTask MutateSkillsAsync(SkillMutation m, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var owner = _instances.GetValueOrDefault(m.InstanceId) ?? throw AgentCoreErrors.NotFound("Agent instance was not found.");
            if (owner.Lifecycle != AgentInstanceLifecycle.Active) throw AgentCoreErrors.Validation("Archived instances cannot change Skills.");
            if (owner.Revision != m.ExpectedInstanceRevision) throw AgentCoreErrors.Conflict("Agent instance revision is stale.");
            var states = _definitionSkillStates.GetValueOrDefault(m.InstanceId) ?? [];
            var skills = _instanceSkills.GetValueOrDefault(m.InstanceId) ?? [];
            if (m.DefinitionState is { } state && states.GetValueOrDefault(state.DefinitionSkillId)?.Revision != m.ExpectedStateRevision)
                throw AgentCoreErrors.Conflict("Definition Skill state revision is stale.");
            var skillId = m.DeleteSkillId ?? m.InstanceSkill?.SkillId;
            if (skillId is string id && skills.GetValueOrDefault(id)?.Revision != m.ExpectedSkillRevision)
                throw AgentCoreErrors.Conflict("Instance Skill revision is stale.");
            if (m.History is not null) { if (EventStore is null) throw AgentCoreErrors.Persistence("Admin history is unavailable."); EventStore.AppendWithinLock(m.History); }
            if (m.DefinitionState is { } ds) states[ds.DefinitionSkillId] = ds;
            if (m.InstanceSkill is { } local) skills[local.SkillId] = local with { RequiredCapabilities = Array.AsReadOnly(local.RequiredCapabilities.ToArray()) };
            if (m.DeleteSkillId is string deleted) skills.Remove(deleted);
            _definitionSkillStates[m.InstanceId] = states;
            _instanceSkills[m.InstanceId] = skills;
            _instances[m.InstanceId] = owner with { Revision = owner.Revision + 1 };
            return ValueTask.CompletedTask;
        }
    }
}
