using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantDefinitionService
{
    public const int MaxResourceBatchBytes = 16 * 1024 * 1024;
    public ValueTask<TenantDefinitionDraftState> ReadDraftStateAsync(Guid actor, Guid tenant, Guid draftId, CancellationToken ct = default, HumanSessionReference? initiator = null) =>
        WithHumanRead<TenantDefinitionDraftState>(actor, tenant, initiator, async () =>
        {
            var snapshot = await AuthorizedSnapshot(actor, tenant, draftId, "read", ct).ConfigureAwait(false);
            var definition = Guid.Parse(snapshot.Draft.DefinitionId); var state = await ResourceState(actor, tenant, definition, ct).ConfigureAwait(false);
            var visible = await ReadableDraftResources(state, actor, definition, draftId, snapshot.Resources, ct).ConfigureAwait(false);
            var tenantScope = AccessScope.ForTenant(tenant);
            var canCreate = visible.All && await ResourceAllowed(state, actor, "resources.manage", tenantScope, ct).ConfigureAwait(false);
            var permissions = new List<DefinitionResourcePermission>();
            foreach (var resource in visible.Items)
            {
                var scope = TenantDefinitionResourceScopes.Draft(tenant, definition, draftId, resource.ResourceId);
                permissions.Add(new(resource.ResourceId, scope, await ResourceAllowed(state, actor, "resources.manage", scope, ct).ConfigureAwait(false),
                    await ResourceAllowed(state, actor, "resources.delete", scope, ct).ConfigureAwait(false)));
            }
            var canEvaluate = visible.Items.Length == snapshot.Resources.Count && await ResourceAllowed(state, actor, "agent-definitions.update", Scope(tenant, definition), ct).ConfigureAwait(false);
            var canPublish = (snapshot.Resources.Count == 0 || canCreate) && await ResourceAllowed(state, actor, "agent-definitions.publish", Scope(tenant, definition), ct).ConfigureAwait(false);
            return new(snapshot.Draft, visible.Items, snapshot.Scenarios, snapshot.Results, new(visible.All, canCreate, canEvaluate, canPublish, permissions));
        }, ct);

    public async ValueTask<AgentDefinitionDraftResourceBatchBound> BindResourcesAsync(Guid actor, Guid tenant, Guid draftId,
        long expectedRevision, long expectedDraftRevision, IReadOnlyList<TenantDefinitionResourceUpload> uploads, CancellationToken ct = default, HumanSessionReference? initiator = null)
    {
        ArgumentNullException.ThrowIfNull(uploads);
        uploads = uploads.ToArray();
        if (uploads.Count is < 1 or > AgentResourceLimits.MaxItemsPerDraft || uploads.Any(u => u is null) || uploads.Sum(u => (long)(u.Content?.Length ?? 0)) > MaxResourceBatchBytes)
            throw new ArgumentException("Resource batch exceeds its item or transfer limit.");
        var snapshot = await AuthorizedSnapshot(actor, tenant, draftId, "read", ct).ConfigureAwait(false);
        var definition = Guid.Parse(snapshot.Draft.DefinitionId); var state = await ResourceState(actor, tenant, definition, ct, initiator).ConfigureAwait(false);
        foreach (var upload in uploads)
        {
            var existing = upload.ResourceId is { } id ? snapshot.Resources.SingleOrDefault(r => r.ResourceId == id)
                ?? throw new AccessDeniedException("resource_unavailable") : null;
            var scope = existing is null ? AccessScope.ForTenant(tenant) : TenantDefinitionResourceScopes.Draft(tenant, definition, draftId, existing.ResourceId);
            await RequireResourcePermission(state, actor, "resources.read", scope, ct).ConfigureAwait(false);
            await RequireResourcePermission(state, actor, "resources.manage", scope, ct).ConfigureAwait(false);
        }
        if (state.Tenant.Revision != expectedRevision) throw new SecurityConflictException("stale_revision");
        if (snapshot.Draft.Revision != expectedDraftRevision) throw new SecurityConflictException("stale_revision");
        var now = time.GetUtcNow(); var prepared = new List<AgentDefinitionDraftResource>(); var payloads = new List<byte[]>();
        foreach (var upload in uploads)
        {
            var existing = upload.ResourceId is { } resourceId ? snapshot.Resources.SingleOrDefault(r => r.ResourceId == resourceId)
                ?? throw new AccessDeniedException("resource_unavailable") : null;
            var path = DefinitionResourcePolicies.NormalizeLogicalPath(upload.LogicalPath);
            var mediaType = DefinitionResourcePolicies.NormalizeMediaType(upload.MediaType);
            if (!Enum.IsDefined(upload.Kind)) throw new ArgumentException("Unknown resource kind.");
            // Hashes from another upload/tenant are never accepted as proof of content ownership.
            var bytes = upload.Content?.ToArray() ?? (existing is null ? throw new ArgumentException("Upload resource content.")
                : await DefinitionResourceStoredContent.RequireForBindingAsync(content, existing.ContentSha256, existing.ByteLength, mediaType, ct).ConfigureAwait(false));
            DefinitionResourcePolicies.ValidateContentSize(bytes.Length);
            DefinitionResourcePolicies.RejectSecretsInTextualContent(mediaType, bytes);
            var hash = DefinitionResourceContentHasher.ComputeSha256Hex(bytes);
            prepared.Add(new(existing?.ResourceId ?? ids.NewId(), draftId, path, upload.Kind, mediaType, hash, bytes.Length, now)); payloads.Add(bytes);
        }
        TenantDefinitionChildRules.ValidateResourceBatch(draftId, snapshot.Resources, prepared);
        for (var i = 0; i < prepared.Count; i++) await content.StoreVerifiedAsync(prepared[i].ContentSha256, payloads[i], ct).ConfigureAwait(false);
        var saved = await definitions.TryBindDefinitionResourcesAsync(Advance(state), expectedRevision, draftId, expectedDraftRevision, prepared,
            Audit(tenant, actor, "resources.bind", definition, now), ct, initiator).ConfigureAwait(false) ?? throw new SecurityConflictException("stale_revision");
        return await VisibleResourceMutation(actor, tenant, definition, draftId, saved, ct, initiator).ConfigureAwait(false);
    }
    public async ValueTask<AgentDefinitionDraftResourceBatchBound> RemoveResourceAsync(Guid actor, Guid tenant, Guid draftId, Guid resourceId,
        long expectedRevision, long expectedDraftRevision, CancellationToken ct = default, HumanSessionReference? initiator = null)
    {
        var draft = await OwnedDraft(tenant, draftId, ct).ConfigureAwait(false); var id = Guid.Parse(draft.DefinitionId);
        var state = await ResourceState(actor, tenant, id, ct, initiator).ConfigureAwait(false);
        var scope = TenantDefinitionResourceScopes.Draft(tenant, id, draftId, resourceId);
        await RequireResourcePermission(state, actor, "resources.read", scope, ct).ConfigureAwait(false);
        await RequireResourcePermission(state, actor, "resources.delete", scope, ct).ConfigureAwait(false);
        if (state.Tenant.Revision != expectedRevision) throw new SecurityConflictException("stale_revision");
        var next = Advance(state) with { Assignments = state.Assignments.Where(a => a.Scope != scope).ToArray() };
        var saved = await definitions.TryRemoveDefinitionResourceAsync(next, expectedRevision, draftId, expectedDraftRevision, resourceId,
            Audit(tenant, actor, "resources.remove", id, time.GetUtcNow()), ct, initiator).ConfigureAwait(false) ?? throw new SecurityConflictException("stale_revision");
        return await VisibleResourceMutation(actor, tenant, id, draftId, saved, ct, initiator).ConfigureAwait(false);
    }
    public ValueTask<TenantDefinitionResourceContent> ReadDraftResourceAsync(Guid actor, Guid tenant, Guid draftId, Guid resourceId, CancellationToken ct = default, HumanSessionReference? initiator = null) =>
        WithHumanRead<TenantDefinitionResourceContent>(actor, tenant, initiator, async () =>
        {
            var snapshot = await AuthorizedSnapshot(actor, tenant, draftId, "read", ct).ConfigureAwait(false);
            var resource = snapshot.Resources.SingleOrDefault(r => r.ResourceId == resourceId) ?? throw new AccessDeniedException("resource_unavailable");
            var definition = Guid.Parse(snapshot.Draft.DefinitionId); var scope = TenantDefinitionResourceScopes.Draft(tenant, definition, draftId, resourceId);
            var state = await ResourceState(actor, tenant, definition, ct).ConfigureAwait(false);
            await RequireResourcePermission(state, actor, "resources.read", scope, ct).ConfigureAwait(false);
            var bytes = await DefinitionResourceStoredContent.RequireForBindingAsync(content, resource.ContentSha256, resource.ByteLength, resource.MediaType, ct).ConfigureAwait(false);
            state = await ResourceState(actor, tenant, definition, ct).ConfigureAwait(false);
            await RequireResourcePermission(state, actor, "resources.read", scope, ct).ConfigureAwait(false);
            if ((await OwnedDraft(tenant, draftId, ct).ConfigureAwait(false)).Revision != snapshot.Draft.Revision) throw new SecurityConflictException("resource_changed");
            return new(resource.LogicalPath, resource.MediaType, bytes);
        }, ct);
    public ValueTask<IReadOnlyList<AgentDefinitionPublicationResource>> PublicationResourcesAsync(Guid actor, Guid tenant, Guid definitionId, int version, CancellationToken ct = default, HumanSessionReference? initiator = null) =>
        WithHumanRead<IReadOnlyList<AgentDefinitionPublicationResource>>(actor, tenant, initiator, async () =>
        {
            return (await PublicationResourceStateAsync(actor, tenant, definitionId, version, ct).ConfigureAwait(false)).Items;
        }, ct);
    public ValueTask<TenantPublicationResourceState> PublicationResourceStateAsync(Guid actor, Guid tenant, Guid definitionId, int version, CancellationToken ct = default, HumanSessionReference? initiator = null) =>
        WithHumanRead<TenantPublicationResourceState>(actor, tenant, initiator, async () =>
        {
            await authorizer.RequireAsync(actor, "agent-definitions.read", Scope(tenant, definitionId), ct).ConfigureAwait(false);
            if (await definitions.FindDefinitionPublicationAsync(tenant, definitionId, version, ct).ConfigureAwait(false) is null) throw new AccessDeniedException("resource_unavailable");
            var resources = await definitions.ListOwnedPublicationResourcesAsync(tenant, definitionId, version, ct).ConfigureAwait(false);
            var state = await ResourceState(actor, tenant, definitionId, ct).ConfigureAwait(false);
            var visible = await authorizer.VisibleResourcesAsync(state, actor, "resources.read", resources,
                r => TenantDefinitionResourceScopes.Publication(tenant, definitionId, version, r.ResourceId), ct).ConfigureAwait(false);
            return new(visible.Items, visible.All);
        }, ct);
    public ValueTask<TenantDefinitionResourceContent> ReadPublicationResourceAsync(Guid actor, Guid tenant, Guid definitionId, int version, Guid resourceId, CancellationToken ct = default, HumanSessionReference? initiator = null) =>
        WithHumanRead<TenantDefinitionResourceContent>(actor, tenant, initiator, async () =>
        {
            var resource = (await PublicationResourcesAsync(actor, tenant, definitionId, version, ct).ConfigureAwait(false)).SingleOrDefault(r => r.ResourceId == resourceId)
                ?? throw new AccessDeniedException("resource_unavailable");
            var bytes = await DefinitionResourceStoredContent.RequireForBindingAsync(content, resource.ContentSha256, resource.ByteLength, resource.MediaType, ct).ConfigureAwait(false);
            await authorizer.RequireAsync(actor, "agent-definitions.read", Scope(tenant, definitionId), ct).ConfigureAwait(false);
            await authorizer.RequireAsync(actor, "resources.read", TenantDefinitionResourceScopes.Publication(tenant, definitionId, version, resourceId), ct).ConfigureAwait(false);
            return new(resource.LogicalPath, resource.MediaType, bytes);
        }, ct);
    private async ValueTask<TenantDefinitionDraftSnapshot> AuthorizedSnapshot(Guid actor, Guid tenant, Guid draftId, string action, CancellationToken ct)
    {
        var snapshot = await definitions.ReadDefinitionDraftSnapshotAsync(tenant, draftId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var scope = Scope(tenant, Guid.Parse(snapshot.Draft.DefinitionId));
        await authorizer.RequireAsync(actor, "agent-definitions." + action, scope, ct).ConfigureAwait(false);
        if (action != "read") await authorizer.RequireAsync(actor, "agent-definitions.read", scope, ct).ConfigureAwait(false);
        return snapshot;
    }
}
