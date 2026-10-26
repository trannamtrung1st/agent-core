using AgentCore.Application.Admin;
using AgentCore.Domain.Evaluations;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask<TenantDefinitionDraftSnapshot?> ReadDefinitionDraftSnapshotAsync(Guid tenantId, Guid draftId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var snapshot = await DefinitionSnapshot(db, tenantId, draftId, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false); return snapshot;
    }
    public async ValueTask<IReadOnlyList<AgentDefinitionPublicationResource>> ListOwnedPublicationResourcesAsync(Guid tenantId, Guid definitionId, int version, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); var id = definitionId.ToString("D");
        var rows = await db.AgentDefinitionPublicationResources.IgnoreQueryFilters().AsNoTracking().Where(r => r.DefinitionId == id && r.Version == version
            && db.Set<TenantDefinitionRecord>().Any(d => d.TenantId == tenantId && d.DeletedAt == null && d.DefinitionId == r.DefinitionId)).OrderBy(r => r.LogicalPath).ToArrayAsync(ct).ConfigureAwait(false);
        return rows.Select(DefinitionResourcePersistence.MapPublicationResource).ToArray();
    }
    public ValueTask<AgentDefinitionDraftResourceBatchBound?> TryBindDefinitionResourcesAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid draftId, long expectedDraftRevision, IReadOnlyList<AgentDefinitionDraftResource> resources, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null) =>
        WriteDefinitionAsync(next, expectedSecurityRevision, audit, async db =>
        {
            var draft = await Draft(db, next.Tenant.TenantId, draftId).SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (draft is null || draft.Revision != expectedDraftRevision) return null;
            var key = draftId.ToString("D");
            var current = await db.AgentDefinitionDraftResources.IgnoreQueryFilters().Where(r => r.DraftId == key).ToArrayAsync(ct).ConfigureAwait(false);
            TenantDefinitionChildRules.ValidateResourceBatch(draftId, current.Select(DefinitionResourcePersistence.MapDraftResource).ToArray(), resources);
            if (definitionContent is null) throw new AccessDeniedException("resource_content_unavailable");
            await DefinitionResourceBatch.PrepareAsync(definitionContent, resources.Select(r => new AgentDefinitionDraftResourceBatchItem(r.ResourceId,
                r.LogicalPath, r.Kind, r.MediaType, r.ContentSha256, r.ByteLength)).ToArray(), ct).ConfigureAwait(false);
            var byId = current.ToDictionary(r => r.ResourceId);
            // Vacate changing paths within the same transaction so a two-file rename can swap
            // paths without exposing an intermediate manifest or violating the unique index.
            foreach (var resource in resources)
                if (byId.TryGetValue(resource.ResourceId.ToString("D"), out var row) && row.LogicalPath != resource.LogicalPath)
                    row.LogicalPath = ".pending-" + resource.ResourceId.ToString("N");
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            foreach (var resource in resources)
                if (byId.TryGetValue(resource.ResourceId.ToString("D"), out var row)) db.Entry(row).CurrentValues.SetValues(DefinitionResourcePersistence.MapDraftResource(resource));
                else { var added = DefinitionResourcePersistence.MapDraftResource(resource); db.Add(added); byId.Add(added.ResourceId, added); }
            draft.Revision++; draft.UpdatedAtUtc = audit.At.ToUnixTimeMilliseconds();
            return new AgentDefinitionDraftResourceBatchBound(draft.Revision, byId.Values.Select(DefinitionResourcePersistence.MapDraftResource).OrderBy(r => r.LogicalPath, StringComparer.Ordinal).ToArray());
        }, ct, initiator);

    public ValueTask<AgentDefinitionDraftResourceBatchBound?> TryRemoveDefinitionResourceAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid draftId, long expectedDraftRevision, Guid resourceId, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null) =>
        WriteDefinitionAsync(next, expectedSecurityRevision, audit, async db =>
        {
            var draft = await Draft(db, next.Tenant.TenantId, draftId).SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (draft is null || draft.Revision != expectedDraftRevision) return null;
            var key = draftId.ToString("D"); var id = resourceId.ToString("D");
            TenantDefinitionResourceScopes.RequireNoGrant(next, TenantDefinitionResourceScopes.Draft(next.Tenant.TenantId, Guid.Parse(draft.DefinitionId), draftId, resourceId));
            var rows = await db.AgentDefinitionDraftResources.IgnoreQueryFilters().Where(r => r.DraftId == key).ToArrayAsync(ct).ConfigureAwait(false);
            var resource = rows.SingleOrDefault(r => r.ResourceId == id) ?? throw new AccessDeniedException("resource_unavailable");
            db.Remove(resource); draft.Revision++; draft.UpdatedAtUtc = audit.At.ToUnixTimeMilliseconds();
            return new AgentDefinitionDraftResourceBatchBound(draft.Revision, rows.Where(r => r.ResourceId != id).Select(DefinitionResourcePersistence.MapDraftResource).OrderBy(r => r.LogicalPath, StringComparer.Ordinal).ToArray());
        }, ct, initiator);

    private static async Task<TenantDefinitionDraftSnapshot?> DefinitionSnapshot(AgentCoreDbContext db, Guid tenant, Guid draftId, CancellationToken ct)
    {
        var draft = await Draft(db, tenant, draftId).AsNoTracking().SingleOrDefaultAsync(ct).ConfigureAwait(false); if (draft is null) return null;
        var key = draftId.ToString("D");
        var resources = await db.AgentDefinitionDraftResources.IgnoreQueryFilters().AsNoTracking().Where(r => r.DraftId == key).OrderBy(r => r.LogicalPath).ToArrayAsync(ct).ConfigureAwait(false);
        var candidate = AgentDefinitionAdminMapping.MapDraft(draft);
        var suites = new List<EvaluationSuiteVersion>(); if (await Suite(db, tenant, draftId, null, ct) is { } associated) suites.Add(associated);
        foreach (var binding in candidate.Candidate.EvaluationBindings ?? [])
        {
            var suite = await Suite(db, tenant, binding.SuiteId, binding.Version, ct) ?? throw new AccessDeniedException("evaluation_suite_unavailable");
            if (!suites.Any(s => s.SuiteId == suite.SuiteId && s.Version == suite.Version)) suites.Add(suite);
        }
        var evidence = db.Set<EvaluationAttemptRow>().AsNoTracking().Where(a => a.TenantId == tenant && a.SubjectKind == EvaluationSubjectKind.DefinitionDraft && a.SubjectId == draftId && a.CommitSequence > 0);
        var remaining = suites.SelectMany(s => s.Checks.Select(c => (s.SuiteId, c.CheckId))).ToHashSet();
        var attempts = new List<EvaluationAttempt>();
        // Inspect bounded batches, retaining at most one newest witness per current check.
        // Imported per-check receipts remain usable without materializing all historical result bodies.
        for (var offset = 0; remaining.Count > 0; offset += 16)
        {
            var rows = await evidence.OrderByDescending(a => a.CommitSequence).ThenBy(a => a.AttemptId).Skip(offset).Take(16).ToArrayAsync(ct);
            foreach (var row in rows)
            {
                var attempt = EvaluationPersistence.Value(row);
                if (!attempt.Findings.Any(f => remaining.Contains((attempt.SuiteId, f.CheckId)))) continue;
                attempts.Add(attempt);
                foreach (var finding in attempt.Findings) remaining.Remove((attempt.SuiteId, finding.CheckId));
            }
            if (rows.Length < 16) break;
        }
        return new(candidate, resources.Select(DefinitionResourcePersistence.MapDraftResource).ToArray(), suites, attempts);
    }
}
