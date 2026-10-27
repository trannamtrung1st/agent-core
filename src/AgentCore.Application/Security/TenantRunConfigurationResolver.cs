using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Security;

public sealed record TenantRunResourceConfiguration(TenantAgentConfigurationSnapshot Snapshot, AgentDefinition Baseline,
    AgentDefinition Definition, IReadOnlyList<EffectiveAgentResource> Resources);

public sealed partial class TenantRunConfigurationResolver(ITenantAgentStore agents, ITenantDefinitionStore definitions,
    IBuiltInAgentDefinitionStore builtIns, InheritedDefinitionResourceCatalog inheritedResources, ITenantCredentialBindingStore bindings,
    ITenantSandboxProfileStore? sandboxProfiles = null, ISandboxImageAuthority? sandboxImages = null, ISandboxReadiness? sandboxReadiness = null, IamAuthorizer? authorizer = null, TenantSpaceContextResolver? spaceContexts = null, ITenantEvaluationStore? evaluations = null)
{
    public async ValueTask<AgentRunContinuitySettings?> CurrentContinuitySettingsAsync(AgentRun run, CancellationToken ct)
    {
        if (run.Admission.Configuration?.ContinuitySettings is not { } pin || run.Owner.TenantId is not { } tenant) return null;
        var snapshot = await agents.ReadTenantAgentConfigurationAsync(tenant, run.AgentInstanceId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("experience_unavailable");
        if (snapshot.Agent.PrincipalId != run.Owner.ExecutorPrincipalId) throw new AccessDeniedException("experience_unavailable");
        return pin with { ExperienceEnabled = pin.ExperienceEnabled && snapshot.Isolation.ExperienceEnabled, AllowMaintenance = pin.AllowMaintenance && snapshot.Isolation.AllowMaintenance };
    }
    public async ValueTask<(TenantAgent Agent, AgentDefinition Definition)> BaselineAsync(Guid tenant, Guid instance, CancellationToken ct)
    {
        var agent = await agents.FindTenantAgentAsync(tenant, instance, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var owner = agent.Instance;
        var baseline = Guid.TryParse(owner.DefinitionId, out var owned)
            ? (await definitions.FindDefinitionPublicationAsync(tenant, owned, owner.ActiveVersion, ct).ConfigureAwait(false))?.Payload
            : await builtIns.GetAsync(owner.DefinitionId, owner.ActiveVersion, ct).ConfigureAwait(false);
        return (agent, baseline ?? throw new AccessDeniedException("definition_unavailable"));
    }
    // Current policy only: do not reread resources, secrets or image availability just to
    // constrain an already admitted Run. Its original instructions and payload stay pinned.
    public async ValueTask<(TenantAgent Agent, AgentDefinition Definition)> CurrentPolicyAsync(Guid tenant, Guid instance, CancellationToken ct = default)
    {
        var (agent, baseline) = await BaselineAsync(tenant, instance, ct).ConfigureAwait(false);
        var effective = InstanceSettingsResolver.Resolve(baseline, agent.Instance.SettingsOverrides) with {
            SelfManagement = ManagementPolicyResolver.Resolve(baseline, agent.Instance)
        };
        return (agent, effective);
    }
    public async ValueTask<TenantRunResourceConfiguration> CurrentResourcesAsync(Guid tenant, Guid instance, CancellationToken ct = default)
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
        var effective = InstanceSettingsResolver.Resolve(baseline, owner.SettingsOverrides) with { SelfManagement = ManagementPolicyResolver.Resolve(baseline, owner) };
        var manifest = InstanceResourceRules.Resolve(effective, published, state.Resources, state.Skills);
        var environment = DefinitionExecutionPolicies.Of(effective);
        var disabled = published.Where(p => !manifest.Any(r => r.Key == "definition:" + p.ResourceId.ToString("D"))).Select(p => p.LogicalPath).ToHashSet(StringComparer.Ordinal);
        var knowledge = environment.KnowledgeList.Where(k => !disabled.Contains(KnowledgeSourcePaths.ResolveBackingPath(k)))
            .Concat(manifest.Where(r => r.Key.StartsWith("instance:", StringComparison.Ordinal) && r.Kind == AgentDefinitionResourceKind.Knowledge)
                .Select(r => new KnowledgeSourceRef(r.Key, r.LogicalPath, r.VirtualPath, r.LogicalPath))).ToArray();
        effective = effective with { ExecutionPolicy = environment with { KnowledgeSources = knowledge } };
        return new(state, baseline, effective, manifest);
    }
    public async ValueTask<(TenantAgent Agent, ResolvedAgentRunConfiguration Configuration)> ResolveAsync(Guid tenant, Guid instance, CancellationToken ct)
    {
        var resources = await CurrentResourcesAsync(tenant, instance, ct).ConfigureAwait(false);
        var state = resources.Snapshot; var owner = state.Agent.Instance;
        var bound = await bindings.ReadTenantCredentialBindingsAsync(tenant, instance, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("resource_unavailable");
        if (bound.Agent.Instance.Revision != owner.Revision) throw new SecurityConflictException("stale_revision");
        var effective = await BindSandboxProfilesAsync(tenant, state.Agent, resources.Definition, ct).ConfigureAwait(false);
        long evaluationPolicyRevision = 0;
        if (evaluations is not null)
        {
            var defaults = await evaluations.ReadEvaluationGovernanceAsync(tenant, ct);
            evaluationPolicyRevision = defaults.Revision;
            effective = effective with { EvaluationBindings = defaults.Defaults.Concat(effective.EvaluationBindings ?? []).Distinct().ToArray() };
            AgentCore.Domain.Evaluations.EvaluationRules.ValidateBindings(effective.EvaluationBindings);
        }
        foreach (var binding in (effective.EvaluationBindings ?? []).Where(b => b.Hook != AgentCore.Domain.Evaluations.EvaluationHook.Publication))
        {
            var suite = evaluations is null ? null : await evaluations.FindSuiteAsync(tenant, binding.SuiteId, binding.Version, ct);
            if (suite is null) throw new AccessDeniedException("evaluation_suite_unavailable");
            await AgentCore.Application.Evaluations.EvaluationBindingValidation.RequireAsync(evaluations!, tenant, binding, suite, ct);
            // Only a required precondition constrains factual execution admission.
            // Other observations repeat execution authority at their own hook and remain unverified on denial.
            if (binding.Policy != AgentCore.Domain.Evaluations.EvaluationPolicy.RequirePassBeforeRun) continue;
            if (suite.Archived || (await evaluations!.FindSuiteAsync(tenant, binding.SuiteId, ct: ct))?.Archived != false) throw new AccessDeniedException("evaluation_suite_unavailable");
            if (authorizer is null) throw new AccessDeniedException("evaluation_authority_unavailable");
            await authorizer.RequireAsync(state.Agent.PrincipalId, "evaluations.read", AgentCore.Application.Evaluations.EvaluationStoreRules.Scope(tenant, suite.SuiteId), ct);
            await authorizer.RequireAsync(state.Agent.PrincipalId, "evaluations.run", AgentCore.Application.Evaluations.EvaluationStoreRules.Scope(tenant, suite.SuiteId), ct);
        }
        var sources = InstanceSettingsResolver.Sources(resources.Baseline, owner.SettingsOverrides, owner.Revision, owner.PersonaRevision).ToDictionary(p => p.Key, p => p.Value);
        if (evaluationPolicyRevision > 0) sources["evaluationBindings"] = new("evaluation-governance", evaluationPolicyRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var configuration = new AgentRunConfiguration(effective, owner.Revision, owner.PersonaRevision, resources.Resources,
            bound.Bindings.Select(b => b.Binding).ToArray(), sources,
            continuitySettings: new(state.Isolation.ExperienceEnabled, state.Isolation.ExperienceRevision, state.Isolation.AllowMaintenance, state.Isolation.MaintenanceRevision));
        return (state.Agent, new(configuration, owner.Persona, EffectiveSkillCatalogResolver.Resolve(effective, state.Skills), owner.ExecutionBudgets));
    }
}
