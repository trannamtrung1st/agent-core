using AgentCore.Application.Admin;
using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.Definitions;

public sealed partial class InMemoryAgentDefinitionAdminStore
{
    // Caller holds TenantGate and the IAM/security and reference-graph gates.
    internal TenantDefinitionDeleted? DeleteTenantDraft(Guid tenant, Guid draftId, long expected, bool retire, DateTimeOffset at)
    {
        var draft = TenantDraft(tenant, draftId);
        if (draft is null || draft.Revision != expected) return null;
        var id = Guid.Parse(draft.DefinitionId);
        var actualRetire = TenantDrafts(tenant, id).Count == 1 && TenantPublications(tenant, id).Count == 0;
        if (actualRetire != retire) return null;
        _drafts.TryRemove(draftId, out _);
        ResourceStore?.DeleteDraftResources(draftId); EvaluationStore?.DeleteDraftEvaluation(draftId);
        if (retire) TenantDefinitions[id] = TenantDefinitions[id] with { DeletedAt = at };
        return new(retire);
    }
    internal AdminDefinitionDeleteWitness? TenantDeletionWitness(Guid tenant, Guid id)
    {
        if (!OwnsActiveDefinition(tenant, id)) return null;
        return new(TenantDrafts(tenant, id).Select(d => new AdminDraftRevisionWitness(d.DraftId, d.Revision)).ToArray(),
            TenantPublications(tenant, id).Select(p => new AdminPublicationRevisionWitness(p.Version, p.MetadataRevision)).ToArray());
    }
    internal TenantDefinitionDeleted? DeleteTenantDefinition(Guid tenant, Guid id, DateTimeOffset at)
    {
        if (!OwnsActiveDefinition(tenant, id)) return null;
        var drafts = TenantDrafts(tenant, id); var publications = TenantPublications(tenant, id);
        foreach (var draft in drafts) { _drafts.TryRemove(draft.DraftId, out _); EvaluationStore?.DeleteDraftEvaluation(draft.DraftId); }
        foreach (var publication in publications) _publications.TryRemove((publication.DefinitionId, publication.Version), out _);
        ResourceStore?.PurgeDefinition(id.ToString("D"), drafts.Select(d => d.DraftId).ToArray());
        TenantDefinitions[id] = TenantDefinitions[id] with { DeletedAt = at };
        return new(true);
    }
}
