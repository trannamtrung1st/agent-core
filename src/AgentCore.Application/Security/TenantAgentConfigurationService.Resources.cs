using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantAgentResources(InstanceResourceCatalog Catalog, long SecurityRevision);
public sealed record TenantAgentResourceInput(Guid? ResourceId, long? ExpectedResourceRevision, string LogicalPath,
    AgentDefinitionResourceKind Kind, string MediaType, byte[]? Content, bool? Enabled = null);
public sealed record TenantAgentResourceSaved(InstanceResourceView? Resource, long InstanceRevision, long SecurityRevision);
public sealed record TenantAgentResourceContent(InstanceResourceView Resource, byte[] Bytes);

public sealed partial class TenantAgentConfigurationService
{
    public async ValueTask<TenantAgentResources> ReadResourcesAsync(Guid actor, Guid tenant, Guid id, CancellationToken ct = default)
    {
        await ChildParentState(actor, tenant, id, ct).ConfigureAwait(false);
        var (snapshot, baseline) = await BoundForRead(actor, tenant, id, ct).ConfigureAwait(false);
        var definition = EffectiveChildDefinition(snapshot, baseline);
        var views = AgentInstanceResourceService.Views(definition, await Resources(tenant, baseline, ct).ConfigureAwait(false), snapshot.Resources, snapshot.Skills);
        var state = await ChildParentState(actor, tenant, id, ct).ConfigureAwait(false);
        var visible = await VisibleChildren(state, actor, tenant, id, "resources.read", views, r => r.Key, ct, true).ConfigureAwait(false);
        return new(new(snapshot.Agent.Instance.Revision, baseline.Id, baseline.Version,
            await SafeResourceViews(state, actor, tenant, id, visible.Items, ct).ConfigureAwait(false)), state.Tenant.Revision);
    }

    public async ValueTask<TenantAgentResourceContent> ReadResourceContentAsync(Guid actor, Guid tenant, Guid id, string key, CancellationToken ct = default)
    {
        var catalog = await ReadResourcesAsync(actor, tenant, id, ct).ConfigureAwait(false);
        var view = catalog.Catalog.Resources.SingleOrDefault(r => r.Key == key) ?? throw new AccessDeniedException("resource_unavailable");
        var bytes = await Content(view, ct).ConfigureAwait(false);
        await ChildReadState(actor, tenant, id, "resources.read", ct, key).ConfigureAwait(false);
        var current = await Snapshot(tenant, id, ct).ConfigureAwait(false);
        if (current.Agent.Instance.Revision != catalog.Catalog.InstanceRevision) throw new SecurityConflictException("resource_changed");
        return new(view, bytes);
    }

    public ValueTask<TenantAgentResourceSaved> UpsertResourceAsync(Guid actor, Guid tenant, Guid id, long securityRevision,
        long instanceRevision, TenantAgentResourceInput input, CancellationToken ct = default) => WithChildGate<TenantAgentResourceSaved>(id, async token =>
    {
        ArgumentNullException.ThrowIfNull(input);
        var state = await ChildChangeState(actor, tenant, id, "resources.manage", "resources.read", securityRevision, token,
            input.ResourceId is { } resourceId ? "instance:" + resourceId.ToString("D") : null).ConfigureAwait(false);
        var (snapshot, baseline) = await ResourceWritable(actor, tenant, id, instanceRevision, state, token).ConfigureAwait(false);
        return await SaveResource(actor, tenant, state, snapshot, baseline, input, null, token).ConfigureAwait(false);
    }, ct);

    public ValueTask<TenantAgentResourceSaved> CopyResourceAsync(Guid actor, Guid tenant, Guid id, string key, string path,
        long securityRevision, long instanceRevision, CancellationToken ct = default) => WithChildGate<TenantAgentResourceSaved>(id, async token =>
    {
        var state = await ChildChangeState(actor, tenant, id, "resources.manage", "resources.read", securityRevision, token).ConfigureAwait(false);
        await RequireChildPermission(state, actor, "resources.read", ChildScope(tenant, id, "resources.read", key), token).ConfigureAwait(false);
        var (snapshot, baseline) = await ResourceWritable(actor, tenant, id, instanceRevision, state, token).ConfigureAwait(false);
        var parsed = AgentInstanceResourceService.ParseKey(key);
        if (parsed.Origin != "definition") throw AgentCoreErrors.Validation("Copy requires a Definition resource.");
        var source = (await Resources(tenant, baseline, token).ConfigureAwait(false)).SingleOrDefault(r => r.ResourceId == parsed.Id)
            ?? throw new AccessDeniedException("resource_unavailable");
        var view = AgentInstanceResourceService.Views(EffectiveChildDefinition(snapshot, baseline), [source], snapshot.Resources, snapshot.Skills).Single(r => r.Key == key);
        return await SaveResource(actor, tenant, state, snapshot, baseline,
            new(null, null, path, source.Kind, source.MediaType, await Content(view, token).ConfigureAwait(false)), source, token).ConfigureAwait(false);
    }, ct);

