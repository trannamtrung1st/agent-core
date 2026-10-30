using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Infrastructure.Workflows;

/// <summary>Stopped-writer, additive installation of the matching Core and embedded runtime schemas.</summary>
public static class FormalWorkflowCutover
{
    public const string Recovery = "Formal Workflow schema maintenance required. Stop all application writers; back up protected keys and resource roots; run: dotnet run --project src/AgentCore.Api -- migrate-workflows <database-path> <new-backup-path>. Restore the coordinated matching backup if interrupted. See docs/15-persistence-and-configuration.md#formal-workflow-persistence-and-cutover.";
    public static async Task MigrateAsync(string databasePath, string backupPath, CancellationToken ct = default)
    {
        using var installationLease = AgentCore.Infrastructure.Persistence.SqliteInstallationLease.Acquire(databasePath);
        databasePath = Path.GetFullPath(databasePath); backupPath = Path.GetFullPath(backupPath);
        if (!File.Exists(databasePath) || databasePath == backupPath || File.Exists(backupPath))
            throw new ArgumentException("Use an existing stopped database and a new distinct backup path.");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        await using var db = new AgentCoreDbContext(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite(connectionString).Options);
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToArray(); var available = db.Database.GetMigrations().ToArray();
        if (!applied.Contains("20261028030836_BuiltInGeneralAssistant", StringComparer.Ordinal)
            || !applied.SequenceEqual(available.Take(applied.Length), StringComparer.Ordinal))
            throw new InvalidOperationException("Apply and qualify the incumbent cutovers before the additive Formal Workflow upgrade. " + Recovery);
        await db.Database.OpenConnectionAsync(ct);
        await using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
        { await backup.OpenAsync(ct); ((SqliteConnection)db.Database.GetDbConnection()).BackupDatabase(backup); }
        // Core's Task table rebuild crosses SQLite PRAGMA boundaries. Stop writers first; a failure requires coordinated restore.
        await db.Database.MigrateAsync(ct);
        var services = new ServiceCollection(); services.AddLogging(); services.AddFormalWorkflowRuntime(new() { Provider = "Sqlite", ConnectionString = connectionString });
        await using var provider = services.BuildServiceProvider(); await provider.MigrateFormalWorkflowRuntimeAsync(ct);
        if (!await provider.GetRequiredService<AgentCore.Application.Ports.IFormalWorkflowRuntime>().IsReadyAsync(ct)) throw new InvalidOperationException(Recovery);
        await using var check = db.Database.GetDbConnection().CreateCommand(); check.CommandText = "PRAGMA foreign_key_check";
        await using var result = await check.ExecuteReaderAsync(ct);
        if (await result.ReadAsync(ct)) throw new InvalidOperationException("Workflow upgrade foreign-key validation failed. " + Recovery);
    }
}
