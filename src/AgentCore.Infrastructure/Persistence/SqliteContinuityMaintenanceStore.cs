using AgentCore.Application.Sessions;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Domain.Experience;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class SqliteExperienceStore : IContinuityMaintenanceStore
{
    public async ValueTask<ContinuityMaintenanceSettings> ReadAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.ContinuityMaintenanceSettings.AsNoTracking().SingleOrDefaultAsync(r => r.AgentInstanceId == id.ToString("D"), ct);
        return row is null ? new(id, null, 0, null) : new(id, row.IntervalSeconds, row.Revision,
            row.LastMaintenanceAtUtc is { } last ? DateTimeOffset.FromUnixTimeMilliseconds(last) : null);
    }

    async ValueTask<ContinuityMaintenanceSettings> IContinuityMaintenanceStore.ConfigureAsync(Guid id, long expectedRevision,
        int? intervalSeconds, CancellationToken ct, AdminEventAppend? audit)
    {
        if (expectedRevision < 0 || intervalSeconds is <= 0) throw AgentCoreErrors.Validation("Continuity maintenance settings are invalid.");
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var key = id.ToString("D");
        var changed = await db.ContinuityMaintenanceSettings.Where(r => r.AgentInstanceId == key && r.Revision == expectedRevision)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.IntervalSeconds, intervalSeconds).SetProperty(r => r.Revision, expectedRevision + 1), ct);
        if (changed == 0 && expectedRevision == 0)
            changed = await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ContinuityMaintenanceSettings (AgentInstanceId, IntervalSeconds, Revision, LastMaintenanceAtUtc) VALUES ({key}, {intervalSeconds}, 1, NULL) ON CONFLICT(AgentInstanceId) DO NOTHING", ct);
        if (changed != 1) throw AgentCoreErrors.Conflict("Continuity maintenance settings revision is stale.");
        if (audit is not null) { AdminEventPersistence.StageAppend(db, audit, Guid.NewGuid()); await db.SaveChangesAsync(ct); }
        await tx.CommitAsync(ct);
        return await ReadAsync(id, ct);
    }

    public async ValueTask<bool> TryClaimAsync(ContinuityMaintenanceSettings expected, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var key = expected.AgentInstanceId.ToString("D");
        var stamp = now.ToUnixTimeMilliseconds();
        var previous = expected.LastMaintenanceAtUtc?.ToUnixTimeMilliseconds();
        // One statement arbitrates both a missing row and an existing row; no read-then-insert race.
        if (expected.Revision == 0 && previous is null)
            return await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ContinuityMaintenanceSettings (AgentInstanceId, IntervalSeconds, Revision, LastMaintenanceAtUtc) VALUES ({key}, NULL, 0, {stamp}) ON CONFLICT(AgentInstanceId) DO UPDATE SET LastMaintenanceAtUtc = {stamp} WHERE Revision = {expected.Revision} AND LastMaintenanceAtUtc IS NULL", ct) == 1;
        return await db.ContinuityMaintenanceSettings.Where(r => r.AgentInstanceId == key && r.Revision == expected.Revision && r.LastMaintenanceAtUtc == previous)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.LastMaintenanceAtUtc, (long?)stamp), ct) == 1;
    }
}