    public ValueTask<TenantAgentResourceSaved> ChangeResourceAsync(Guid actor, Guid tenant, Guid id, string key, bool delete, bool? enabled,
        long securityRevision, long instanceRevision, long expectedResourceRevision, CancellationToken ct = default) => WithChildGate<TenantAgentResourceSaved>(id, async token =>
    {
        var state = await ChildChangeState(actor, tenant, id, delete ? "resources.delete" : "resources.manage", "resources.read", securityRevision, token, key).ConfigureAwait(false);
        var (snapshot, baseline) = await ResourceWritable(actor, tenant, id, instanceRevision, state, token).ConfigureAwait(false);
        var published = await Resources(tenant, baseline, token).ConfigureAwait(false); var definition = EffectiveChildDefinition(snapshot, baseline);
        var view = AgentInstanceResourceService.Views(definition, published, snapshot.Resources, snapshot.Skills).SingleOrDefault(r => r.Key == key)
            ?? throw new AccessDeniedException("resource_unavailable");
        if (view.Revision != expectedResourceRevision) throw new SecurityConflictException("stale_revision");
        var parsed = AgentInstanceResourceService.ParseKey(key); InstanceResourceMutation mutation;
        if (delete)
        {
            if (parsed.Origin != "instance") throw AgentCoreErrors.Validation("Inherited resource bytes are read-only.");
            mutation = new(id, instanceRevision, DeleteResourceId: parsed.Id, ExpectedResourceRevision: expectedResourceRevision);
        }
        else if (parsed.Origin == "instance")
        {
            if (enabled is null) throw AgentCoreErrors.Validation("Instance resources do not inherit enabled state.");
            var previous = snapshot.Resources.InstanceResources.Single(r => r.ResourceId == parsed.Id);
            mutation = new(id, instanceRevision, Resource: previous with { Enabled = enabled.Value, Revision = checked(previous.Revision + 1), UpdatedAt = time.GetUtcNow() }, ExpectedResourceRevision: previous.Revision);
        }
        else
        {
            var previous = snapshot.Resources.DefinitionStates.SingleOrDefault(r => r.ResourceId == parsed.Id);
            mutation = new(id, instanceRevision, DefinitionState: new(id, parsed.Id, enabled, (previous?.Revision ?? 0) + 1, time.GetUtcNow()), ExpectedStateRevision: previous?.Revision);
        }
        var next = ResourceAfter(snapshot.Resources, mutation);
        _ = AgentInstanceResourceService.Resolve(definition, published, next, snapshot.Skills);
        var saved = await Commit(actor, tenant, state, new(id, instanceRevision, Resource: mutation), delete ? "resources.delete" : "resources.choice.update", token).ConfigureAwait(false);
        var result = delete ? null : (await SafeResourceViews(state, actor, tenant, id,
            [AgentInstanceResourceService.Views(definition, published, next, snapshot.Skills).Single(r => r.Key == key)], token).ConfigureAwait(false)).Single();
        return new(result, saved.Instance.Revision, state.Tenant.Revision + 1);
    }, ct);

