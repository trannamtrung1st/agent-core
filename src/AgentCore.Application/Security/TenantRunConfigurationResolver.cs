using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Security;

public sealed partial class TenantRunConfigurationResolver(ITenantAgentStore agents, ITenantDefinitionStore definitions,
    IBuiltInAgentDefinitionStore builtIns, InheritedDefinitionResourceCatalog inheritedResources, ITenantCredentialBindingStore bindings,
    ITenantSandboxProfileStore? sandboxProfiles = null, ISandboxImageAuthority? sandboxImages = null, ISandboxReadiness? sandboxReadiness = null, IamAuthorizer? authorizer = null)
{
    public async ValueTask<(TenantAgent Agent, AgentDefinition Definition)> BaselineAsync(Guid tenant, Guid instance, CancellationToken ct)
    {
        var agent = await agents.FindTenantAgentAsync(tenant, instance, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var owner = agent.Instance;
        var baseline = Guid.TryParse(owner.DefinitionId, out var owned)
            ? (await definitions.FindDefinitionPublicationAsync(tenant, owned, owner.ActiveVersion, ct).ConfigureAwait(false))?.Payload
            : await builtIns.GetAsync(owner.DefinitionId, owner.ActiveVersion, ct).ConfigureAwait(false);
        return (agent, baseline ?? throw new AccessDeniedException("definition_unavailable"));
    }
    public async ValueTask<(TenantAgent Agent, ResolvedAgentRunConfiguration Configuration)> ResolveAsync(Guid tenant, Guid instance, CancellationToken ct)
    {
        var state = await agents.ReadTenantAgentConfigurationAsync(tenant, instance, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("resource_unavailable");
        var owner = state.Agent.Instance;
        AgentDefinition baseline; IReadOnlyList<AgentDefinitionPublicationResource> published;
        if (Guid.TryParse(owner.DefinitionId, out var definitionId))
        {
            baseline = (await definitions.FindDefinitionPublicationAsync(tenant, definitionId, owner.ActiveVersion, ct).ConfigureAwait(false))?.Payload
                ?? throw new AccessDeniedException("definition_unavailable");
            published = await definitions.ListOwnedPublicationResourcesAsync(tenant, definitionId, owner.ActiveVersion, ct).ConfigureAwait(false);
        }
        else
        {
            baseline = await builtIns.GetAsync(owner.DefinitionId, owner.ActiveVersion, ct).ConfigureAwait(false)
                ?? throw new AccessDeniedException("definition_unavailable");
            published = await inheritedResources.ListAsync(baseline, ct).ConfigureAwait(false);
        }
        var bound = await bindings.ReadTenantCredentialBindingsAsync(tenant, instance, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("resource_unavailable");
        if (bound.Agent.Instance.Revision != owner.Revision) throw new SecurityConflictException("stale_revision");
        var effective = InstanceSettingsResolver.Resolve(baseline, owner.SettingsOverrides) with { SelfManagement = ManagementPolicyResolver.Resolve(baseline, owner) };
        var manifest = AgentInstanceResourceService.Resolve(effective, published, state.Resources, state.Skills);
        var environment = RoleEnvironments.Of(effective);
        var disabled = published.Where(p => !manifest.Any(r => r.Key == "definition:" + p.ResourceId.ToString("D"))).Select(p => p.LogicalPath).ToHashSet(StringComparer.Ordinal);
        var knowledge = environment.KnowledgeList.Where(k => !disabled.Contains(KnowledgeSourcePaths.ResolveBackingPath(k)))
            .Concat(manifest.Where(r => r.Key.StartsWith("instance:", StringComparison.Ordinal) && r.Kind == AgentDefinitionResourceKind.Knowledge)
                .Select(r => new KnowledgeSourceRef(r.Key, r.LogicalPath, r.VirtualPath, r.LogicalPath))).ToArray();
        effective = effective with { Environment = environment with { KnowledgeSources = knowledge } };
        effective = await BindSandboxProfilesAsync(tenant, state.Agent, effective, ct).ConfigureAwait(false);
        var configuration = new AgentRunConfiguration(effective, owner.Revision, owner.PersonaRevision, manifest,
            bound.Bindings.Select(b => b.Binding).ToArray(), InstanceSettingsResolver.Sources(baseline, owner.SettingsOverrides, owner.Revision, owner.PersonaRevision));
        return (state.Agent, new(configuration, owner.Persona, EffectiveSkillCatalogResolver.Resolve(effective, state.Skills), owner.ExecutionBudgets));
    }
}
