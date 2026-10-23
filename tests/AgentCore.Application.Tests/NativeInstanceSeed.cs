using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tests;

// Unowned native fixture rows only. Canonical lifecycle authority is exercised through tenant APIs.
internal static class NativeInstanceSeed
{
    internal static async ValueTask<AgentInstance> CreateAsync(IAgentInstanceStore store, IAgentDefinitionStore definitions, IIdGenerator ids, TimeProvider clock, string definitionId, int? version = null)
    {
        var definition = (await definitions.GetAsync(definitionId, version))!; var now = clock.GetUtcNow();
        var instance = new AgentInstance(ids.NewId(), definition.Id, definition.Version, definition.Identity, AgentInstanceLifecycle.Active, now, now);
        await store.InsertAsync(instance, initialSkills: definition.SkillList); return instance;
    }
    internal static async ValueTask<AgentInstance> UpgradeAsync(IAgentInstanceStore store, IAgentDefinitionStore definitions, TimeProvider clock, Guid id, int version, long revision)
    {
        var current = (await store.FindAsync(id))!;
        var target = (await definitions.GetAsync(current.DefinitionId, version))!;
        return await store.UpdateWithExpectedRevisionAsync(new(id, revision, ActiveVersion: version, DefinitionSkills: target.SkillList), clock.GetUtcNow());
    }
    internal static ValueTask<AgentInstance> LifecycleAsync(IAgentInstanceStore store, TimeProvider clock, Guid id, AgentInstanceLifecycle lifecycle, long revision) =>
        store.UpdateWithExpectedRevisionAsync(new(id, revision, Lifecycle: lifecycle), clock.GetUtcNow());
}
