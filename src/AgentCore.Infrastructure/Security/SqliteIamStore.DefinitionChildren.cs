using AgentCore.Application.Admin;
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
            && db.Set<TenantDefinitionRecord>().Any(d => d.TenantId == tenantId && d.DefinitionId == r.DefinitionId)).OrderBy(r => r.LogicalPath).ToArrayAsync(ct).ConfigureAwait(false);
        return rows.Select(DefinitionResourcePersistence.MapPublicationResource).ToArray();
    }
    public ValueTask<AgentDefinitionDraftResourceBatchBound?> TryBindDefinitionResourcesAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid draftId, long expectedDraftRevision, IReadOnlyList<AgentDefinitionDraftResource> resources, AuthorizationAudit audit, CancellationToken ct = default) =>
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
        }, ct);

    public ValueTask<AgentDefinitionDraftResourceBatchBound?> TryRemoveDefinitionResourceAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid draftId, long expectedDraftRevision, Guid resourceId, AuthorizationAudit audit, CancellationToken ct = default) =>
        WriteDefinitionAsync(next, expectedSecurityRevision, audit, async db =>
        {
            var draft = await Draft(db, next.Tenant.TenantId, draftId).SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (draft is null || draft.Revision != expectedDraftRevision) return null;
            var key = draftId.ToString("D"); var id = resourceId.ToString("D");
            var rows = await db.AgentDefinitionDraftResources.IgnoreQueryFilters().Where(r => r.DraftId == key).ToArrayAsync(ct).ConfigureAwait(false);
            var resource = rows.SingleOrDefault(r => r.ResourceId == id) ?? throw new AccessDeniedException("resource_unavailable");
            db.Remove(resource); draft.Revision++; draft.UpdatedAtUtc = audit.At.ToUnixTimeMilliseconds();
            return new AgentDefinitionDraftResourceBatchBound(draft.Revision, rows.Where(r => r.ResourceId != id).Select(DefinitionResourcePersistence.MapDraftResource).OrderBy(r => r.LogicalPath, StringComparer.Ordinal).ToArray());
        }, ct);

    public ValueTask<TenantDefinitionScenarioSaved?> TrySaveDefinitionScenarioAsync(TenantSecurityState next, long expectedSecurityRevision,
        long expectedDraftRevision, DefinitionEvaluationScenarioUpsert scenario, AuthorizationAudit audit, CancellationToken ct = default) =>
        WriteDefinitionAsync(next, expectedSecurityRevision, audit, async db =>
        {
            TenantDefinitionChildRules.ValidateScenario(scenario);
            var draft = await Draft(db, next.Tenant.TenantId, scenario.DraftId).SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (draft is null || draft.Revision != expectedDraftRevision) return null;
            var key = scenario.DraftId.ToString("D");
            var rows = await db.AgentDefinitionDraftEvaluationScenarios.IgnoreQueryFilters().Where(s => s.DraftId == key).ToArrayAsync(ct).ConfigureAwait(false);
            var before = rows.SingleOrDefault(s => s.ScenarioId == scenario.ScenarioId);
            if (before is null && rows.Length >= TenantDefinitionChildRules.MaxScenarios) throw new ArgumentException("This draft has reached its evaluation scenario limit.");
            var saved = new DefinitionEvaluationScenario(scenario.DraftId, scenario.ScenarioId, (before?.ScenarioVersion ?? 0) + 1,
                scenario.Title, scenario.Prompt, scenario.RequirementLevel, scenario.CheckType, scenario.ToolName, audit.At);
            if (before is null) db.Add(DefinitionDraftEvaluationPersistence.MapScenario(saved));
            else db.Entry(before).CurrentValues.SetValues(DefinitionDraftEvaluationPersistence.MapScenario(saved));
            draft.Revision++; draft.UpdatedAtUtc = audit.At.ToUnixTimeMilliseconds();
            return new TenantDefinitionScenarioSaved(saved, draft.Revision);
        }, ct);
    public ValueTask<TenantDefinitionDraftRevision?> TryRemoveDefinitionScenarioAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid draftId, long expectedDraftRevision, string scenarioId, AuthorizationAudit audit, CancellationToken ct = default) =>
        WriteDefinitionAsync(next, expectedSecurityRevision, audit, async db =>
        {
            var draft = await Draft(db, next.Tenant.TenantId, draftId).SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (draft is null || draft.Revision != expectedDraftRevision) return null;
            var key = draftId.ToString("D");
            var row = await db.AgentDefinitionDraftEvaluationScenarios.IgnoreQueryFilters().SingleOrDefaultAsync(s => s.DraftId == key && s.ScenarioId == scenarioId, ct).ConfigureAwait(false)
                ?? throw new AccessDeniedException("resource_unavailable");
            db.Remove(row); draft.Revision++; draft.UpdatedAtUtc = audit.At.ToUnixTimeMilliseconds();
            return new TenantDefinitionDraftRevision(draft.Revision);
        }, ct);
    public ValueTask<DefinitionEvaluationResult?> TrySaveDefinitionEvaluationAsync(TenantSecurityState next, long expectedSecurityRevision,
        DefinitionEvaluationResult result, AuthorizationAudit audit, CancellationToken ct = default) =>
        WriteDefinitionAsync(next, expectedSecurityRevision, audit, async db =>
        {
            var snapshot = await DefinitionSnapshot(db, next.Tenant.TenantId, result.DraftId, ct).ConfigureAwait(false);
            if (snapshot is null || snapshot.Draft.Revision != result.DraftRevision
                || !snapshot.Scenarios.Any(s => s.ScenarioId == result.ScenarioId && s.ScenarioVersion == result.ScenarioVersion)
                || TenantDefinitionChildRules.Fingerprint(snapshot) != result.ConfigurationFingerprint) return null;
            var committed = result with { CommitRevision = next.Tenant.Revision };
            db.Add(DefinitionDraftEvaluationPersistence.MapResult(committed, audit.AuditId)); return committed;
        }, ct);
    private static async Task<TenantDefinitionDraftSnapshot?> DefinitionSnapshot(AgentCoreDbContext db, Guid tenant, Guid draftId, CancellationToken ct)
    {
        var draft = await Draft(db, tenant, draftId).AsNoTracking().SingleOrDefaultAsync(ct).ConfigureAwait(false); if (draft is null) return null;
        var key = draftId.ToString("D");
        var resources = await db.AgentDefinitionDraftResources.IgnoreQueryFilters().AsNoTracking().Where(r => r.DraftId == key).OrderBy(r => r.LogicalPath).ToArrayAsync(ct).ConfigureAwait(false);
        var scenarios = await db.AgentDefinitionDraftEvaluationScenarios.IgnoreQueryFilters().AsNoTracking().Where(s => s.DraftId == key).OrderBy(s => s.ScenarioId).ToArrayAsync(ct).ConfigureAwait(false);
        var results = await LatestDefinitionResults(db, key, ct).ConfigureAwait(false);
        return new(AgentDefinitionAdminMapping.MapDraft(draft), resources.Select(DefinitionResourcePersistence.MapDraftResource).ToArray(),
            scenarios.Select(DefinitionDraftEvaluationPersistence.MapScenario).ToArray(), results.Select(DefinitionDraftEvaluationPersistence.MapResult).ToArray());
    }
    private static Task<AgentDefinitionDraftEvaluationResultRecord[]> LatestDefinitionResults(AgentCoreDbContext db, string draftId, CancellationToken ct) =>
        db.AgentDefinitionDraftEvaluationResults.IgnoreQueryFilters().AsNoTracking().Where(r => r.DraftId == draftId && db.AgentDefinitionDraftEvaluationScenarios.IgnoreQueryFilters().Any(s => s.DraftId == r.DraftId && s.ScenarioId == r.ScenarioId)).GroupBy(r => r.ScenarioId)
            .Select(group => group.OrderByDescending(r => r.CommitRevision ?? 0).ThenByDescending(r => r.RecordedAtUtc).ThenByDescending(r => r.ResultId).First()).ToArrayAsync(ct);
}
