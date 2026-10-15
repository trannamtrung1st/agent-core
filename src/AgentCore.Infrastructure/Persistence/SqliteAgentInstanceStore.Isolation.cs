using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class SqliteAgentInstanceStore : IStateIsolationConfigurationStore
{
    public async ValueTask ApplyAsync(IsolationMetadataUpdate update, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var id = update.InstanceId.ToString("D");
        var owner = await db.AgentInstances.SingleOrDefaultAsync(r => r.InstanceId == id, ct) ?? throw AgentCoreErrors.NotFound("Agent Instance was not found.");
        var learned = await db.ExperienceSettings.SingleOrDefaultAsync(r => r.AgentInstanceId == id, ct);
        var maintenance = await db.IdentityMaintenanceSettings.SingleOrDefaultAsync(r => r.AgentInstanceId == id, ct);
        if (owner.Revision != update.Expected.Instance || (learned?.Revision ?? 0) != update.Expected.Experience
            || (maintenance?.Revision ?? 0) != update.Expected.Maintenance)
            throw AgentCoreErrors.Conflict("State & Isolation owners changed. Refresh the preview before applying.");
        if (owner.Lifecycle != "Active") throw AgentCoreErrors.Forbidden("Archived Instance configuration is read-only.");
        var previous = Map(owner);
        owner.SettingsOverridesJson = JsonSerializer.Serialize(update.Settings, Json);
        owner.UpdatedAtUtc = now.ToUnixTimeMilliseconds(); owner.Revision++;
        if ((learned?.Enabled ?? false) != update.ExperienceEnabled)
        {
            if (learned is null) { learned = new() { AgentInstanceId = id }; db.ExperienceSettings.Add(learned); }
            learned.Enabled = update.ExperienceEnabled; learned.Revision++;
        }
        if ((maintenance?.AllowAgentConsolidation ?? false) != update.AllowMaintenance)
        {
            if (maintenance is null) { maintenance = new() { AgentInstanceId = id }; db.IdentityMaintenanceSettings.Add(maintenance); }
            maintenance.AllowAgentConsolidation = update.AllowMaintenance; maintenance.Revision++;
        }
        AdminEventPersistence.StageAppend(db, update.Audit, ids.NewId());
        foreach (var e in CoreEventPersistence.Instance(previous, Map(owner))) CoreEventPersistence.Stage(db, e);
        try { await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw AgentCoreErrors.Conflict("State & Isolation changed while applying. Refresh the preview."); }
    }
}
