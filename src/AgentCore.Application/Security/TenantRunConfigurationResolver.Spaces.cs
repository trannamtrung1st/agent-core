using System.Security.Cryptography;
using System.Text;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Spaces;

namespace AgentCore.Application.Security;

public sealed partial class TenantRunConfigurationResolver
{
    private TenantSpaceContextResolver SpaceContexts => spaceContexts ?? new((ITenantSpaceStore)agents, (ITenantEnvironmentStore)agents,
        agents, authorizer ?? new IamAuthorizer((IIamStore)agents));

    public async ValueTask<(TenantAgent Agent, ResolvedAgentRunConfiguration Configuration)> ResolveInSpaceAsync(Guid tenant, Guid instance,
        SessionContextSelection selection, CancellationToken ct)
    {
        var baseline = await ResolveAsync(tenant, instance, ct).ConfigureAwait(false);
        // New work adopts the current mandatory release; current-resource checks for
        // an already admitted Run keep its pin and independent live authority fences.
        await ManagedGeneralAssistant.RequireCurrentAsync(builtIns, baseline.Agent, ct);
        var pin = await SpaceContexts.CaptureAsync(tenant, instance, selection, ct).ConfigureAwait(false);
        var original = baseline.Configuration.Configuration;
        var membership = (await ((ITenantSpaceStore)agents).ReadMembershipsAsync(tenant, pin.SpaceId, ct).ConfigureAwait(false)).Single(m => m.InstanceId == instance);
        var effective = ResolveAvailability(Inherited(original.Resources, original.CredentialBindings), membership);
        var manifest = await WithSpaceResources(tenant, pin.SpaceId, FilterResources(original.Resources, effective), effective, ct).ConfigureAwait(false);
        var definition = WithSpaceKnowledge(FilterKnowledge(original.Definition, original.Resources, manifest), manifest);
        var credentials = await ResolveCredentials(tenant, instance, pin.SpaceId, original.CredentialBindings, effective, membership, ct).ConfigureAwait(false);
        var fields = original.FieldSources.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        fields["space.instructions"] = new("Space", pin.SpaceRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        fields["membership.instructions"] = new("Membership", pin.MembershipRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        fields["session.context"] = new("Session", pin.SelectionRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (pin.EnvironmentRevision is { } environmentRevision) fields["environment.identity"] = new("Environment", environmentRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var configuration = new AgentRunConfiguration(definition, original.InstanceRevision, original.PersonaRevision, manifest,
            credentials.Select(b => b.Binding).ToArray(), fields, original.ResolvedBrowserIsolation, original.ContinuitySettings, pin, effective);
        return (baseline.Agent, baseline.Configuration with { Configuration = configuration });
    }
    public ValueTask RequireCurrentSpaceAsync(AgentRun run, CancellationToken ct) => SpaceContexts.RequireCurrentAsync(run, ct);
    // A fresh admission may refresh current configuration, but its context identity is
    // derived from the causal Run, never a newer selection in the parent Session.
    public async ValueTask<(TenantAgent Agent, ResolvedAgentRunConfiguration Configuration)> ResolveInCapturedSpaceAsync(
        AgentRun source, CancellationToken ct, long? selectionRevision = null)
    {
        await RequireCurrentSpaceAsync(source, ct).ConfigureAwait(false);
        var pin = source.Admission.Configuration!.SpaceContext!;
        return await ResolveInSpaceAsync(pin.TenantId, source.AgentInstanceId,
            SessionContextSelection.FromCapture(pin, source.CreatedAtUtc, selectionRevision), ct).ConfigureAwait(false);
    }
    public async ValueTask<TenantRunResourceConfiguration> CurrentResourcesForRunAsync(AgentRun run, CancellationToken ct = default)
    {
        var pin = run.Admission.Configuration?.SpaceContext ?? throw new AccessDeniedException("space_context_required");
        await SpaceContexts.RequireCurrentAsync(run, ct).ConfigureAwait(false);
        var resources = await CurrentResourcesAsync(pin.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false);
        var bound = await bindings.ReadTenantCredentialBindingsAsync(pin.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var membership = (await ((ITenantSpaceStore)agents).ReadMembershipsAsync(pin.TenantId, pin.SpaceId, ct).ConfigureAwait(false)).Single(m => m.InstanceId == run.AgentInstanceId);
        var effective = ResolveAvailability(Inherited(resources.Resources, bound.Bindings.Select(b => b.Binding).ToArray()), membership);
        var manifest = await WithSpaceResources(pin.TenantId, pin.SpaceId, FilterResources(resources.Resources, effective), effective, ct).ConfigureAwait(false);
        return resources with { Resources = manifest, Definition = WithSpaceKnowledge(FilterKnowledge(resources.Definition, resources.Resources, manifest), manifest) };
    }
    public async ValueTask<TenantCredentialBindingSnapshot> CurrentCredentialBindingsAsync(AgentRun run, CancellationToken ct = default, ITenantCredentialBindingStore? source = null)
    {
        var pin = run.Admission.Configuration?.SpaceContext ?? throw new AccessDeniedException("space_context_required");
        await SpaceContexts.RequireCurrentAsync(run, ct).ConfigureAwait(false);
        var snapshot = await (source ?? bindings).ReadTenantCredentialBindingsAsync(pin.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var resources = await CurrentResourcesAsync(pin.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false);
        var membership = (await ((ITenantSpaceStore)agents).ReadMembershipsAsync(pin.TenantId, pin.SpaceId, ct).ConfigureAwait(false)).Single(m => m.InstanceId == run.AgentInstanceId);
        var effective = ResolveAvailability(Inherited(resources.Resources, snapshot.Bindings.Select(b => b.Binding).ToArray()), membership);
        var resolved = await ResolveCredentials(pin.TenantId, run.AgentInstanceId, pin.SpaceId, snapshot.Bindings.Select(b => b.Binding).ToArray(), effective, membership, ct).ConfigureAwait(false);
        return snapshot with { Bindings = resolved };
    }
    private async ValueTask<IReadOnlyList<TenantCredentialBinding>> ResolveCredentials(Guid tenant, Guid instance, Guid space,
        IReadOnlyList<AgentCredentialBinding> inherited, IReadOnlyList<SpaceResourceBinding> effective, SpaceMembership membership, CancellationToken ct)
    {
        var result = new List<TenantCredentialBinding>();
        foreach (var source in effective.Where(b => b.Kind == SpaceBindingKind.Credential))
        {
            var id = Guid.Parse(source.ResourceKey);
            var resource = await ((ITenantCredentialStore)agents).FindTenantCredentialAsync(tenant, id, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("credential_unavailable");
            var original = membership.DisabledInheritedBindingKeys.Contains(source.StableKey, StringComparer.Ordinal) ? null
                : inherited.SingleOrDefault(b => b.CredentialId == id && b.Reference == source.AccountRef);
            var binding = original ?? ContextualCredentialBinding(tenant, space, instance, id, source.AccountRef!, membership);
            result.Add(new(binding, resource));
        }
        return result;
    }
    public static AgentCredentialBinding ContextualCredentialBinding(Guid tenant, Guid space, Guid instance, Guid credential, string alias, SpaceMembership member)
    {
        var identity = SHA256.HashData(Encoding.UTF8.GetBytes($"AgentCore.SpaceCredentialBinding\0{tenant:D}\0{space:D}\0{instance:D}\0{credential:D}"));
        return new(new Guid(identity.AsSpan(0, 16)), instance, credential, CredentialRules.Reference(alias), member.Revision, member.CreatedAt, member.UpdatedAt, space);
    }
    private static IReadOnlyList<SpaceResourceBinding> ResolveAvailability(IReadOnlyList<SpaceResourceBinding> inherited, SpaceMembership membership)
    {
        try { return EffectiveSpaceBindings.Resolve(inherited, membership); }
        catch (ArgumentException) { throw new AccessDeniedException("contextual_binding_ambiguous"); }
    }
    private static IReadOnlyList<SpaceResourceBinding> Inherited(IReadOnlyList<EffectiveAgentResource> resources, IReadOnlyList<AgentCredentialBinding> credentials) =>
        resources.Select(r => new SpaceResourceBinding(r.Key.StartsWith("definition:", StringComparison.Ordinal) ? SpaceBindingKind.DefinitionResource : SpaceBindingKind.InstanceResource, r.Key))
            .Concat(credentials.Select(b => new SpaceResourceBinding(SpaceBindingKind.Credential, b.CredentialId.ToString("D"), b.Reference))).ToArray();
    private static IReadOnlyList<EffectiveAgentResource> FilterResources(IReadOnlyList<EffectiveAgentResource> resources, IReadOnlyList<SpaceResourceBinding> effective)
    {
        var keys = effective.Where(b => b.Kind is SpaceBindingKind.DefinitionResource or SpaceBindingKind.InstanceResource).Select(b => b.ResourceKey).ToHashSet(StringComparer.Ordinal);
        return resources.Where(r => keys.Contains(r.Key)).ToArray();
    }
    private async ValueTask<IReadOnlyList<EffectiveAgentResource>> WithSpaceResources(Guid tenant, Guid space,
        IReadOnlyList<EffectiveAgentResource> inherited, IReadOnlyList<SpaceResourceBinding> effective, CancellationToken ct)
    {
        var result = inherited.ToList();
        foreach (var binding in effective.Where(b => b.Kind == SpaceBindingKind.SpaceFile))
        {
            if (!SpaceResourceKeys.TryParse(binding.ResourceKey, out var sourceSpace, out var sourceId) || sourceSpace != space
                || agents is not ITenantSpaceResourceStore sources) throw new AccessDeniedException("resource_unavailable");
            var source = await sources.FindSpaceResourceAsync(tenant, space, sourceId, ct).ConfigureAwait(false)
                ?? throw new AccessDeniedException("resource_unavailable");
            result.Add(source.Effective());
        }
        return result;
    }
    private static AgentDefinition WithSpaceKnowledge(AgentDefinition definition, IReadOnlyList<EffectiveAgentResource> resources) =>
        definition with { ExecutionPolicy = DefinitionExecutionPolicies.Of(definition) with {
            KnowledgeSources = DefinitionExecutionPolicies.Of(definition).KnowledgeList.Concat(resources
                .Where(r => r.SpaceSource is not null && r.Kind == AgentDefinitionResourceKind.Knowledge)
                .Select(r => new KnowledgeSourceRef(r.Key, r.LogicalPath, r.VirtualPath, r.LogicalPath))).ToArray() } };
    private static AgentDefinition FilterKnowledge(AgentDefinition definition, IReadOnlyList<EffectiveAgentResource> original, IReadOnlyList<EffectiveAgentResource> selected)
    {
        var removed = original.Where(r => !selected.Any(s => s.Key == r.Key)).Select(r => r.LogicalPath).ToHashSet(StringComparer.Ordinal);
        return definition with { ExecutionPolicy = DefinitionExecutionPolicies.Of(definition) with {
            KnowledgeSources = DefinitionExecutionPolicies.Of(definition).KnowledgeList.Where(k => !removed.Contains(KnowledgeSourcePaths.ResolveBackingPath(k))).ToArray() } };
    }
}
