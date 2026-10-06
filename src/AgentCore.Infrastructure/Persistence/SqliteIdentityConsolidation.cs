using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Memory;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class SqliteStructuredMemoryStore
{
    public async ValueTask<StructuredMemoryItem> ConsolidateAsync(IReadOnlyList<StructuredMemoryItem> sources,
        StructuredMemoryItem result, CancellationToken ct = default)
    {
        IdentityConsolidationSemantics.Memory(sources, result);
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var key = result.MemoryId.ToString("D");
        var established = await db.StructuredMemories.AsNoTracking().SingleOrDefaultAsync(r => r.MemoryId == key, ct);
        if (established is not null)
        {
            var existing = Map(established);
            if (!IdentityConsolidationSemantics.SameOwner(existing, result) || existing.Kind != result.Kind
                || existing.Subject != result.Subject || existing.Content != result.Content || existing.Status == MemoryItemStatus.Deleted
                || !(existing.Provenance.DerivedFromMemoryIds ?? []).SequenceEqual(result.Provenance.DerivedFromMemoryIds!))
                throw AgentCoreErrors.Conflict("Consolidation result is no longer available.");
            return existing;
        }
        var ids = sources.Select(s => s.MemoryId.ToString("D")).ToArray();
        var rows = await db.StructuredMemories.Where(r => ids.Contains(r.MemoryId)).ToListAsync(ct);
        if (rows.Count != sources.Count || rows.Any(r => !IdentityConsolidationSemantics.MemoryUnchanged(Map(r), sources.Single(s => s.MemoryId.ToString("D") == r.MemoryId))))
            throw AgentCoreErrors.Conflict("Memory sources changed; reload before consolidating.");
        // Persist supersession before insert so the filtered unique subject index sees the replacement.
        foreach (var row in rows) { row.Status = (int)MemoryItemStatus.Superseded; row.UpdatedAtUtc = result.UpdatedAt.ToUnixTimeMilliseconds(); }
        await db.SaveChangesAsync(ct);
        db.StructuredMemories.Add(Map(result));
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return result; }
        catch (DbUpdateException) { throw AgentCoreErrors.Conflict("An unrelated active memory already uses this subject."); }
    }
}

public sealed partial class SqliteExperienceStore
{
    public async ValueTask<IdentityMaintenanceSettings> MaintenanceSettingsAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.IdentityMaintenanceSettings.AsNoTracking().SingleOrDefaultAsync(r => r.AgentInstanceId == id.ToString("D"), ct);
        return row is null ? new(id, false, 0) : new(id, row.AllowAgentConsolidation, row.Revision);
    }
    public async ValueTask<IdentityMaintenanceSettings> ConfigureMaintenanceAsync(Guid id, long expectedRevision, bool allow,
        CancellationToken ct = default, AdminEventAppend? audit = null)
    {
        if (expectedRevision < 0) throw AgentCoreErrors.Validation("Maintenance revision is invalid.");
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (expectedRevision == 0)
        {
            db.IdentityMaintenanceSettings.Add(new() { AgentInstanceId = id.ToString("D"), AllowAgentConsolidation = allow, Revision = 1 });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { throw AgentCoreErrors.Conflict("Maintenance settings revision is stale."); }
        }
        else if (await db.IdentityMaintenanceSettings.Where(r => r.AgentInstanceId == id.ToString("D") && r.Revision == expectedRevision)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.AllowAgentConsolidation, allow).SetProperty(r => r.Revision, expectedRevision + 1), ct) != 1)
            throw AgentCoreErrors.Conflict("Maintenance settings revision is stale.");
        if (audit is not null) { AdminEventPersistence.StageAppend(db, audit, Guid.NewGuid()); await db.SaveChangesAsync(ct); }
        await tx.CommitAsync(ct);
        return new(id, allow, expectedRevision + 1);
    }
    public async ValueTask<AgentExperience> ConsolidateAsync(IReadOnlyList<AgentExperience> sources, AgentExperience result, CancellationToken ct = default)
    {
        IdentityConsolidationSemantics.Experience(sources, result);
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var key = result.ExperienceId.ToString("D");
        var established = await db.Experiences.AsNoTracking().SingleOrDefaultAsync(r => r.ExperienceId == key, ct);
        if (established is not null)
        {
            var existing = Decode(established);
            if (existing.AgentInstanceId != result.AgentInstanceId || existing.ProfileId != result.ProfileId
                || existing.Visibility is ExperienceVisibility.Deleted or ExperienceVisibility.Suppressed
                    || System.Text.Json.JsonSerializer.Serialize(existing.Content) != System.Text.Json.JsonSerializer.Serialize(result.Content)
                || !(existing.DerivedFromExperienceIds ?? []).SequenceEqual(result.DerivedFromExperienceIds!))
                throw AgentCoreErrors.Conflict("Consolidation result is no longer available.");
            return existing;
        }
        foreach (var source in sources)
        {
            var current = await db.Experiences.AsNoTracking().SingleOrDefaultAsync(r => r.ExperienceId == source.ExperienceId.ToString("D"), ct);
            if (current is null || Decode(current) is not { Visibility: ExperienceVisibility.Eligible, Content: not null } r
                || r.AgentInstanceId != result.AgentInstanceId || r.ProfileId != result.ProfileId || r.Revision != source.Revision)
                throw AgentCoreErrors.Conflict("Experience sources changed; reload before consolidating.");
            var superseded = r with { Visibility = ExperienceVisibility.Superseded, Revision = r.Revision + 1 };
            if (await db.Experiences.Where(row => row.ExperienceId == current.ExperienceId && row.Revision == r.Revision)
                .ExecuteUpdateAsync(s => s.SetProperty(row => row.PayloadJson, JsonSerializer.Serialize(superseded, (JsonSerializerOptions?)null))
                    .SetProperty(row => row.Revision, superseded.Revision), ct) != 1)
                throw AgentCoreErrors.Conflict("Experience sources changed; reload before consolidating.");
        }
        db.Experiences.Add(new() { ExperienceId = key, AgentInstanceId = result.AgentInstanceId.ToString("D"),
            ProfileId = result.ProfileId.ToString("D"), SourceKind = (int)result.SourceKind, SourceId = result.SourceId.ToString("D"),
            ThroughCursor = result.ThroughCursor, CreatedAtUtc = result.CreatedAtUtc.ToUnixTimeMilliseconds(),
            PayloadJson = JsonSerializer.Serialize(result), Revision = result.Revision });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return result;
    }
}
