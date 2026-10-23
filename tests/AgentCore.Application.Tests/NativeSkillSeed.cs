using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tests;

// Native fixture rows only: exercise shared preparation, reconciliation and store CAS.
// No tenant principal, grant, approval or Run authority is synthesized here.
internal static class NativeSkillSeed
{
    internal static async ValueTask<IReadOnlyList<InstanceSkillView>> ViewsAsync(IAgentInstanceStore store, IAgentDefinitionStore definitions, Guid id)
    {
        var owner = (await store.FindAsync(id))!;
        var definition = (await definitions.GetAsync(owner.DefinitionId, owner.ActiveVersion))!;
        return InstanceSkillRules.Views(definition, await store.ReadSkillsAsync(id))
            .Concat(InstanceSkillRules.BuiltIns(definition, ManagementPolicyResolver.Resolve(definition, owner), owner)).ToArray();
    }
    internal static async ValueTask<InstanceSkillView> InspectAsync(IAgentInstanceStore store, IAgentDefinitionStore definitions, Guid id, string key) =>
        (await ViewsAsync(store, definitions, id)).Single(s => s.Key == key);
    internal static async ValueTask<InstanceSkillView> WriteAsync(IAgentInstanceStore store, IAgentDefinitionStore definitions, TimeProvider clock,
        Guid id, string operation, string? key = null, long? expectedRevision = null, InstanceSkillInput? input = null,
        bool? enabled = null, SkillAuthor actor = SkillAuthor.Admin, AgentDefinition? context = null) =>
        (await PrepareAndStore(store, definitions, clock, id, operation, key, expectedRevision, input, enabled, actor, context)).Skill;
    internal static async ValueTask<InstanceSkillCustomization> CustomizeAsync(IAgentInstanceStore store, IAgentDefinitionStore definitions, TimeProvider clock,
        Guid id, string key, long expectedRevision, SkillAuthor actor)
    {
        var prepared = await PrepareAndStore(store, definitions, clock, id, "customize", key, expectedRevision, null, null, actor, null);
        return new(prepared.Skill, prepared.DefinitionSkill!);
    }
    private static async ValueTask<PreparedInstanceSkill> PrepareAndStore(IAgentInstanceStore store, IAgentDefinitionStore definitions, TimeProvider clock,
        Guid id, string operation, string? key, long? expectedRevision, InstanceSkillInput? input, bool? enabled, SkillAuthor actor, AgentDefinition? context)
    {
        var owner = (await store.FindAsync(id))!;
        var current = (await definitions.GetAsync(owner.DefinitionId, owner.ActiveVersion))!;
        var prepared = InstanceSkillRules.Prepare(owner, context ?? current, await store.ReadSkillsAsync(id), operation, key, expectedRevision, input, enabled, actor, clock.GetUtcNow());
        _ = EffectiveSkillCatalogResolver.Resolve(current, prepared.Next);
        if (prepared.Mutation is not null) await store.MutateSkillsAsync(prepared.Mutation);
        return prepared;
    }
}
