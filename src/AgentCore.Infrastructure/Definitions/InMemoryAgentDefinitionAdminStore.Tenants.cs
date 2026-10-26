using System.Collections.Concurrent;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;

namespace AgentCore.Infrastructure.Definitions;

public sealed partial class InMemoryAgentDefinitionAdminStore
{
    internal object TenantGate { get; } = new();
    internal ConcurrentDictionary<Guid, TenantDefinition> TenantDefinitions { get; } = new();
    internal bool IsTenantDefinition(string id) => Guid.TryParse(id, out var parsed) && TenantDefinitions.ContainsKey(parsed);
    internal bool IsTenantDraft(Guid id) => _drafts.TryGetValue(id, out var draft) && IsTenantDefinition(draft.DefinitionId);
    internal bool OwnsActiveDefinition(Guid tenant, Guid id) => TenantDefinitions.GetValueOrDefault(id) is { DeletedAt: null } root && root.TenantId == tenant;
    internal AgentDefinitionDraft? TenantDraft(Guid tenant, Guid id) => _drafts.TryGetValue(id, out var draft)
        && Guid.TryParseExact(draft.DefinitionId, "D", out var definition) && OwnsActiveDefinition(tenant, definition) ? AgentDefinitionAdminSnapshots.Freeze(draft) : null;
    internal AgentDefinitionPublication? TenantPublication(Guid tenant, Guid id, int version) => OwnsActiveDefinition(tenant, id)
        && _publications.TryGetValue((id.ToString("D"), version), out var p) ? AgentDefinitionAdminSnapshots.Freeze(p) : null;
    internal IReadOnlyList<AgentDefinitionDraft> TenantDrafts(Guid tenant, Guid id) => !OwnsActiveDefinition(tenant, id) ? []
        : _drafts.Values.Where(d => d.DefinitionId == id.ToString("D")).OrderBy(d => d.DraftId).Select(AgentDefinitionAdminSnapshots.Freeze).ToArray();
    internal IReadOnlyList<AgentDefinitionPublication> TenantPublications(Guid tenant, Guid id) => !OwnsActiveDefinition(tenant, id) ? []
        : _publications.Values.Where(p => p.DefinitionId == id.ToString("D")).OrderBy(p => p.Version).Select(AgentDefinitionAdminSnapshots.Freeze).ToArray();
    internal bool CreateTenantDefinition(TenantDefinition root, AgentDefinitionDraft draft, IReadOnlyList<AgentDefinitionDraftResource> initialResources, CancellationToken ct)
    {
        TenantDefinitionCreationRules.Validate(root, draft, initialResources);
        var frozen = AgentDefinitionAdminSnapshots.Freeze(draft); AgentDefinitionValidator.ValidateCandidate(frozen.Candidate);
        if (TenantDefinitions.ContainsKey(root.DefinitionId) || _drafts.Values.Any(d => d.DefinitionId == draft.DefinitionId)
            || _publications.Keys.Any(k => k.DefinitionId == draft.DefinitionId) || _drafts.ContainsKey(draft.DraftId)) return false;
        foreach (var resource in initialResources)
        {
            if (ResourceStore is null) throw new AccessDeniedException("resource_content_unavailable");
            _ = AgentCore.Application.Admin.DefinitionResourceStoredContent.RequireForBindingAsync(ResourceStore.TenantContent,
                resource.ContentSha256, resource.ByteLength, resource.MediaType, ct).GetAwaiter().GetResult();
        }
        ct.ThrowIfCancellationRequested();
        TenantDefinitions[root.DefinitionId] = root; _drafts[draft.DraftId] = frozen;
        if (initialResources.Count > 0) ResourceStore!.RestoreDraftResources(draft.DraftId, initialResources.ToList());
        return true;
    }
    internal bool SaveTenantDraft(Guid tenant, AgentDefinitionDraft draft, long? expected)
    {
        var frozen = AgentDefinitionAdminSnapshots.Freeze(draft); AgentDefinitionValidator.ValidateCandidate(frozen.Candidate);
        if (frozen.Candidate.DefinitionId != draft.DefinitionId || !Guid.TryParseExact(draft.DefinitionId, "D", out var id)
            || !OwnsActiveDefinition(tenant, id)) throw new AccessDeniedException("resource_unavailable");
        if (expected is null)
        {
            if (_drafts.ContainsKey(draft.DraftId) || draft.Revision != 1 || draft.SourceKind != DefinitionDraftSourceKind.ForkDurable
                || draft.SourceVersion is not { } version || draft.SourceDefinitionId is not null || !_publications.ContainsKey((draft.DefinitionId, version))) return false;
            var resources = ResourceStore?.CopyPublicationResources(draft.DefinitionId, version)?.Select(r => new AgentDefinitionDraftResource(r.ResourceId,
                draft.DraftId, r.LogicalPath, r.Kind, r.MediaType, r.ContentSha256, r.ByteLength, draft.UpdatedAt)).ToList();
            ResourceStore?.RestoreDraftResources(draft.DraftId, resources);
        }
        else if (!_drafts.TryGetValue(draft.DraftId, out var before) || before.DefinitionId != draft.DefinitionId
            || before.Revision != expected || draft.Revision != checked(expected + 1) || !TenantDefinitionCreationRules.SameSource(before, draft)) return false;
        _drafts[draft.DraftId] = frozen; return true;
    }
    internal AgentDefinitionPublication? PublishTenantDraft(Guid tenant, Guid draftId, long expected, DateTimeOffset at, TenantDefinitionEvidence evidence)
    {
        var draft = TenantDraft(tenant, draftId); if (draft is null || draft.Revision != expected) return null;
        var resources = ResourceStore?.CopyDraftResources(draftId) ?? [];
        TenantDefinitionPublicationRules.RequireReady(draft, evidence);
        ResourceStore?.EnsureReadyForPublication(draftId);
        var version = checked(_publications.Keys.Where(k => k.DefinitionId == draft.DefinitionId).Select(k => k.Version).DefaultIfEmpty(0).Max() + 1);
        var payload = TenantDefinitionPublicationRules.FreezeDraftSuite(draft.Candidate.ToPublished(version), evidence.Suites, draftId); AgentDefinitionValidator.Validate(payload);
        var publication = AgentDefinitionAdminSnapshots.Freeze(new AgentDefinitionPublication(draft.DefinitionId, version, payload, draft.Revision, DefinitionPublicationStatus.Active, 1, at));
        ResourceStore?.SnapshotPublicationOnPublish(draftId, draft.DefinitionId, version);
        _publications[(draft.DefinitionId, version)] = publication; _drafts.TryRemove(draftId, out _);
        ResourceStore?.DeleteDraftResources(draftId); EvaluationStore?.DeleteDraftEvaluation(draftId);
        return AgentDefinitionAdminSnapshots.Freeze(publication);
    }
    internal AgentDefinitionPublication? DeprecateTenantPublication(Guid tenant, Guid id, int version, long expected)
    {
        var current = TenantPublication(tenant, id, version); if (current is null || current.MetadataRevision != expected) return null;
        var updated = current with { Status = DefinitionPublicationStatus.Deprecated, MetadataRevision = current.MetadataRevision + 1 };
        _publications[(current.DefinitionId, version)] = AgentDefinitionAdminSnapshots.Freeze(updated); return updated;
    }
}
