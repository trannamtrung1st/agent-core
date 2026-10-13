using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using Microsoft.EntityFrameworkCore;
namespace AgentCore.Infrastructure.Persistence;

public sealed class SqliteDriveStore(IDbContextFactory<AgentCoreDbContext> contexts) : IDriveOperationStore, ISyntheticDriveStore
{
    public async ValueTask<DriveOperation?> GetAsync(Guid owner, Guid id, CancellationToken ct)
    { await using var db = await contexts.CreateDbContextAsync(ct); var row = await db.Set<DriveOperationRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.AgentInstanceId == owner.ToString("D") && r.OperationId == id.ToString("D"), ct); return row is null ? null : Map(row); }
    public async ValueTask<DriveOperation?> FindAsync(Guid owner, Guid credential, string hash, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.Set<DriveOperationRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.AgentInstanceId == owner.ToString("D") && r.CredentialId == credential.ToString("D") && r.EffectHash == hash && r.State != "Succeeded" && r.State != "Rejected", ct);
        return row is null ? null : Map(row);
    }
    public async ValueTask<DriveOperation> ClaimAsync(DriveOperation operation, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); var existing = await db.Set<DriveOperationRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.OperationId == operation.OperationId.ToString("D"), ct);
        if (existing is not null) { if (existing.AgentInstanceId != operation.AgentInstanceId.ToString("D") || existing.CredentialId != operation.CredentialId.ToString("D") || existing.EffectHash != operation.EffectHash) throw AgentCoreErrors.Conflict("Drive operation identity is already claimed."); return Map(existing); }
        var row = new DriveOperationRecord(); Apply(row, operation); db.Add(row); try { await db.SaveChangesAsync(ct); } catch (DbUpdateException) { throw AgentCoreErrors.Conflict("A matching Drive operation is already in progress. Inspect it before retrying."); }
        return operation;
    }
    public async ValueTask<DriveOperation> UpdateAsync(DriveOperation operation, long expectedRevision, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); var row = await db.Set<DriveOperationRecord>().SingleOrDefaultAsync(r => r.OperationId == operation.OperationId.ToString("D") && r.AgentInstanceId == operation.AgentInstanceId.ToString("D") && r.CredentialId == operation.CredentialId.ToString("D"), ct) ?? throw AgentCoreErrors.NotFound("Drive operation was not found.");
        if (!DriveOperationTransitions.SameIntent(Map(row), operation) || row.Revision != expectedRevision || operation.Revision != expectedRevision + 1 || row.EffectHash != operation.EffectHash || row.FileId != operation.FileId || row.AuthEpoch != operation.AuthEpoch || row.SnapshotJson != operation.SnapshotJson) throw AgentCoreErrors.Conflict("Drive operation changed.");
        Apply(row, operation); try { await db.SaveChangesAsync(ct); } catch (DbUpdateException) { throw AgentCoreErrors.Conflict("Drive operation changed concurrently."); }
        return operation;
    }
    public async ValueTask DeleteOwnerAsync(Guid id, CancellationToken ct) { await using var db = await contexts.CreateDbContextAsync(ct); await db.Set<DriveOperationRecord>().Where(r => r.AgentInstanceId == id.ToString("D")).ExecuteDeleteAsync(ct); }
    public async ValueTask<DriveFile?> GetAsync(Guid credential, string id, CancellationToken ct) { await using var db = await contexts.CreateDbContextAsync(ct); var row = await db.Set<SyntheticDriveFileRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.CredentialId == credential.ToString("D") && r.FileId == id, ct); return row is null ? null : JsonSerializer.Deserialize<DriveFile>(row.MetadataJson); }
    public async ValueTask<IReadOnlyList<DriveFile>> ListAsync(Guid credential, CancellationToken ct) { await using var db = await contexts.CreateDbContextAsync(ct); return (await db.Set<SyntheticDriveFileRecord>().AsNoTracking().Where(r => r.CredentialId == credential.ToString("D")).OrderBy(r => r.FileId).Take(4096).ToArrayAsync(ct)).Select(r => JsonSerializer.Deserialize<DriveFile>(r.MetadataJson)!).ToArray(); }
    public async ValueTask<byte[]> ReadAsync(Guid credential, string id, CancellationToken ct) { await using var db = await contexts.CreateDbContextAsync(ct); return (await db.Set<SyntheticDriveFileRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.CredentialId == credential.ToString("D") && r.FileId == id, ct))?.Bytes ?? throw AgentCoreErrors.NotFound("Drive file was not found."); }
    public async ValueTask SaveAsync(Guid credential, DriveFile file, byte[]? bytes, string? expectedVersion, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); var row = await db.Set<SyntheticDriveFileRecord>().SingleOrDefaultAsync(r => r.CredentialId == credential.ToString("D") && r.FileId == file.Id, ct);
        if (row is not null && (expectedVersion is null || row.Version != expectedVersion) || row is null && expectedVersion is not null) throw AgentCoreErrors.Conflict("Drive remote revision conflict.");
        if (row is null) { row = new() { CredentialId = credential.ToString("D"), FileId = file.Id }; db.Add(row); }
        row.MetadataJson = JsonSerializer.Serialize(file); row.Version = file.Version; if (bytes is not null) row.Bytes = bytes;
        try { await db.SaveChangesAsync(ct); } catch (DbUpdateException) { throw AgentCoreErrors.Conflict("Drive file changed concurrently."); }
    }
    private static DriveOperation Map(DriveOperationRecord row) => new(Guid.Parse(row.OperationId), Guid.Parse(row.AgentInstanceId), Guid.Parse(row.CredentialId), row.EffectHash, row.AuthEpoch, row.Tool, row.FileId, row.SnapshotJson, row.State, row.Revision, DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAtUtc), row.ResultJson, row.ProtectedUploadSession);
    private static void Apply(DriveOperationRecord row, DriveOperation op) { row.OperationId = op.OperationId.ToString("D"); row.AgentInstanceId = op.AgentInstanceId.ToString("D"); row.CredentialId = op.CredentialId.ToString("D"); row.EffectHash = op.EffectHash; row.AuthEpoch = op.AuthEpoch; row.Tool = op.Tool; row.FileId = op.FileId; row.SnapshotJson = op.SnapshotJson; row.State = op.State; row.Revision = op.Revision; row.CreatedAtUtc = op.CreatedAtUtc.ToUnixTimeMilliseconds(); row.ResultJson = op.ResultJson; row.ProtectedUploadSession = op.ProtectedUploadSession; }
}
