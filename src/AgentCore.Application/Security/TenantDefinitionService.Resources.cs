using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Security;

public sealed partial class TenantDefinitionService
{
    public const int MaxResourceBatchBytes = 16 * 1024 * 1024;
    public async ValueTask<TenantDefinitionDraftSnapshot> ReadDraftStateAsync(Guid actor, Guid tenant, Guid draftId, CancellationToken ct = default) =>
        await AuthorizedSnapshot(actor, tenant, draftId, "read", ct).ConfigureAwait(false);

    public async ValueTask<AgentDefinitionDraftResourceBatchBound> BindResourcesAsync(Guid actor, Guid tenant, Guid draftId,
        long expectedRevision, long expectedDraftRevision, IReadOnlyList<TenantDefinitionResourceUpload> uploads, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(uploads);
        if (uploads.Count is < 1 or > AgentResourceLimits.MaxItemsPerDraft || uploads.Any(u => u is null) || uploads.Sum(u => (long)(u.Content?.Length ?? 0)) > MaxResourceBatchBytes)
            throw new ArgumentException("Resource batch exceeds its item or transfer limit.");
        var snapshot = await AuthorizedSnapshot(actor, tenant, draftId, "update", ct).ConfigureAwait(false);
        var state = await ForChange(actor, tenant, "update", Scope(tenant, Guid.Parse(snapshot.Draft.DefinitionId)), expectedRevision, ct).ConfigureAwait(false);
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
        return await definitions.TryBindDefinitionResourcesAsync(Advance(state), expectedRevision, draftId, expectedDraftRevision, prepared,
            Audit(tenant, actor, "resources.bind", Guid.Parse(snapshot.Draft.DefinitionId), now), ct).ConfigureAwait(false) ?? throw new SecurityConflictException("stale_revision");
    }
    public async ValueTask<AgentDefinitionDraftResourceBatchBound> RemoveResourceAsync(Guid actor, Guid tenant, Guid draftId, Guid resourceId,
        long expectedRevision, long expectedDraftRevision, CancellationToken ct = default)
    {
        var draft = await OwnedDraft(tenant, draftId, ct).ConfigureAwait(false); var id = Guid.Parse(draft.DefinitionId);
        var state = await ForChange(actor, tenant, "update", Scope(tenant, id), expectedRevision, ct).ConfigureAwait(false);
        return await definitions.TryRemoveDefinitionResourceAsync(Advance(state), expectedRevision, draftId, expectedDraftRevision, resourceId,
            Audit(tenant, actor, "resources.remove", id, time.GetUtcNow()), ct).ConfigureAwait(false) ?? throw new SecurityConflictException("stale_revision");
    }
    public async ValueTask<TenantDefinitionResourceContent> ReadDraftResourceAsync(Guid actor, Guid tenant, Guid draftId, Guid resourceId, CancellationToken ct = default)
    {
        var snapshot = await AuthorizedSnapshot(actor, tenant, draftId, "read", ct).ConfigureAwait(false);
        var resource = snapshot.Resources.SingleOrDefault(r => r.ResourceId == resourceId) ?? throw new AccessDeniedException("resource_unavailable");
        var bytes = await DefinitionResourceStoredContent.RequireForBindingAsync(content, resource.ContentSha256, resource.ByteLength, resource.MediaType, ct).ConfigureAwait(false);
        await authorizer.RequireAsync(actor, "agent-definitions.read", Scope(tenant, Guid.Parse(snapshot.Draft.DefinitionId)), ct).ConfigureAwait(false);
        return new(resource.LogicalPath, resource.MediaType, bytes);
    }
    public async ValueTask<IReadOnlyList<AgentDefinitionPublicationResource>> PublicationResourcesAsync(Guid actor, Guid tenant, Guid definitionId, int version, CancellationToken ct = default)
    {
        await authorizer.RequireAsync(actor, "agent-definitions.read", Scope(tenant, definitionId), ct).ConfigureAwait(false);
        if (await definitions.FindDefinitionPublicationAsync(tenant, definitionId, version, ct).ConfigureAwait(false) is null) throw new AccessDeniedException("resource_unavailable");
        return await definitions.ListOwnedPublicationResourcesAsync(tenant, definitionId, version, ct).ConfigureAwait(false);
    }
    public async ValueTask<TenantDefinitionResourceContent> ReadPublicationResourceAsync(Guid actor, Guid tenant, Guid definitionId, int version, Guid resourceId, CancellationToken ct = default)
    {
        var resource = (await PublicationResourcesAsync(actor, tenant, definitionId, version, ct).ConfigureAwait(false)).SingleOrDefault(r => r.ResourceId == resourceId)
            ?? throw new AccessDeniedException("resource_unavailable");
        var bytes = await DefinitionResourceStoredContent.RequireForBindingAsync(content, resource.ContentSha256, resource.ByteLength, resource.MediaType, ct).ConfigureAwait(false);
        await authorizer.RequireAsync(actor, "agent-definitions.read", Scope(tenant, definitionId), ct).ConfigureAwait(false);
        return new(resource.LogicalPath, resource.MediaType, bytes);
    }
    private async ValueTask<TenantDefinitionDraftSnapshot> AuthorizedSnapshot(Guid actor, Guid tenant, Guid draftId, string action, CancellationToken ct)
    {
        var snapshot = await definitions.ReadDefinitionDraftSnapshotAsync(tenant, draftId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var scope = Scope(tenant, Guid.Parse(snapshot.Draft.DefinitionId));
        await authorizer.RequireAsync(actor, "agent-definitions." + action, scope, ct).ConfigureAwait(false);
        if (action != "read") await authorizer.RequireAsync(actor, "agent-definitions.read", scope, ct).ConfigureAwait(false);
        return snapshot;
    }
}
