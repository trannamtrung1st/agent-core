using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Tests.Shared;

// Reuse only empty current-schema setup, never identity, tenant data or test state.
// Explicit predecessor/upgrade tests continue to construct their own databases.
internal static class CurrentSqliteTestDatabase
{
    private static readonly Lazy<Task<string>> Template = new(CreateTemplateAsync);
    private static readonly SemaphoreSlim CopyGate = new(1, 1);

    internal static async Task SeedAsync(string destination)
    {
        // The caller owns a fresh disposable path. Never replace an existing DB.
        using (new FileStream(destination, FileMode.CreateNew, FileAccess.Write)) { }
        try
        {
            var sourcePath = await Template.Value.ConfigureAwait(false);
            // Opening several WAL readers at once can race SQLite recovery.
            // Serialize only the short copy, never the independently owned cases.
            await CopyGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await using var source = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                await using var target = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = destination, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
                await source.OpenAsync().ConfigureAwait(false); await target.OpenAsync().ConfigureAwait(false);
                source.BackupDatabase(target);
            }
            finally { CopyGate.Release(); }
        }
        catch
        {
            File.Delete(destination);
            throw;
        }
    }

    private static async Task<string> CreateTemplateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-core-empty-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        };
        var path = Path.Combine(root, "empty.db");
        var connection = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString();
        await using var db = new AgentCoreDbContext(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite(connection).Options);
        await db.Database.MigrateAsync().ConfigureAwait(false);
        if (db.Database.HasPendingModelChanges()) throw new InvalidOperationException("The current test schema does not match the model.");
        return path;
    }
}
