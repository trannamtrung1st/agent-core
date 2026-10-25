using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Models;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantModelStore
{
    private readonly IIdGenerator modelIdSource = new AgentCore.Infrastructure.Identity.SystemIdGenerator(clock ?? TimeProvider.System);
    public async ValueTask<ModelCatalogSourceSnapshot> ReadModelCatalogSourceAsync(Guid tenant, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var owner = await db.Set<Tenant>().AsNoTracking().SingleOrDefaultAsync(t => t.TenantId == tenant, ct);
        if (owner?.Status != IdentityStatus.Active) throw new AccessDeniedException("tenant_unavailable");
        var connections = (await db.Set<ProviderConnectionRecord>().AsNoTracking().Where(c => c.TenantId == tenant).ToArrayAsync(ct))
            .Select(ModelManagementPersistence.Domain).ToDictionary(c => c.ConnectionId);
        var offerings = (await db.Set<ModelOfferingRecord>().AsNoTracking().Where(o => o.TenantId == tenant).ToArrayAsync(ct)).Select(ModelManagementPersistence.Domain);
        var credentialIds = connections.Values.Where(c => c.CredentialId is not null).Select(c => c.CredentialId!.Value.ToString("D")).Distinct().ToArray();
        // Metadata only: a selectable catalog read never loads or unprotects credential material.
        var credentials = await OwnedCredentials(db, tenant).AsNoTracking().Where(c => credentialIds.Contains(c.CredentialId)
            && c.Purpose == "ModelInference" && c.Kind == "ApiKey" && c.Status == "Active")
            .Select(c => new { c.CredentialId, c.Revision, c.SecretRevision }).ToDictionaryAsync(c => c.CredentialId, ct);
        var routes = new List<ModelRouteSnapshot>();
        foreach (var offering in offerings)
        {
            if (!connections.TryGetValue(offering.ConnectionId, out var connection)) continue;
            var credential = connection.CredentialId is { } id ? credentials.GetValueOrDefault(id.ToString("D")) : null;
            if (!connection.NoCredential && credential is null) continue;
            routes.Add(new(owner.Revision, connection, offering, new(connection.CredentialId, credential?.Revision, credential?.SecretRevision)));
        }
        var row = await db.Set<TenantModelSettingsRecord>().AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == tenant, ct);
        await tx.CommitAsync(ct);
        return new(tenant, owner.Revision, row is null ? new(tenant, null, null, 0) : ModelManagementPersistence.Domain(row), Array.AsReadOnly(routes.ToArray()));
    }
    public async ValueTask<ModelRouteSnapshot?> FindModelRouteAsync(Guid tenant, Guid offeringId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var owner = await db.Set<Tenant>().AsNoTracking().SingleOrDefaultAsync(t => t.TenantId == tenant, ct);
        var row = await db.Set<ModelOfferingRecord>().AsNoTracking().SingleOrDefaultAsync(o => o.TenantId == tenant && o.OfferingId == offeringId, ct);
        var connectionRow = row is null ? null : await db.Set<ProviderConnectionRecord>().AsNoTracking().SingleOrDefaultAsync(c => c.TenantId == tenant && c.ConnectionId == row.ConnectionId, ct);
        if (owner is null || row is null || connectionRow is null) return null;
        var connection = ModelManagementPersistence.Domain(connectionRow); var credential = await ModelCredential(db, connection, ct);
        TenantModelRules.RequireCredential(connection, credential);
        var result = new ModelRouteSnapshot(owner.Revision, connection, ModelManagementPersistence.Domain(row), new(connection.CredentialId, credential?.Value.Revision, credential?.Value.SecretRevision));
        await tx.CommitAsync(ct); return result;
    }
    public async ValueTask<TenantModelState> ReadTenantModelsAsync(Guid tenant, CancellationToken ct = default)
    {
        if (tenant == Guid.Empty) throw new ArgumentException("Tenant is required.");
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var connections = await db.Set<ProviderConnectionRecord>().AsNoTracking().Where(c => c.TenantId == tenant).OrderBy(c => c.ConnectionId).ToArrayAsync(ct);
        var offerings = await db.Set<ModelOfferingRecord>().AsNoTracking().Where(o => o.TenantId == tenant).OrderBy(o => o.OfferingId).ToArrayAsync(ct);
        var settings = await db.Set<TenantModelSettingsRecord>().AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == tenant, ct);
        await tx.CommitAsync(ct);
        return new(tenant, Array.AsReadOnly(connections.Select(ModelManagementPersistence.Domain).ToArray()),
            Array.AsReadOnly(offerings.Select(ModelManagementPersistence.Domain).ToArray()), settings is null ? new(tenant, null, null, 0) : ModelManagementPersistence.Domain(settings));
    }
    public async ValueTask<IReadOnlyList<ModelPreset>> ReadModelPresetsAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return Array.AsReadOnly((await db.Set<ModelPresetRecord>().AsNoTracking().OrderBy(p => p.PresetId).ToArrayAsync(ct)).Select(ModelManagementPersistence.Domain).ToArray());
    }
    public async ValueTask<IReadOnlyList<ModelPresetSummary>> ReadModelPresetSummariesAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var presets = await db.Set<ModelPresetRecord>().AsNoTracking().OrderBy(p => p.PresetId).ToArrayAsync(ct);
        var references = await db.Set<ModelOfferingRecord>().AsNoTracking().Where(o => o.PresetId != null)
            .GroupBy(o => o.PresetId!.Value).Select(g => new { PresetId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.PresetId, g => g.Count, ct);
        await tx.CommitAsync(ct);
        return Array.AsReadOnly(presets.Select(p => new ModelPresetSummary(ModelManagementPersistence.Domain(p), references.GetValueOrDefault(p.PresetId))).ToArray());
    }
    public async ValueTask<bool> TrySaveModelPresetAsync(ModelPreset value, long expectedRevision, HumanSessionReference human,
        AuthorizationAudit audit, CancellationToken ct = default)
    {
        value = ModelManagementRules.Freeze(value); TenantModelRules.Audit(human, audit, null, "system.model-presets.manage", value.PresetId);
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        await RequireSystemHumanAsync(db, human, "system.model-presets.manage", ct);
        var old = await db.Set<ModelPresetRecord>().SingleOrDefaultAsync(p => p.PresetId == value.PresetId, ct);
        if ((old?.Revision ?? 0) != expectedRevision) return false;
        if (value.Revision != checked(expectedRevision + 1) || old is not null && (old.Kind != value.Kind || old.ExternalModelId != value.ExternalModelId))
            throw new ArgumentException("Preset identity is immutable.");
        if (old is null) db.Add(ModelManagementPersistence.Row(value)); else db.Entry(old).CurrentValues.SetValues(ModelManagementPersistence.Row(value));
        db.Add(audit);
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
        catch (DbUpdateException e) when (IsConstraint(e)) { return false; }
    }
    public ValueTask<bool> TrySaveProviderConnectionAsync(TenantSecurityState next, long expectedSecurityRevision,
        ProviderConnection value, long expectedRevision, HumanSessionReference human, AuthorizationAudit audit, CancellationToken ct = default)
    {
        value = ModelManagementRules.Freeze(value); TenantModelRules.Audit(human, audit, value.TenantId, "model-connections.manage", value.ConnectionId);
        RequireModelOwner(next, value.TenantId);
        return WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db =>
        {
            var old = await db.Set<ProviderConnectionRecord>().SingleOrDefaultAsync(c => c.ConnectionId == value.ConnectionId, ct);
            if ((old?.Revision ?? 0) != expectedRevision) return false;
            TenantModelRules.ConnectionChange(value, old is null ? null : ModelManagementPersistence.Domain(old), expectedRevision);
            if (!value.Enabled && await (from s in db.Set<TenantModelSettingsRecord>() join o in db.Set<ModelOfferingRecord>() on s.DefaultOfferingId equals o.OfferingId
                where s.TenantId == value.TenantId && o.ConnectionId == value.ConnectionId select s).AnyAsync(ct)) throw new SecurityConflictException("model_connection_is_default");
            TenantModelRules.RequireCredential(value, await ModelCredential(db, value, ct), requireActive: value.Enabled);
            if (old is null) db.Add(ModelManagementPersistence.Row(value)); else db.Entry(old).CurrentValues.SetValues(ModelManagementPersistence.Row(value));
            return true;
        }, ct, human);
    }
    public ValueTask<bool> TrySaveModelOfferingAsync(TenantSecurityState next, long expectedSecurityRevision,
        ModelOffering value, long expectedRevision, HumanSessionReference human, AuthorizationAudit audit, CancellationToken ct = default)
    {
        value = ModelManagementRules.Freeze(value); TenantModelRules.Audit(human, audit, value.TenantId, "models.manage", value.OfferingId);
        RequireModelOwner(next, value.TenantId);
        return WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db =>
        {
            var old = await db.Set<ModelOfferingRecord>().SingleOrDefaultAsync(o => o.OfferingId == value.OfferingId, ct);
            if ((old?.Revision ?? 0) != expectedRevision) return false;
            var c = await db.Set<ProviderConnectionRecord>().SingleOrDefaultAsync(c => c.ConnectionId == value.ConnectionId && c.TenantId == value.TenantId, ct);
            var p = value.PresetId is null ? null : await db.Set<ModelPresetRecord>().SingleOrDefaultAsync(p => p.PresetId == value.PresetId, ct);
            TenantModelRules.OfferingChange(value, old is null ? null : ModelManagementPersistence.Domain(old), expectedRevision,
                c is null ? null : ModelManagementPersistence.Domain(c), p is null ? null : ModelManagementPersistence.Domain(p));
            if ((!value.Enabled || value.Retired) && await db.Set<TenantModelSettingsRecord>().AnyAsync(s => s.TenantId == value.TenantId && s.DefaultOfferingId == value.OfferingId, ct))
                throw new SecurityConflictException("model_offering_is_default");
            var settings = await db.Set<TenantModelSettingsRecord>().SingleOrDefaultAsync(s => s.TenantId == value.TenantId && s.DefaultOfferingId == value.OfferingId, ct);
            if (settings is not null && c is not null)
            {
                var connection = ModelManagementPersistence.Domain(c);
                TenantModelRules.RequireDefault(ModelManagementPersistence.Domain(settings), value, connection, await ModelCredential(db, connection, ct));
            }
            if (old is null) db.Add(ModelManagementPersistence.Row(value)); else db.Entry(old).CurrentValues.SetValues(ModelManagementPersistence.Row(value));
            return true;
        }, ct, human);
    }
    public ValueTask<bool> TrySaveTenantModelSettingsAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantModelSettings value, long expectedRevision, HumanSessionReference human, AuthorizationAudit audit, CancellationToken ct = default)
    {
        value = ModelManagementRules.Freeze(value); TenantModelRules.Audit(human, audit, value.TenantId, "models.manage", value.TenantId);
        RequireModelOwner(next, value.TenantId);
        return WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db =>
        {
            var old = await db.Set<TenantModelSettingsRecord>().SingleOrDefaultAsync(s => s.TenantId == value.TenantId, ct);
            if ((old?.Revision ?? 0) != expectedRevision) return false;
            if (value.Revision != checked(expectedRevision + 1)) throw new ArgumentException("Invalid model settings revision.");
            var o = value.DefaultOfferingId is null ? null : await db.Set<ModelOfferingRecord>().SingleOrDefaultAsync(o => o.TenantId == value.TenantId && o.OfferingId == value.DefaultOfferingId, ct);
            var c = o is null ? null : await db.Set<ProviderConnectionRecord>().SingleOrDefaultAsync(c => c.TenantId == value.TenantId && c.ConnectionId == o.ConnectionId, ct);
            var connection = c is null ? null : ModelManagementPersistence.Domain(c);
            TenantModelRules.RequireDefault(value, o is null ? null : ModelManagementPersistence.Domain(o), connection, connection is null ? null : await ModelCredential(db, connection, ct));
            if (old is null) db.Add(ModelManagementPersistence.Row(value)); else db.Entry(old).CurrentValues.SetValues(ModelManagementPersistence.Row(value));
            return true;
        }, ct, human);
    }
    private static void RequireModelOwner(TenantSecurityState next, Guid tenant)
    { if (next.Tenant.TenantId != tenant) throw new ArgumentException("Invalid model owner."); }
    public ValueTask<bool> TryApplyModelMetadataAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid connectionId, long expectedConnectionRevision, ModelCredentialWitness credential, DateTimeOffset at,
        ModelMetadataResult result, bool discover, HumanSessionReference human, AuthorizationAudit audit, CancellationToken ct = default)
    {
        var observed = ModelDiscoveryReconciliation.Freeze(result);
        TenantModelRules.Audit(human, audit, next.Tenant.TenantId, "model-connections.test", connectionId);
        return WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db =>
        {
            var row = await db.Set<ProviderConnectionRecord>().SingleOrDefaultAsync(c => c.ConnectionId == connectionId && c.TenantId == next.Tenant.TenantId, ct);
            if (row is null || row.Revision != expectedConnectionRevision || !row.Enabled) return false;
            var connection = ModelManagementPersistence.Domain(row); var current = await ModelCredential(db, connection, ct);
            TenantModelRules.RequireCredential(connection, current);
            if (credential != new ModelCredentialWitness(connection.CredentialId, current?.Value.Revision, current?.Value.SecretRevision)) return false;
            if (result.Success && discover)
            {
                var previous = await db.Set<ModelOfferingRecord>().Where(o => o.TenantId == connection.TenantId && o.ConnectionId == connectionId).ToArrayAsync(ct);
                var known = previous.ToDictionary(o => o.OfferingId);
                var updated = ModelDiscoveryReconciliation.Reconcile(connection, previous.Select(ModelManagementPersistence.Domain).ToArray(), observed, at, modelIdSource.NewId, result.Result == "synthetic_metadata_available");
                foreach (var offering in updated)
                    if (known.TryGetValue(offering.OfferingId, out var old)) db.Entry(old).CurrentValues.SetValues(ModelManagementPersistence.Row(offering));
                    else db.Add(ModelManagementPersistence.Row(offering));
            }
            db.Entry(row).CurrentValues.SetValues(row with { LastTestAt = at, LastTestResult = result.Result }); return true;
        }, ct, human);
    }
    private static async Task<TenantCredential?> ModelCredential(AgentCoreDbContext db, ProviderConnection connection, CancellationToken ct)
    {
        if (connection.CredentialId is null) return null;
        var row = await OwnedCredentials(db, connection.TenantId).Include(c => c.EmailAccount).Include(c => c.DriveAccount).SingleOrDefaultAsync(c => c.CredentialId == connection.CredentialId.Value.ToString("D"), ct);
        return row is null ? null : await CredentialView(db, connection.TenantId, row, ct);
    }
    private static async Task<bool> OwnsModelResourceAsync(AgentCoreDbContext db, AccessScope scope, CancellationToken ct)
    {
        if (!TenantModelRules.TryId(scope, out var id)) return false;
        return scope.ResourceType == "model-connection"
            ? await db.Set<ProviderConnectionRecord>().AnyAsync(c => c.TenantId == scope.TenantId && c.ConnectionId == id, ct)
            : await db.Set<ModelOfferingRecord>().AnyAsync(o => o.TenantId == scope.TenantId && o.OfferingId == id, ct);
    }
}
