using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
namespace AgentCore.Application.Agents;

public sealed class EffectiveSkillCatalogResolver(IAgentInstanceStore instances)
{
    public async ValueTask<IReadOnlyList<EffectiveSkill>> ResolveAsync(Guid instanceId, AgentDefinition definition, CancellationToken ct = default)
    {
        var state = await instances.ReadSkillsAsync(instanceId, ct);
        return Resolve(definition, state);
    }
    public static IReadOnlyList<EffectiveSkill> Resolve(AgentDefinition definition, InstanceSkillSnapshot state)
    {
        var entries = new List<EffectiveSkill>();
        foreach (var s in definition.SkillList)
        {
            var enabled = state.DefinitionStates.SingleOrDefault(x => x.DefinitionSkillId == s.Id)
                ?? throw AgentCoreErrors.Persistence($"Definition Skill state '{s.Id}' was not initialized.");
            if (enabled.EnabledOverride ?? s.DefaultEnabled) entries.Add(new("definition:" + s.Id, SkillOrigin.Definition, s.Id, s.Name,
                s.Description, s.Procedure, s.Projection, s.RequiredCapabilities.ToArray(), s.ResourcePaths.ToArray()));
        }
        entries.AddRange(state.InstanceSkills.Where(s => s.Enabled).OrderBy(s => s.SkillId).Select(s =>
            new EffectiveSkill("instance:" + s.SkillId, SkillOrigin.Instance, s.SkillId, s.Name,
                s.Description, s.Procedure, s.Projection, s.RequiredCapabilities.ToArray(), [])));
        if (entries.Where(s => s.Projection == SkillProjection.Always).Sum(s => s.Procedure.Length) > SkillPolicy.MaxActiveProcedureCharacters)
            throw AgentCoreErrors.Validation("Enabled Always Skills exceed the 8000-character procedure context budget.");
        return SkillPolicy.FreezeCatalog(entries);
    }
}
