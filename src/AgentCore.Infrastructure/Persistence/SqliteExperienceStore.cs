using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Admin;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Experience;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed class SqliteExperienceStore(IDbContextFactory<AgentCoreDbContext> contexts) : IExperienceStore
{
    public async ValueTask<ExperienceSettings> SettingsAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var r = await db.ExperienceSettings.AsNoTracking().SingleOrDefaultAsync(r => r.AgentInstanceId == id.ToString("D"), ct);
        return r is null ? new(id, false, 0) : new(id, r.Enabled, r.Revision);
    }
    public async ValueTask<ExperienceSettings> ConfigureAsync(Guid id, long expectedRevision, bool enabled, CancellationToken ct = default, AdminEventAppend? audit = null)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (expectedRevision < 0) throw AgentCoreErrors.Validation("Experience revision is invalid.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (expectedRevision == 0)
        {
            db.ExperienceSettings.Add(new() { AgentInstanceId = id.ToString("D"), Enabled = enabled, Revision = 1 });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { throw AgentCoreErrors.Conflict("Experience settings revision is stale."); }
        }
        else if (await db.ExperienceSettings.Where(r => r.AgentInstanceId == id.ToString("D") && r.Revision == expectedRevision)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Enabled, enabled).SetProperty(r => r.Revision, expectedRevision + 1), ct) != 1)
            throw AgentCoreErrors.Conflict("Experience settings revision is stale.");
        if (audit is not null) { AdminEventPersistence.StageAppend(db, audit, Guid.NewGuid()); await db.SaveChangesAsync(ct); }
        await tx.CommitAsync(ct);
        return new(id, enabled, expectedRevision + 1);
    }
    public async ValueTask<AgentExperience> AdmitAsync(AgentExperience p, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var existing = await FindSourceAsync(db, p, ct);
        if (existing is not null) return Decode(existing);
        db.Experiences.Add(new() { ExperienceId = p.ExperienceId.ToString("D"), AgentInstanceId = p.AgentInstanceId.ToString("D"),
            ProfileId = p.ProfileId.ToString("D"), SourceKind = (int)p.SourceKind, SourceId = p.SourceId.ToString("D"),
            ThroughCursor = p.ThroughCursor, CreatedAtUtc = p.CreatedAtUtc.ToUnixTimeMilliseconds(), PayloadJson = JsonSerializer.Serialize(p), Revision = p.Revision });
        try { await db.SaveChangesAsync(ct); return p; }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear(); existing = await FindSourceAsync(db, p, ct);
            return existing is not null ? Decode(existing) : throw AgentCoreErrors.Conflict("Experience source is already owned.");
        }
    }
    public async ValueTask<AgentExperience?> GetAsync(Guid id, Guid recordId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var r = await db.Experiences.AsNoTracking().SingleOrDefaultAsync(r => r.AgentInstanceId == id.ToString("D") && r.ExperienceId == recordId.ToString("D"), ct);
        return r is null ? null : Decode(r);
    }
    public async ValueTask<IReadOnlyList<AgentExperience>> ListAsync(Guid id, int limit, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return (await db.Experiences.AsNoTracking().Where(r => r.AgentInstanceId == id.ToString("D"))
            .OrderByDescending(r => r.CreatedAtUtc).ThenBy(r => r.ExperienceId).Take(Math.Clamp(limit, 1, 100)).ToListAsync(ct)).Select(Decode).ToArray();
    }
    public async ValueTask<IReadOnlyList<AgentExperience>> PendingAsync(int limit, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        // Only repair requests without a WorkItem; terminal failed work owns its status and cannot starve admission gaps.
        var rows = await db.Experiences.FromSqlRaw("SELECT * FROM Experiences WHERE json_extract(PayloadJson, '$.Content') IS NULL AND json_extract(PayloadJson, '$.Visibility') <> 2 AND NOT EXISTS (SELECT 1 FROM WorkItems WHERE WorkItemId = json_extract(Experiences.PayloadJson, '$.GenerationWorkItemId'))")
            .AsNoTracking().OrderBy(r => r.CreatedAtUtc).Take(Math.Clamp(limit, 1, 100)).ToListAsync(ct);
        return rows.Select(Decode).ToArray();
    }
    public async ValueTask<AgentExperience> CompleteAsync(Guid id, Guid recordId, ExperienceContent content, CancellationToken ct = default)
    {
        content.Validate();
        // A visibility mutation racing generation wins; generation never restores a reset/deleted record.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var r = await GetAsync(id, recordId, ct) ?? throw AgentCoreErrors.NotFound("Experience was not found.");
            if (r.Content is not null || r.Visibility == ExperienceVisibility.Deleted) return r;
            var saved = r with { Content = content, Revision = r.Revision + 1 };
            if (await SaveCasAsync(saved, r.Revision, ct)) return saved;
        }
        throw AgentCoreErrors.Conflict("Experience generation conflicted with a reset.");
    }
    public async ValueTask<AgentExperience> SetVisibilityAsync(Guid id, Guid recordId, long expectedRevision, ExperienceVisibility visibility, CancellationToken ct = default, AdminEventAppend? audit = null)
    {
        var r = await GetAsync(id, recordId, ct) ?? throw AgentCoreErrors.NotFound("Experience was not found.");
        if (r.Visibility == ExperienceVisibility.Deleted) throw AgentCoreErrors.Validation("Deleted experience cannot be restored.");
        var saved = r with { Visibility = visibility, Content = visibility == ExperienceVisibility.Deleted ? null : r.Content, Revision = r.Revision + 1 };
        if (r.Revision != expectedRevision || !await SaveCasAsync(saved, expectedRevision, ct, audit)) throw AgentCoreErrors.Conflict("Experience revision is stale.");
        return saved;
    }
    public async ValueTask ResetAsync(Guid id, CancellationToken ct = default, AdminEventAppend? audit = null)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.Experiences.Where(r => r.AgentInstanceId == id.ToString("D")).ToListAsync(ct);
        foreach (var row in rows)
        {
            var r = Decode(row) with { Visibility = ExperienceVisibility.Deleted, Content = null, Revision = row.Revision + 1 };
            row.PayloadJson = JsonSerializer.Serialize(r); row.Revision = r.Revision;
        }
        if (audit is not null) AdminEventPersistence.StageAppend(db, audit, Guid.NewGuid());
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    private async Task<bool> SaveCasAsync(AgentExperience r, long expected, CancellationToken ct, AdminEventAppend? audit = null)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var changed = await db.Experiences.Where(row => row.ExperienceId == r.ExperienceId.ToString("D") && row.AgentInstanceId == r.AgentInstanceId.ToString("D") && row.Revision == expected)
            .ExecuteUpdateAsync(s => s.SetProperty(row => row.PayloadJson, JsonSerializer.Serialize(r, (JsonSerializerOptions?)null))
                .SetProperty(row => row.Revision, r.Revision), ct) == 1;
        if (changed && audit is not null) { AdminEventPersistence.StageAppend(db, audit, Guid.NewGuid()); await db.SaveChangesAsync(ct); }
        await tx.CommitAsync(ct);
        return changed;
    }
    private static Task<ExperienceRecord?> FindSourceAsync(AgentCoreDbContext db, AgentExperience p, CancellationToken ct) =>
        db.Experiences.AsNoTracking().SingleOrDefaultAsync(r => r.AgentInstanceId == p.AgentInstanceId.ToString("D") && r.SourceKind == (int)p.SourceKind
            && r.SourceId == p.SourceId.ToString("D") && r.ThroughCursor == p.ThroughCursor, ct);
    private static AgentExperience Decode(ExperienceRecord r) => JsonSerializer.Deserialize<AgentExperience>(r.PayloadJson)!;
}
