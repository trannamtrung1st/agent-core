using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Admin;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public ValueTask<TenantDefinitionDeleted?> TryDeleteDefinitionDraftAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid draftId, long expectedDraftRevision, bool retireDefinition, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default) =>
        WriteDefinitionAsync(next, expectedSecurityRevision, audit, async db =>
        {
            var draft = await Draft(db, next.Tenant.TenantId, draftId).SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (draft is null || draft.Revision != expectedDraftRevision) return null;
            var id = Guid.Parse(draft.DefinitionId);
            var actualRetire = !await db.AgentDefinitionDrafts.IgnoreQueryFilters().AnyAsync(d => d.DefinitionId == draft.DefinitionId && d.DraftId != draft.DraftId, ct).ConfigureAwait(false)
                && !await db.AgentDefinitionPublications.IgnoreQueryFilters().AnyAsync(p => p.DefinitionId == draft.DefinitionId, ct).ConfigureAwait(false);
            if (retireDefinition != actualRetire) return null;
            TenantDefinitionResourceScopes.RequireNoDraftGrants(next, draftId);
            if (retireDefinition)
            {
                TenantDefinitionDeletionRules.RequireNoResourceGrants(next, id);
                await RequireDefinitionUnreferenced(db, draft.DefinitionId, ct).ConfigureAwait(false);
                await RetireDefinition(db, next.Tenant.TenantId, id, at, ct).ConfigureAwait(false);
            }
            await PurgeDraftChildren(db, [draft.DraftId], ct).ConfigureAwait(false);
            db.AgentDefinitionDrafts.Remove(draft);
            return new TenantDefinitionDeleted(retireDefinition);
        }, ct);

    public ValueTask<TenantDefinitionDeleted?> TryDeleteDefinitionAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid definitionId, AdminDefinitionDeleteWitness witness, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default)
    {
        TenantDefinitionDeletionRules.Validate(witness);
        return WriteDefinitionAsync(next, expectedSecurityRevision, audit, async db =>
        {
            if (!await Root(db, next.Tenant.TenantId, definitionId).AnyAsync(ct).ConfigureAwait(false)) return null;
            var key = definitionId.ToString("D");
            var drafts = await db.AgentDefinitionDrafts.IgnoreQueryFilters().Where(d => d.DefinitionId == key).ToArrayAsync(ct).ConfigureAwait(false);
            var publications = await db.AgentDefinitionPublications.IgnoreQueryFilters().Where(p => p.DefinitionId == key).ToArrayAsync(ct).ConfigureAwait(false);
            if (!TenantDefinitionDeletionRules.Matches(witness, new(drafts.Select(d => new AdminDraftRevisionWitness(Guid.Parse(d.DraftId), d.Revision)).ToArray(),
                publications.Select(p => new AdminPublicationRevisionWitness(p.Version, p.MetadataRevision)).ToArray()))) return null;
            TenantDefinitionDeletionRules.RequireNoResourceGrants(next, definitionId);
            await RequireDefinitionUnreferenced(db, key, ct).ConfigureAwait(false);
            await PurgeDraftChildren(db, drafts.Select(d => d.DraftId).ToArray(), ct).ConfigureAwait(false);
            await db.AgentDefinitionPublicationResources.IgnoreQueryFilters().Where(r => r.DefinitionId == key).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            db.AgentDefinitionDrafts.RemoveRange(drafts); db.AgentDefinitionPublications.RemoveRange(publications);
            await RetireDefinition(db, next.Tenant.TenantId, definitionId, at, ct).ConfigureAwait(false);
            return new TenantDefinitionDeleted(true);
        }, ct);
    }
    private static async Task RequireDefinitionUnreferenced(AgentCoreDbContext db, string id, CancellationToken ct)
    {
        if ((await TenantDeletionReferenceQueries.CountDefinitionAsync(db, id, ct).ConfigureAwait(false)).HasReferences)
            throw new SecurityConflictException("definition_in_use");
    }
    private static async Task RetireDefinition(AgentCoreDbContext db, Guid tenant, Guid id, DateTimeOffset at, CancellationToken ct)
    {
        var root = await Root(db, tenant, id).SingleAsync(ct).ConfigureAwait(false);
        db.Entry(root).CurrentValues.SetValues(root with { DeletedAt = at });
    }
    private static async Task PurgeDraftChildren(AgentCoreDbContext db, string[] keys, CancellationToken ct)
    {
        await db.AgentDefinitionDraftResources.IgnoreQueryFilters().Where(r => keys.Contains(r.DraftId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.AgentDefinitionDraftEvaluationScenarios.IgnoreQueryFilters().Where(s => keys.Contains(s.DraftId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.AgentDefinitionDraftEvaluationResults.IgnoreQueryFilters().Where(r => keys.Contains(r.DraftId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }
}
