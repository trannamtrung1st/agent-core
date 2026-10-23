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
        var catalog = await ReadResourceCatalog(actor, tenant, id, ct).ConfigureAwait(false);
        return new(new(catalog.Agent.Instance.Revision, catalog.Agent.Instance.DefinitionId, catalog.Agent.Instance.ActiveVersion,
            catalog.Items), catalog.State.Tenant.Revision);
    }

    private async ValueTask<ChildCatalog<InstanceResourceView>> ReadResourceCatalog(Guid actor, Guid tenant, Guid id, CancellationToken ct)
    {
        await ChildParentState(actor, tenant, id, ct).ConfigureAwait(false);
        var (snapshot, baseline) = await BoundForRead(actor, tenant, id, ct).ConfigureAwait(false);
        var definition = EffectiveChildDefinition(snapshot, baseline);
        var views = InstanceResourceRules.Views(definition, await Resources(tenant, baseline, ct).ConfigureAwait(false), snapshot.Resources, snapshot.Skills);
        var state = await ChildParentState(actor, tenant, id, ct).ConfigureAwait(false);
        var visible = await VisibleChildren(state, actor, tenant, id, "resources.read", views, r => r.Key, ct, true).ConfigureAwait(false);
        return new(snapshot.Agent, await SafeResourceViews(state, actor, tenant, id, visible.Items, ct).ConfigureAwait(false), state, visible.All);
    }

    public async ValueTask<TenantAgentResourceContent> ReadResourceContentAsync(Guid actor, Guid tenant, Guid id, string key, CancellationToken ct = default,
        Func<InstanceResourceView, int>? maximumBytes = null)
    {
        var catalog = await ReadResourcesAsync(actor, tenant, id, ct).ConfigureAwait(false);
        var view = catalog.Catalog.Resources.SingleOrDefault(r => r.Key == key) ?? throw new AccessDeniedException("resource_unavailable");
        // Evaluate a preview's limit against this fresh, authorized item before allocating its blob.
        if (maximumBytes is not null && view.ByteLength > maximumBytes(view))
            throw new AgentCoreException("preview_too_large", "The file exceeds its effective preview limit. Download the original.", 413);
        var bytes = await Content(view, ct).ConfigureAwait(false);
        await ChildReadState(actor, tenant, id, "resources.read", ct, key).ConfigureAwait(false);
        var current = await Snapshot(tenant, id, ct).ConfigureAwait(false);
        if (current.Agent.Instance.Revision != catalog.Catalog.InstanceRevision) throw new SecurityConflictException("resource_changed");
        return new(view, bytes);
    }

    public ValueTask<TenantAgentResourceSaved> UpsertResourceAsync(Guid actor, Guid tenant, Guid id, long securityRevision,
        long instanceRevision, TenantAgentResourceInput input, CancellationToken ct = default, HumanSessionReference? initiator = null) => WithChildGate<TenantAgentResourceSaved>(id, async token =>
    {
        ArgumentNullException.ThrowIfNull(input);
        var state = await ChildChangeState(actor, tenant, id, "resources.manage", "resources.read", securityRevision, token,
            input.ResourceId is { } resourceId ? "instance:" + resourceId.ToString("D") : null).ConfigureAwait(false);
        var (snapshot, baseline) = await ResourceWritable(actor, tenant, id, instanceRevision, state, token).ConfigureAwait(false);
        return await SaveResource(actor, tenant, state, snapshot, baseline, input, null, token, initiator).ConfigureAwait(false);
    }, ct);

    public ValueTask<TenantAgentResourceSaved> CopyResourceAsync(Guid actor, Guid tenant, Guid id, string key, string path,
        long securityRevision, long instanceRevision, CancellationToken ct = default, HumanSessionReference? initiator = null) => WithChildGate<TenantAgentResourceSaved>(id, async token =>
    {
        var state = await ChildChangeState(actor, tenant, id, "resources.manage", "resources.read", securityRevision, token).ConfigureAwait(false);
        await RequireChildPermission(state, actor, "resources.read", ChildScope(tenant, id, "resources.read", key), token).ConfigureAwait(false);
        var (snapshot, baseline) = await ResourceWritable(actor, tenant, id, instanceRevision, state, token).ConfigureAwait(false);
        var parsed = InstanceResourceRules.ParseKey(key);
        if (parsed.Origin != "definition") throw AgentCoreErrors.Validation("Copy requires a Definition resource.");
        var source = (await Resources(tenant, baseline, token).ConfigureAwait(false)).SingleOrDefault(r => r.ResourceId == parsed.Id)
            ?? throw new AccessDeniedException("resource_unavailable");
        var view = InstanceResourceRules.Views(EffectiveChildDefinition(snapshot, baseline), [source], snapshot.Resources, snapshot.Skills).Single(r => r.Key == key);
        return await SaveResource(actor, tenant, state, snapshot, baseline,
            new(null, null, path, source.Kind, source.MediaType, await Content(view, token).ConfigureAwait(false)), source, token, initiator).ConfigureAwait(false);
    }, ct);

    public ValueTask<TenantAgentResourceSaved> ChangeResourceAsync(Guid actor, Guid tenant, Guid id, string key, bool delete, bool? enabled,
        long securityRevision, long instanceRevision, long expectedResourceRevision, CancellationToken ct = default, HumanSessionReference? initiator = null) => WithChildGate<TenantAgentResourceSaved>(id, async token =>
    {
        var state = await ChildChangeState(actor, tenant, id, delete ? "resources.delete" : "resources.manage", "resources.read", securityRevision, token, key).ConfigureAwait(false);
        var (snapshot, baseline) = await ResourceWritable(actor, tenant, id, instanceRevision, state, token).ConfigureAwait(false);
        var published = await Resources(tenant, baseline, token).ConfigureAwait(false);
        var actorKind = state.Principals.Single(p => p.PrincipalId == actor).Kind == PrincipalKind.SystemIdentity ? SkillAuthor.Agent : SkillAuthor.Admin;
        var prepared = TenantAgentResourcePreparation.Choice(snapshot.Agent.Instance, baseline, snapshot.Resources, snapshot.Skills,
            published, key, delete, enabled, expectedResourceRevision, actorKind, time.GetUtcNow());
        if (prepared.Change is null)
            return await UnchangedResource(actor, tenant, state, snapshot, prepared.Resource!, token, initiator).ConfigureAwait(false);
        var saved = await Commit(actor, tenant, state, prepared.Change, delete ? "resources.delete" : "resources.choice.update", token, initiator).ConfigureAwait(false);
        var result = prepared.Resource is null ? null : (await SafeResourceViews(state, actor, tenant, id, [prepared.Resource], token).ConfigureAwait(false)).Single();
        return new(result, saved.Instance.Revision, state.Tenant.Revision + 1);
    }, ct);

    private async ValueTask<TenantAgentResourceSaved> SaveResource(Guid actor, Guid tenant, TenantSecurityState state, TenantAgentConfigurationSnapshot snapshot,
        AgentDefinition baseline, TenantAgentResourceInput input, AgentDefinitionPublicationResource? source, CancellationToken ct, HumanSessionReference? initiator)
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
        var published = await Resources(tenant, baseline, ct).ConfigureAwait(false);
        var actorKind = state.Principals.Single(p => p.PrincipalId == actor).Kind == PrincipalKind.SystemIdentity ? SkillAuthor.Agent : SkillAuthor.Admin;
        var prepared = TenantAgentResourcePreparation.Upsert(owner, baseline, snapshot.Resources, snapshot.Skills, published,
            input, bytes, actorKind, ids.NewId(), time.GetUtcNow(), source);
        if (prepared.Change is null)
            return await UnchangedResource(actor, tenant, state, snapshot, prepared.Resource!, ct, initiator).ConfigureAwait(false);
        var resource = prepared.Change.Resource!.Resource!;
        await (content ?? throw new AccessDeniedException("resource_content_unavailable")).StoreVerifiedAsync(resource.ContentSha256, bytes, ct).ConfigureAwait(false);
        var saved = await Commit(actor, tenant, state, prepared.Change, "resources.upsert", ct, initiator).ConfigureAwait(false);
        return new(prepared.Resource, saved.Instance.Revision, state.Tenant.Revision + 1);
    }
    private async ValueTask<TenantAgentResourceSaved> UnchangedResource(Guid actor, Guid tenant, TenantSecurityState state,
        TenantAgentConfigurationSnapshot snapshot, InstanceResourceView resource, CancellationToken ct, HumanSessionReference? initiator)
    {
        var id = snapshot.Agent.Instance.InstanceId;
        var current = await ChildChangeState(actor, tenant, id, "resources.manage", "resources.read", state.Tenant.Revision, ct, resource.Key).ConfigureAwait(false);
        var visible = (await SafeResourceViews(current, actor, tenant, id, [resource], ct).ConfigureAwait(false)).Single();
        await RequireOriginalHuman(actor, tenant, initiator, ct).ConfigureAwait(false);
        return new(visible, snapshot.Agent.Instance.Revision, state.Tenant.Revision);
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
        InstanceResourceRules.Verify(view.ContentSha256, view.ByteLength, bytes); return bytes;
    }
    private static AgentDefinition EffectiveChildDefinition(TenantAgentConfigurationSnapshot snapshot, AgentDefinition baseline) =>
        InstanceSettingsResolver.Resolve(baseline, snapshot.Agent.Instance.SettingsOverrides) with { SelfManagement = ManagementPolicyResolver.Resolve(baseline, snapshot.Agent.Instance) };
}