    private async ValueTask<TenantAgentResourceSaved> SaveResource(Guid actor, Guid tenant, TenantSecurityState state, TenantAgentConfigurationSnapshot snapshot,
        AgentDefinition baseline, TenantAgentResourceInput input, AgentDefinitionPublicationResource? source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var bytes = input.Content?.ToArray();
        if (bytes is null)
        {
            var old = snapshot.Resources.InstanceResources.SingleOrDefault(r => r.ResourceId == input.ResourceId)
                ?? throw AgentCoreErrors.Validation("Upload bytes, or select an existing resource in this Instance for metadata-only editing.");
            bytes = await Content(new("instance:" + old.ResourceId.ToString("D"), "Instance", old.LogicalPath, old.Kind,
                old.MediaType, old.ContentSha256, old.ByteLength, old.Enabled, null, old.Revision, "", []), ct).ConfigureAwait(false);
        }
        var owner = snapshot.Agent.Instance;
        var previous = snapshot.Resources.InstanceResources.SingleOrDefault(r => r.ResourceId == input.ResourceId);
        var resource = AgentInstanceResourceService.PrepareUpsert(owner, snapshot.Resources, input.ResourceId, input.ExpectedResourceRevision,
            input.LogicalPath, input.Kind, input.MediaType, bytes, input.Enabled ?? previous?.Enabled ?? true,
            state.Principals.Single(p => p.PrincipalId == actor).Kind == PrincipalKind.SystemIdentity ? SkillAuthor.Agent : SkillAuthor.Admin,
            ids.NewId(), time.GetUtcNow(), source);
        var mutation = new InstanceResourceMutation(owner.InstanceId, owner.Revision, Resource: resource, ExpectedResourceRevision: input.ExpectedResourceRevision);
        var next = ResourceAfter(snapshot.Resources, mutation); var definition = EffectiveChildDefinition(snapshot, baseline);
        var published = await Resources(tenant, baseline, ct).ConfigureAwait(false);
        _ = AgentInstanceResourceService.Resolve(definition, published, next, snapshot.Skills);
        await (content ?? throw new AccessDeniedException("resource_content_unavailable")).StoreVerifiedAsync(resource.ContentSha256, bytes, ct).ConfigureAwait(false);
        var saved = await Commit(actor, tenant, state, new(owner.InstanceId, owner.Revision, Resource: mutation), "resources.upsert", ct).ConfigureAwait(false);
        return new(AgentInstanceResourceService.Views(definition, published, next, snapshot.Skills).Single(r => r.Key == "instance:" + resource.ResourceId.ToString("D")), saved.Instance.Revision, state.Tenant.Revision + 1);
    }
    private async ValueTask<(TenantAgentConfigurationSnapshot Snapshot, AgentDefinition Definition)> ResourceWritable(Guid actor, Guid tenant, Guid id, long revision, TenantSecurityState state, CancellationToken ct)
    {
        var snapshot = await Snapshot(tenant, id, ct).ConfigureAwait(false); var owner = snapshot.Agent.Instance; RequireWritable(owner, revision);
        var (baseline, _, _) = await Definition(tenant, owner.DefinitionId, owner.ActiveVersion, ct).ConfigureAwait(false);
        if (state.Principals.Single(p => p.PrincipalId == actor).Kind == PrincipalKind.SystemIdentity
            && !ManagementPolicyResolver.Resolve(baseline, owner).Allows(HarnessManagementScope.Resources)) throw AgentCoreErrors.Forbidden("Resource administration is outside the effective self-management grant.");
        return (snapshot, baseline);
    }
    private async ValueTask<byte[]> Content(InstanceResourceView view, CancellationToken ct)
    {
        DefinitionResourcePolicies.ValidateContentSize(view.ByteLength);
        var bytes = await (content ?? throw new AccessDeniedException("resource_content_unavailable")).ReadAsync(view.ContentSha256, ct).ConfigureAwait(false)
            ?? throw AgentCoreErrors.Persistence("Resource bytes are unavailable.");
        AgentInstanceResourceService.Verify(view.ContentSha256, view.ByteLength, bytes); return bytes;
    }
    private static AgentDefinition EffectiveChildDefinition(TenantAgentConfigurationSnapshot snapshot, AgentDefinition baseline) =>
        InstanceSettingsResolver.Resolve(baseline, snapshot.Agent.Instance.SettingsOverrides) with { SelfManagement = ManagementPolicyResolver.Resolve(baseline, snapshot.Agent.Instance) };
    private static InstanceResourceSnapshot ResourceAfter(InstanceResourceSnapshot current, InstanceResourceMutation mutation) => new(
        current.DefinitionStates.Where(r => r.ResourceId != mutation.DefinitionState?.ResourceId).Concat(mutation.DefinitionState is null ? [] : new[] { mutation.DefinitionState }).ToArray(),
        current.InstanceResources.Where(r => r.ResourceId != mutation.DeleteResourceId && r.ResourceId != mutation.Resource?.ResourceId).Concat(mutation.Resource is null ? [] : new[] { mutation.Resource }).ToArray());
}
