using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Execution;

public sealed record ResolvedAgentRunConfiguration(AgentRunConfiguration Configuration, AgentIdentity Persona,
    IReadOnlyList<EffectiveSkill> Skills, ExecutionBudgetPolicy? InstanceBudgets);

public sealed class AgentRunConfigurationResolver(IAgentInstanceStore instances, IAgentDefinitionStore definitions,
    IAgentDefinitionResourceAdminStore resources)
{
    public async ValueTask<ResolvedAgentRunConfiguration> ResolveAsync(Guid id, CancellationToken ct = default, bool allowArchived = false)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var owner = await instances.FindAsync(id, ct) ?? throw AgentCoreErrors.NotFound("Agent Instance was not found.");
            if (!allowArchived && owner.Lifecycle != AgentInstanceLifecycle.Active) throw AgentCoreErrors.Forbidden("Agent Instance is archived.");
            var baseline = await definitions.GetAsync(owner.DefinitionId, owner.ActiveVersion, ct) ?? throw AgentCoreErrors.Persistence("Selected Definition is unavailable.");
            var skillState = await instances.ReadSkillsAsync(id, ct);
            var resourceState = await instances.ReadResourcesAsync(id, ct);
            var published = await resources.ListPublicationResourcesAsync(baseline.Id, baseline.Version, ct);
            var current = await instances.FindAsync(id, ct);
            if (current?.Revision != owner.Revision) continue;
            var effective = InstanceSettingsResolver.Resolve(baseline, owner.SettingsOverrides);
            var manifest = AgentInstanceResourceService.Resolve(effective, published, resourceState, skillState);
            var environment = RoleEnvironments.Of(effective);
            // A disabled inherited file removes its knowledge binding; a local knowledge file adds an origin-qualified one.
            var disabledPaths = published.Where(p => !manifest.Any(r => r.Key == "definition:" + p.ResourceId.ToString("D"))).Select(p => p.LogicalPath).ToHashSet(StringComparer.Ordinal);
            var knowledge = environment.KnowledgeList.Where(k => !disabledPaths.Contains(KnowledgeSourcePaths.ResolveBackingPath(k)))
                .Concat(manifest.Where(r => r.Key.StartsWith("instance:", StringComparison.Ordinal) && r.Kind == AgentDefinitionResourceKind.Knowledge)
                    .Select(r => new KnowledgeSourceRef(r.Key, r.LogicalPath, r.VirtualPath, r.LogicalPath))).ToArray();
            effective = effective with { Environment = environment with { KnowledgeSources = knowledge } };
            return new(new(effective, owner.Revision, owner.PersonaRevision, manifest), owner.Persona,
                EffectiveSkillCatalogResolver.Resolve(effective, skillState), owner.ExecutionBudgets);
        }
        throw AgentCoreErrors.Conflict("Instance changed while resolving configuration. Retry admission.");
    }
}
