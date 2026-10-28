using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Workflows;
using AgentCore.Domain.Workflows;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantWorkflowCatalogStore
{
    public async ValueTask<WorkflowDefinition?> FindWorkflowAsync(Guid tenant, Guid workflow, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.Set<WorkflowDefinitionRow>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.WorkflowId == workflow, ct);
        return row is null ? null : WorkflowPersistence.Read<WorkflowDefinition>(row.Json);
    }
    public async ValueTask<WorkflowVersion?> FindWorkflowVersionAsync(Guid tenant, Guid workflow, int version, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.Set<WorkflowVersionRow>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.WorkflowId == workflow && r.Version == version, ct);
        return row is null ? null : WorkflowPersistence.Read<WorkflowVersion>(row.Json);
    }
    public async ValueTask<WorkflowPage<WorkflowDefinition>> ListWorkflowsAsync(Guid tenant, IReadOnlyList<Guid>? allowed, int page, int size, CancellationToken ct = default)
    {
        if (page is < 0 or > 100_000 || size is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var query = db.Set<WorkflowDefinitionRow>().AsNoTracking().Where(r => r.TenantId == tenant && (allowed == null || allowed.Contains(r.WorkflowId)));
        var count = await query.CountAsync(ct); var rows = await query.OrderBy(r => r.WorkflowId).Skip(page * size).Take(size).ToArrayAsync(ct);
        await tx.CommitAsync(ct); return new(rows.Select(r => WorkflowPersistence.Read<WorkflowDefinition>(r.Json)).ToArray(), count, page, size);
    }
    public async ValueTask<WorkflowCatalogReceipt?> FindWorkflowCatalogRequestAsync(Guid tenant, Guid actor, string key, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.Set<WorkflowCatalogReceiptRow>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.ActorId == actor && r.RequestKey == key, ct);
        return row is null ? null : WorkflowPersistence.Read<WorkflowCatalogReceipt>(row.Json);
    }
    public async ValueTask<WorkflowPublicationIntent> PrepareWorkflowPublicationAsync(WorkflowCatalogWrite command, CancellationToken ct = default)
    {
        WorkflowCatalogAuthority.Command(command);
        if (command.Version is null || command.Audit.Action != "workflows.publish") throw new ArgumentException("An immutable publication is required.");
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var tenant = command.Definition.TenantId; await RequireHumanSessionAsync(db, tenant, command.Human, ct);
        var state = await ReadTenantAsync(db, tenant, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        await WorkflowCatalogAuthority.RequireAsync(state, command, false, new(this), ct);
        var previous = await db.Set<WorkflowPublicationRow>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.ActorId == command.Human.PrincipalId && r.RequestKey == command.RequestKey, ct);
        if (previous is not null) { var saved = WorkflowPersistence.Read<WorkflowPublicationIntent>(previous.Json); if (saved.Fingerprint != command.Fingerprint) throw new SecurityConflictException("workflow_request_conflict"); return saved; }
        var row = await db.Set<WorkflowDefinitionRow>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.WorkflowId == command.Definition.WorkflowId, ct);
        if (state.Tenant.Revision != command.ExpectedSecurityRevision || row?.Revision != command.ExpectedRevision) throw new SecurityConflictException("stale_revision");
        WorkflowCatalogRules.Change(WorkflowPersistence.Read<WorkflowDefinition>(row.Json), command.Definition, command.Version, command.ExpectedRevision, command.Audit.Action, command.Human.PrincipalId);
        var intent = new WorkflowPublicationIntent(tenant, command.Definition.WorkflowId, command.Human.PrincipalId, command.RequestKey, command.Fingerprint, command.ExpectedRevision, command.Version);
        db.Add(new WorkflowPublicationRow { TenantId = tenant, ActorId = command.Human.PrincipalId, RequestKey = command.RequestKey, WorkflowId = command.Definition.WorkflowId, Json = WorkflowPersistence.Write(intent) }); db.Add(command.Audit);
        await RequireHumanSessionAsync(db, tenant, command.Human, ct); await SaveTaskChanges(db, ct); await tx.CommitAsync(ct); return intent;
    }
    public async ValueTask<WorkflowCatalogReceipt?> WriteWorkflowCatalogAsync(WorkflowCatalogWrite command, CancellationToken ct = default)
    {
        WorkflowCatalogAuthority.Command(command);
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var tenant = command.Definition.TenantId; var id = command.Definition.WorkflowId;
        await RequireHumanSessionAsync(db, tenant, command.Human, ct);
        var state = await ReadTenantAsync(db, tenant, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        var row = await db.Set<WorkflowDefinitionRow>().SingleOrDefaultAsync(r => r.TenantId == tenant && r.WorkflowId == id, ct);
        await WorkflowCatalogAuthority.RequireAsync(state, command, command.Audit.Action == "workflows.create", new(this), ct);
        var previous = await db.Set<WorkflowCatalogReceiptRow>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.ActorId == command.Human.PrincipalId && r.RequestKey == command.RequestKey, ct);
        if (previous is not null)
        {
            var receipt = WorkflowPersistence.Read<WorkflowCatalogReceipt>(previous.Json);
            if (receipt.Fingerprint != command.Fingerprint) throw new SecurityConflictException("workflow_request_conflict");
            return receipt;
        }
        if (state.Tenant.Revision != command.ExpectedSecurityRevision || (row?.Revision ?? 0) != command.ExpectedRevision) return null;
        var before = row is null ? null : WorkflowPersistence.Read<WorkflowDefinition>(row.Json);
        WorkflowCatalogRules.Change(before, command.Definition, command.Version, command.ExpectedRevision, command.Audit.Action, command.Human.PrincipalId);
        if (row is null && await db.Set<WorkflowDefinitionRow>().CountAsync(r => r.TenantId == tenant, ct) >= 200) throw new ArgumentException("Workflow catalog limit reached.");
        if (row is null) db.Add(new WorkflowDefinitionRow { TenantId = tenant, WorkflowId = id, Revision = command.Definition.Revision, Json = WorkflowPersistence.Write(command.Definition) });
        else { row.Revision = command.Definition.Revision; row.Json = WorkflowPersistence.Write(command.Definition); }
        if (command.Version is { } version)
        {
            var publication = await db.Set<WorkflowPublicationRow>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.ActorId == command.Human.PrincipalId && r.RequestKey == command.RequestKey, ct);
            var intent = publication is null ? null : WorkflowPersistence.Read<WorkflowPublicationIntent>(publication.Json);
            if (intent is null || intent.Fingerprint != command.Fingerprint || intent.DraftRevision != command.ExpectedRevision || intent.Version != version)
                throw new AccessDeniedException("workflow_publication_not_prepared");
            db.Add(new WorkflowVersionRow { TenantId = tenant, WorkflowId = id, Version = version.Version, Json = WorkflowPersistence.Write(version) });
        }
        var result = new WorkflowCatalogReceipt(tenant, command.Human.PrincipalId, command.RequestKey, command.Fingerprint, command.Audit.Action,
            command.Definition, command.Version, state.Tenant.Revision + 1);
        db.Add(new WorkflowCatalogReceiptRow { TenantId = tenant, ActorId = command.Human.PrincipalId, WorkflowId = id, RequestKey = command.RequestKey, Json = WorkflowPersistence.Write(result) });
        var trackedTenant = await db.Set<AgentCore.Domain.Security.Tenant>().SingleAsync(t => t.TenantId == tenant, ct);
        db.Entry(trackedTenant).CurrentValues.SetValues(state.Tenant with { Revision = state.Tenant.Revision + 1 }); db.Add(command.Audit);
        await RequireHumanSessionAsync(db, tenant, command.Human, ct);
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return result; }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException error) when (IsConstraint(error)) { return null; }
    }
}
