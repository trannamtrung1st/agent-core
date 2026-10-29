using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed class TenantAutomationMigrationTests
{
    [Fact]
    public async Task Tagged_Automation_migration_roundtrips_legacy_rows_and_restores_existing_security_guards()
    {
        var path = Path.Combine(Path.GetTempPath(), "tenant-automation-migration-" + Guid.NewGuid().ToString("N") + ".db");
        var factory = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite("Data Source=" + path + ";Foreign Keys=True").Options);
        try
        {
            await using var db = factory.CreateDbContext();
            var migrator = db.GetService<IMigrator>();
            const string target = "20261021010000_TenantAutomationAdmissionApproval";
            await migrator.MigrateAsync(target);
            Assert.False(db.Database.HasPendingModelChanges());
            var at = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var schedule = new OneShotSchedule(at.AddDays(1), "UTC");
            var legacy = new Automation(Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid()), AutomationStatus.Active, "Retain legacy source bytes", new ScheduleTrigger(schedule), schedule.AtUtc,
                null, 0, 1, 1, new(TriggerAuthorizationOrigin.AdminOwner, null, null, at, at), null);
            // Today's store INSERT contains later model/context columns. Seed the
            // actual frozen columns of this unowned historical Automation instead.
            await InsertLegacy(db, legacy);
            var targetGuards = await Guards(db);
            var guards = await Guards(db); Assert.Contains("TR_TenantOccurrences_Source_Insert", guards); Assert.Contains("TR_TenantSessions_Creator_Insert", guards);
            Assert.Contains("TR_TenantApprovals_IndependentTask_Insert", guards); Assert.Contains("TR_TenantApprovals_IndependentTask_Update", guards);
            Assert.Contains("TR_TenantApprovals_AutomationAdmission_INSERT", guards); Assert.Contains("TR_TenantReceipts_BrowserArtifacts_Insert", guards); Assert.Equal(46, guards.Length);
            await db.GetService<IMigrator>().MigrateAsync("20261020200000_TenantIndependentChildControls");
            Assert.Equal(guards.Length - 3, (await Guards(db)).Length);
            await migrator.MigrateAsync(target); Assert.Equal(targetGuards, await Guards(db));
            await db.GetService<IMigrator>().MigrateAsync("20261020170000_TenantAccountOperationRead");
            var previousGuards = await Guards(db); Assert.Equal(guards.Length - 10, previousGuards.Length);
            await migrator.MigrateAsync(target); Assert.Equal(targetGuards, await Guards(db));
            await db.Database.MigrateAsync();
            var read = (await new SqliteTriggerStore(factory).GetAsync(legacy.Owner, legacy.AutomationId))!;
            Assert.Equal(legacy.Owner, read.Owner); Assert.Null(read.Owner.TenantId); Assert.Equal(legacy.Instructions, read.Instructions);
            Assert.Equal(legacy.NextOccurrenceAtUtc, read.NextOccurrenceAtUtc); Assert.Equal(legacy.Triggers.Single().TriggerId, read.Triggers.Single().TriggerId);
            await db.Database.OpenConnectionAsync(); using var command = db.Database.GetDbConnection().CreateCommand(); command.CommandText = "PRAGMA foreign_key_check";
            using var rows = await command.ExecuteReaderAsync(); Assert.False(await rows.ReadAsync());
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); }
    }
    internal static async Task InsertLegacy(AgentCoreDbContext db, Automation automation)
    {
        await db.Database.OpenConnectionAsync();
        var record = TriggerStoreMapping.ToRecord(automation);
        var columns = new HashSet<string>(StringComparer.Ordinal);
        using (var query = db.Database.GetDbConnection().CreateCommand())
        {
            query.CommandText = "PRAGMA table_info(Automations)";
            using var rows = await query.ExecuteReaderAsync();
            while (await rows.ReadAsync()) columns.Add(rows.GetString(1));
        }
        var properties = db.Model.FindEntityType(typeof(AutomationRecord))!.GetProperties()
            .Where(property => columns.Contains(property.GetColumnName())).ToArray();
        using (var insert = db.Database.GetDbConnection().CreateCommand())
        {
            insert.CommandText = "INSERT INTO Automations (" + string.Join(',', properties.Select(p => "\"" + p.GetColumnName() + "\""))
                + ") VALUES (" + string.Join(',', properties.Select((_, i) => "$p" + i)) + ")";
            for (var i = 0; i < properties.Length; i++)
            {
                var parameter = insert.CreateParameter(); parameter.ParameterName = "$p" + i;
                parameter.Value = properties[i].PropertyInfo!.GetValue(record) ?? DBNull.Value; insert.Parameters.Add(parameter);
            }
            await insert.ExecuteNonQueryAsync();
        }
        db.AddRange(record.Triggers); await db.SaveChangesAsync();
    }
    private static async Task<string[]> Guards(AgentCoreDbContext db)
    {
        await db.Database.OpenConnectionAsync(); using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'trigger' ORDER BY name";
        using var reader = await command.ExecuteReaderAsync(); var names = new List<string>(); while (await reader.ReadAsync()) names.Add(reader.GetString(0)); return names.ToArray();
    }
    private sealed class Factory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    { public AgentCoreDbContext CreateDbContext() => new(options); }
}
