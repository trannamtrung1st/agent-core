using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask<bool> OwnsResourceAsync(AccessScope scope, CancellationToken ct = default)
    {
        if (TenantEventAdmissionRules.TryId(scope, out var coreEvent))
        {
            await using var db = await contexts.CreateDbContextAsync(ct);
            return await db.CoreEvents.IgnoreQueryFilters().AnyAsync(e => e.TenantId == scope.TenantId && e.EventId == coreEvent.ToString("D"), ct);
        }
        if (TenantWebhookRules.TryId(scope, out var webhook))
        {
            await using var db = await contexts.CreateDbContextAsync(ct);
            return await db.WebhookEvents.IgnoreQueryFilters().AnyAsync(s => s.TenantId == scope.TenantId && s.ResourceId == webhook.ToString("D"), ct);
        }
        if (TenantAutomationRules.TryId(scope, out var automation))
        {
            await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
            return await db.Automations.IgnoreQueryFilters().AnyAsync(a => a.TenantId == scope.TenantId && a.AutomationId == automation.ToString("D"), ct).ConfigureAwait(false);
        }
        if (TenantWorkspaceRules.TryParse(scope, out _, out _))
        {
            await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
            return await OwnsWorkspace(db, scope, ct).ConfigureAwait(false);
        }
        if (TenantSandboxProfileRules.TryId(scope, out var profile)) return await FindTenantSandboxProfileAsync(scope.TenantId!.Value, profile, ct: ct).ConfigureAwait(false) is not null;
        if (TenantApprovalRules.TryApprovalId(scope, out var approval))
        {
            await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
            return await db.Set<TenantApprovalRecord>().AnyAsync(r => r.TenantId == scope.TenantId && r.RequestId == approval, ct).ConfigureAwait(false);
        }
        if (TenantAttachmentRules.TryId(scope, out var attachment))
        {
            await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
            return await db.Attachments.AnyAsync(a => a.TenantId == scope.TenantId && a.AttachmentId == attachment.ToString("D")
                && db.Set<TenantSessionRecord>().Any(root => root.TenantId == a.TenantId && root.SessionId == a.SessionId
                    && db.Sessions.Any(saved => saved.SessionId == root.SessionId && saved.DurablyDeletedAtUtc == null)), ct).ConfigureAwait(false);
        }
        if (TenantArtifactRules.TryId(scope, out var artifact))
        {
            await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
            return await SqliteArtifactStore.OwnedTenant(db, scope.TenantId!.Value).AnyAsync(a => a.ArtifactId == artifact.ToString("D"), ct).ConfigureAwait(false);
        }
        if (TenantRunRules.TryId(scope, out var run))
        {
            await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
            return await OwnedRuns(db, scope.TenantId!.Value).AnyAsync(r => r.AgentRunId == run.ToString("D"), ct).ConfigureAwait(false);
        }
        if (TenantSessionRules.TryId(scope, out var session))
        {
            await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
            return await OwnedSessions(db, scope.TenantId!.Value).AnyAsync(s => s.SessionId == session.ToString("D"), ct).ConfigureAwait(false);
        }
        if (TenantCredentialRules.TryId(scope, out var credential))
        {
            await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
            return await OwnedCredentials(db, scope.TenantId!.Value).AnyAsync(c => c.CredentialId == credential.ToString("D"), ct).ConfigureAwait(false);
        }
        if (TenantDefinitionResourceScopes.TryParse(scope, out var resource))
        {
            await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
            return await OwnsDefinitionResourceAsync(db, scope.TenantId!.Value, resource, ct).ConfigureAwait(false);
        }
        if (TenantAgentChildScopes.TryParse(scope, out var instance, out _))
        {
            await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            return (await ChildCatalogAsync(db, scope.TenantId!.Value, instance, null, ct).ConfigureAwait(false)).Contains(scope);
        }
        return scope.Kind == AccessScopeKind.Resource && scope.ResourceType == "definition" && scope.TenantId is { } tenant
            && Guid.TryParseExact(scope.ResourceId, "D", out var id) && scope.ResourceId == id.ToString("D")
            && await FindDefinitionAsync(tenant, id, ct).ConfigureAwait(false) is not null;
    }

    public async ValueTask<TenantDefinition?> FindDefinitionAsync(Guid tenantId, Guid definitionId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return (await Root(db, tenantId, definitionId).SingleOrDefaultAsync(ct).ConfigureAwait(false))?.Domain();
    }
    public async ValueTask<TenantDefinitionPage> ListDefinitionsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedIds, int page, int pageSize, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty || page < 0 || page > 100_000 || pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.Set<TenantDefinitionRecord>().AsNoTracking().Where(d => d.TenantId == tenantId && d.DeletedAt == null);
        if (allowedIds is not null) { var allowed = allowedIds.Select(id => id.ToString("D")).ToArray(); query = query.Where(d => allowed.Contains(d.DefinitionId)); }
        var count = await query.CountAsync(ct).ConfigureAwait(false);
        var roots = await query.OrderBy(d => d.DefinitionId).Skip(page * pageSize).Take(pageSize).ToArrayAsync(ct).ConfigureAwait(false);
        var items = new List<TenantDefinitionSummary>();
        foreach (var root in roots)
        {
            var drafts = await db.AgentDefinitionDrafts.IgnoreQueryFilters().AsNoTracking().Where(d => d.DefinitionId == root.DefinitionId).OrderByDescending(d => d.UpdatedAtUtc).ThenBy(d => d.DraftId).ToArrayAsync(ct).ConfigureAwait(false);
            var latest = await db.AgentDefinitionPublications.IgnoreQueryFilters().AsNoTracking().Where(p => p.DefinitionId == root.DefinitionId).OrderByDescending(p => p.Version).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            var name = latest is not null ? AgentDefinitionAdminMapping.MapPublication(latest).Payload.Identity.Name
                : drafts.Length > 0 ? AgentDefinitionAdminMapping.MapDraft(drafts[0]).Candidate.Identity.Name : root.DefinitionId;
            items.Add(new(root.Domain(), drafts.Length, latest?.Version, name));
        }
        return new(items, count, page, pageSize);
    }
    public async ValueTask<IReadOnlyList<AgentDefinitionDraft>> ListDefinitionDraftsAsync(Guid tenantId, Guid definitionId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var id = definitionId.ToString("D");
        var rows = await db.AgentDefinitionDrafts.IgnoreQueryFilters().AsNoTracking().Where(d => d.DefinitionId == id
            && db.Set<TenantDefinitionRecord>().Any(r => r.DefinitionId == d.DefinitionId && r.TenantId == tenantId && r.DeletedAt == null)).OrderBy(d => d.DraftId).ToArrayAsync(ct).ConfigureAwait(false);
        return rows.Select(AgentDefinitionAdminMapping.MapDraft).ToArray();
    }
    public async ValueTask<AgentDefinitionDraft?> FindDefinitionDraftAsync(Guid tenantId, Guid draftId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await Draft(db, tenantId, draftId).AsNoTracking().SingleOrDefaultAsync(ct).ConfigureAwait(false);
        return row is null ? null : AgentDefinitionAdminMapping.MapDraft(row);
    }
    public async ValueTask<AgentDefinitionPublication?> FindDefinitionPublicationAsync(Guid tenantId, Guid definitionId, int version, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await Publication(db, tenantId, definitionId, version).AsNoTracking().SingleOrDefaultAsync(ct).ConfigureAwait(false);
        return row is null ? null : AgentDefinitionAdminMapping.MapPublication(row);
    }
    public async ValueTask<IReadOnlyList<AgentDefinitionPublicationSummary>> ListDefinitionPublicationsAsync(Guid tenantId, Guid definitionId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var id = definitionId.ToString("D");
        var rows = await db.AgentDefinitionPublications.IgnoreQueryFilters().AsNoTracking().Where(p => p.DefinitionId == id
            && db.Set<TenantDefinitionRecord>().Any(r => r.DefinitionId == p.DefinitionId && r.TenantId == tenantId && r.DeletedAt == null)).OrderBy(p => p.Version).ToArrayAsync(ct).ConfigureAwait(false);
        return rows.Select(AgentDefinitionAdminMapping.MapPublication).Select(Summary).ToArray();
    }
    public async ValueTask<bool> TryCreateDefinitionAsync(TenantSecurityState next, long expectedSecurityRevision, TenantDefinition definition,
        AgentDefinitionDraft draft, IReadOnlyList<AgentDefinitionDraftResource> initialResources, AuthorizationAudit audit, CancellationToken ct = default) =>
        await WriteDefinitionAsync(next, expectedSecurityRevision, audit, async db =>
        {
            if (definition.TenantId != next.Tenant.TenantId)
                throw new ArgumentException("Invalid definition ownership.");
            TenantDefinitionCreationRules.Validate(definition, draft, initialResources);
            if (await db.Set<TenantDefinitionRecord>().AnyAsync(d => d.DefinitionId == draft.DefinitionId, ct).ConfigureAwait(false)
                || await db.AgentDefinitionDrafts.IgnoreQueryFilters().AnyAsync(d => d.DefinitionId == draft.DefinitionId, ct).ConfigureAwait(false)
                || await db.AgentDefinitionPublications.IgnoreQueryFilters().AnyAsync(d => d.DefinitionId == draft.DefinitionId, ct).ConfigureAwait(false)) return null;
            AgentDefinitionValidator.ValidateCandidate(draft.Candidate);
            foreach (var resource in initialResources)
            {
                if (definitionContent is null) throw new AccessDeniedException("resource_content_unavailable");
                _ = await AgentCore.Application.Admin.DefinitionResourceStoredContent.RequireForBindingAsync(definitionContent,
                    resource.ContentSha256, resource.ByteLength, resource.MediaType, ct).ConfigureAwait(false);
                db.AgentDefinitionDraftResources.Add(DefinitionResourcePersistence.MapDraftResource(resource));
            }
            var root = TenantDefinitionRecord.Row(definition); db.Add(root); db.AgentDefinitionDrafts.Add(AgentDefinitionAdminMapping.MapDraft(draft));
            return root;
        }, ct).ConfigureAwait(false) is not null;

    public async ValueTask<bool> TrySaveDefinitionDraftAsync(TenantSecurityState next, long expectedSecurityRevision,
        AgentDefinitionDraft draft, long? expectedDraftRevision, AuthorizationAudit audit, CancellationToken ct = default) =>
        await WriteDefinitionAsync(next, expectedSecurityRevision, audit, async db =>
        {
            AgentDefinitionValidator.ValidateCandidate(draft.Candidate);
            if (draft.Candidate.DefinitionId != draft.DefinitionId || !Guid.TryParseExact(draft.DefinitionId, "D", out var definitionId)
                || !await Root(db, next.Tenant.TenantId, definitionId).AnyAsync(ct).ConfigureAwait(false)) throw new AccessDeniedException("resource_unavailable");
            var row = await Draft(db, next.Tenant.TenantId, draft.DraftId).SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (expectedDraftRevision is null)
            {
                if (row is not null || draft.Revision != 1 || draft.SourceKind != DefinitionDraftSourceKind.ForkDurable || draft.SourceVersion is not { } sourceVersion || draft.SourceDefinitionId is not null) return null;
                if (!await Publication(db, next.Tenant.TenantId, definitionId, sourceVersion).AnyAsync(ct).ConfigureAwait(false)) return null;
                db.AgentDefinitionDrafts.Add(AgentDefinitionAdminMapping.MapDraft(draft));
                var resources = await db.AgentDefinitionPublicationResources.IgnoreQueryFilters().AsNoTracking().Where(r => r.DefinitionId == draft.DefinitionId && r.Version == sourceVersion).ToArrayAsync(ct).ConfigureAwait(false);
                foreach (var r in resources) db.AgentDefinitionDraftResources.Add(new() { DraftId = draft.DraftId.ToString("D"), ResourceId = r.ResourceId,
                    LogicalPath = r.LogicalPath, Kind = r.Kind, MediaType = r.MediaType, ContentSha256 = r.ContentSha256, ByteLength = r.ByteLength, UpdatedAtUtc = draft.UpdatedAt.ToUnixTimeMilliseconds() });
            }
            else
            {
                if (row is null || row.DefinitionId != draft.DefinitionId || row.Revision != expectedDraftRevision || draft.Revision != checked(expectedDraftRevision + 1)
                    || !TenantDefinitionCreationRules.SameSource(AgentDefinitionAdminMapping.MapDraft(row), draft)) return null;
                db.Entry(row).CurrentValues.SetValues(AgentDefinitionAdminMapping.MapDraft(draft));
            }
            return draft;
        }, ct).ConfigureAwait(false) is not null;

    public ValueTask<AgentDefinitionPublication?> TryPublishDefinitionDraftAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid draftId, long expectedDraftRevision, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default) =>
        WriteDefinitionAsync(next, expectedSecurityRevision, audit, async db =>
        {
            var row = await Draft(db, next.Tenant.TenantId, draftId).SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (row is null || row.Revision != expectedDraftRevision) return null;
            var draft = AgentDefinitionAdminMapping.MapDraft(row); var key = draftId.ToString("D");
            TenantDefinitionResourceScopes.RequireNoDraftGrants(next, draftId);
            var resources = await db.AgentDefinitionDraftResources.IgnoreQueryFilters().AsNoTracking().Where(r => r.DraftId == key).ToArrayAsync(ct).ConfigureAwait(false);
            var scenarios = await db.AgentDefinitionDraftEvaluationScenarios.IgnoreQueryFilters().AsNoTracking().Where(s => s.DraftId == key).ToArrayAsync(ct).ConfigureAwait(false);
            var results = await LatestDefinitionResults(db, key, ct).ConfigureAwait(false);
            TenantDefinitionPublicationRules.RequireReady(draft, new(resources.Select(DefinitionResourcePersistence.MapDraftResource).ToArray(), scenarios.Select(DefinitionDraftEvaluationPersistence.MapScenario).ToArray(), results.Select(DefinitionDraftEvaluationPersistence.MapResult).ToArray()));
            if (resources.Length > 0)
            {
                if (definitionContent is null) throw new AccessDeniedException("resource_content_unavailable");
                await DefinitionResourcePersistence.VerifyDraftResourcesAsync(db, definitionContent, draftId, ct, includeTenant: true).ConfigureAwait(false);
            }
            var maxVersion = await db.AgentDefinitionPublications.IgnoreQueryFilters().Where(p => p.DefinitionId == draft.DefinitionId).Select(p => (int?)p.Version).MaxAsync(ct).ConfigureAwait(false) ?? 0;
            var version = checked(maxVersion + 1); var payload = draft.Candidate.ToPublished(version); AgentDefinitionValidator.Validate(payload);
            var publication = new AgentDefinitionPublication(draft.DefinitionId, version, payload, draft.Revision, DefinitionPublicationStatus.Active, 1, at);
            db.AgentDefinitionPublications.Add(AgentDefinitionAdminMapping.MapPublication(publication));
            DefinitionResourcePersistence.BindDraftResourcesToPublication(db, draftId, draft.DefinitionId, version, includeTenant: true);
            await db.AgentDefinitionDraftResources.IgnoreQueryFilters().Where(r => r.DraftId == key).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.AgentDefinitionDraftEvaluationScenarios.IgnoreQueryFilters().Where(s => s.DraftId == key).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.AgentDefinitionDraftEvaluationResults.IgnoreQueryFilters().Where(r => r.DraftId == key).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            db.AgentDefinitionDrafts.Remove(row);
            return publication;
        }, ct);

    public ValueTask<AgentDefinitionPublication?> TryDeprecateDefinitionPublicationAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid definitionId, int version, long expectedMetadataRevision, AuthorizationAudit audit, CancellationToken ct = default) =>
        WriteDefinitionAsync(next, expectedSecurityRevision, audit, async db =>
        {
            var row = await Publication(db, next.Tenant.TenantId, definitionId, version).SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (row is null || row.MetadataRevision != expectedMetadataRevision) return null;
            row.Status = (int)DefinitionPublicationStatus.Deprecated; row.MetadataRevision++;
            return AgentDefinitionAdminMapping.MapPublication(row);
        }, ct);

    private async ValueTask<T?> WriteDefinitionAsync<T>(TenantSecurityState next, long expected, AuthorizationAudit audit,
        Func<AgentCoreDbContext, Task<T?>> change, CancellationToken ct) where T : class
    {
        next = IamStateRules.Freeze(next);
        if (audit.TenantId != next.Tenant.TenantId || next.Tenant.Revision != checked(expected + 1)) throw new ArgumentException("Invalid definition transaction.");
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var current = await db.Set<Tenant>().SingleOrDefaultAsync(t => t.TenantId == next.Tenant.TenantId, ct).ConfigureAwait(false);
        if (current is null || current.Revision != expected) return null;
        var before = (await ReadTenantAsync(db, current.TenantId, ct).ConfigureAwait(false))!;
        IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next);
        await ValidateTenantActorAsync(db, before, audit.ActorPrincipalId, ct).ConfigureAwait(false);
        await EnsureActiveAccountOwnerAsync(db, next, null, ct).ConfigureAwait(false);
        await EnsureResourceOwnershipAsync(db, next, ct).ConfigureAwait(false);
        try
        {
            var result = await change(db).ConfigureAwait(false); if (result is null) return null;
            db.Entry(current).CurrentValues.SetValues(next.Tenant); await ApplyTenantChildrenAsync(db, next, ct).ConfigureAwait(false); db.Add(audit);
            await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return result;
        }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException ex) when (IsConstraint(ex)) { return null; }
    }
    private static IQueryable<TenantDefinitionRecord> Root(AgentCoreDbContext db, Guid tenant, Guid id) =>
        db.Set<TenantDefinitionRecord>().Where(d => d.TenantId == tenant && d.DeletedAt == null && d.DefinitionId == id.ToString("D"));
    private static IQueryable<AgentDefinitionDraftRecord> Draft(AgentCoreDbContext db, Guid tenant, Guid draft) =>
        db.AgentDefinitionDrafts.IgnoreQueryFilters().Where(d => d.DraftId == draft.ToString("D") && db.Set<TenantDefinitionRecord>().Any(r => r.TenantId == tenant && r.DeletedAt == null && r.DefinitionId == d.DefinitionId));
    private static IQueryable<AgentDefinitionPublicationRecord> Publication(AgentCoreDbContext db, Guid tenant, Guid id, int version) =>
        db.AgentDefinitionPublications.IgnoreQueryFilters().Where(p => p.DefinitionId == id.ToString("D") && p.Version == version && db.Set<TenantDefinitionRecord>().Any(r => r.TenantId == tenant && r.DeletedAt == null && r.DefinitionId == p.DefinitionId));
    private static AgentDefinitionPublicationSummary Summary(AgentDefinitionPublication p) => new(p.DefinitionId, p.Version, p.Status, p.MetadataRevision, p.PublishedAt,
        p.Payload.SkillList.Select(s => new PublishedSkillReference(s.Id, s.Name, s.RequiredCapabilities)).ToArray());
}
