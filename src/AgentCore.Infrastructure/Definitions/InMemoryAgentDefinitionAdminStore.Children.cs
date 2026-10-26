using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;

namespace AgentCore.Infrastructure.Definitions;

public sealed partial class InMemoryAgentDefinitionAdminStore
{
    internal TenantDefinitionDraftSnapshot? TenantSnapshot(Guid tenant, Guid draftId)
    {
        var draft = TenantDraft(tenant, draftId); if (draft is null) return null;
        return new(draft, ResourceStore?.CopyDraftResources(draftId)?.OrderBy(r => r.LogicalPath, StringComparer.Ordinal).ToArray() ?? [],
            [], []);
    }
    internal IReadOnlyList<AgentDefinitionPublicationResource> TenantPublicationResources(Guid tenant, Guid definitionId, int version) =>
        TenantPublication(tenant, definitionId, version) is null ? [] : ResourceStore?.CopyPublicationResources(definitionId.ToString("D"), version)?.OrderBy(r => r.LogicalPath, StringComparer.Ordinal).ToArray() ?? [];
    internal AgentDefinitionDraftResourceBatchBound? BindTenantResources(Guid tenant, Guid draftId, long expected,
        IReadOnlyList<AgentDefinitionDraftResource> incoming, DateTimeOffset at)
    {
        var draft = TenantDraft(tenant, draftId); if (draft is null || draft.Revision != expected) return null;
        var store = ResourceStore ?? throw new AccessDeniedException("resource_content_unavailable");
        var before = store.CopyDraftResources(draftId) ?? [];
        TenantDefinitionChildRules.ValidateResourceBatch(draftId, before, incoming);
        DefinitionResourceBatch.PrepareAsync(store.TenantContent, incoming.Select(r => new AgentDefinitionDraftResourceBatchItem(r.ResourceId,
            r.LogicalPath, r.Kind, r.MediaType, r.ContentSha256, r.ByteLength)).ToArray()).GetAwaiter().GetResult();
        var byId = before.ToDictionary(r => r.ResourceId); foreach (var resource in incoming) byId[resource.ResourceId] = resource;
        var updated = byId.Values.OrderBy(r => r.LogicalPath, StringComparer.Ordinal).ToList();
        store.RestoreDraftResources(draftId, updated); _drafts[draftId] = draft with { Revision = draft.Revision + 1, UpdatedAt = at };
        return new(draft.Revision + 1, updated.ToArray());
    }
    internal AgentDefinitionDraftResourceBatchBound? RemoveTenantResource(Guid tenant, Guid draftId, long expected, Guid resourceId, DateTimeOffset at)
    {
        var draft = TenantDraft(tenant, draftId); if (draft is null || draft.Revision != expected) return null;
        var before = ResourceStore?.CopyDraftResources(draftId) ?? [];
        if (!before.Any(r => r.ResourceId == resourceId)) throw new AccessDeniedException("resource_unavailable");
        var updated = before.Where(r => r.ResourceId != resourceId).ToList(); ResourceStore!.RestoreDraftResources(draftId, updated);
        _drafts[draftId] = draft with { Revision = draft.Revision + 1, UpdatedAt = at }; return new(draft.Revision + 1, updated.ToArray());
    }
}
