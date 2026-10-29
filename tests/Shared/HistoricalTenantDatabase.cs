using AgentCore.Infrastructure.Persistence;
using AgentCore.Domain.Conversation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

namespace AgentCore.Tests.Shared;

// Projects an explicitly selected test graph into a fresh, real migration target.
// The current source stays intact; neither migration history nor guards are copied.
internal sealed class HistoricalTenantDatabase : IAsyncDisposable, IDbContextFactory<AgentCoreDbContext>
{
    private readonly string path;
    private readonly DbContextOptions<AgentCoreDbContext> options;
    private string sourcePath = "";
    public AgentCoreDbContext Db { get; }
    private HistoricalTenantDatabase(string path)
    {
        this.path = path;
        options = new DbContextOptionsBuilder<AgentCoreDbContext>()
            .UseSqlite($"Data Source={new Uri(Path.GetFullPath(path)).AbsoluteUri};Foreign Keys=True;Default Timeout=10").Options;
        Db = CreateDbContext();
    }
    public AgentCoreDbContext CreateDbContext() => new(options);

    internal static readonly string[] AdmissionTables = [
        "Accounts", "IdentityBootstrap", "Tenants", "AccessRoles", "Principals",
        "AccessRolePermissions", "AccessRoleAssignments", "AuthenticationSessions",
        "TenantUsers", "AgentInstances", "SystemIdentities", "Sessions",
        "SessionSnapshots", "ConversationEntries", "TenantSessions", "Activations", "ActivationSourceEntries",
        "AgentRuns", "AuthorizationAudit", "TenantApprovalRequests", "TenantApprovalDecisions",
        "TenantOperationGrants", "TenantOperationReceipts"
    ];

    internal static readonly string[] SpaceAdmissionTables = [
        .. AdmissionTables[..12], "Spaces", "SpaceMemberships",
        "SpaceMembershipAddedBindings", "SpaceMembershipDisabledBindings", .. AdmissionTables[12..]
    ];
    internal static readonly string[] CredentialAdmissionTables = [
        .. AdmissionTables[..12], "Credentials", "TenantCredentials", "AgentCredentialBindings", .. AdmissionTables[12..]
    ];
    internal static readonly string[] ArtifactAdmissionTables = [
        .. CredentialAdmissionTables.TakeWhile(table => table != "TenantApprovalRequests"), "Artifacts",
        .. CredentialAdmissionTables.SkipWhile(table => table != "TenantApprovalRequests")
    ];
    internal static readonly string[] MemoryAdmissionTables = [
        .. AdmissionTables.TakeWhile(table => table != "TenantApprovalRequests"), "TenantLearnedMemories",
        .. AdmissionTables.SkipWhile(table => table != "TenantApprovalRequests")
    ];
    internal static readonly string[] ExperienceSetupTables = [
        .. MemoryAdmissionTables.TakeWhile(table => table != "TenantApprovalRequests"), "ExperienceSettings", "IdentityMaintenanceSettings",
        .. MemoryAdmissionTables.SkipWhile(table => table != "TenantApprovalRequests")
    ];
    internal static readonly string[] ExperienceAdmissionTables = [
        .. ExperienceSetupTables.TakeWhile(table => table != "TenantApprovalRequests"), "TenantExperiences",
        .. ExperienceSetupTables.SkipWhile(table => table != "TenantApprovalRequests")
    ];

    public static async Task<HistoricalTenantDatabase> CreateAsync(
        IDbContextFactory<AgentCoreDbContext> source, string target, IReadOnlyList<string> tables,
        IReadOnlyList<AgentRun>? factAdmissionRuns = null, string? destinationPath = null)
    {
        await using var sourceDb = source.CreateDbContext();
        var sourceName = new SqliteConnectionStringBuilder(sourceDb.Database.GetConnectionString()).DataSource;
        var sourcePath = Uri.TryCreate(sourceName, UriKind.Absolute, out var sourceUri) && sourceUri.IsFile
            ? sourceUri.LocalPath : Path.GetFullPath(sourceName);
        var fixture = new HistoricalTenantDatabase(destinationPath ?? sourcePath + ".historical-" + Guid.NewGuid().ToString("N"));
        fixture.sourcePath = sourcePath;
        using (new FileStream(fixture.path, FileMode.CreateNew, FileAccess.Write)) { }
        try
        {
            await fixture.Db.GetService<IMigrator>().MigrateAsync(target);
            await fixture.Db.Database.OpenConnectionAsync();
            var connection = (SqliteConnection)fixture.Db.Database.GetDbConnection();
            await using (var attach = connection.CreateCommand())
            {
                attach.CommandText = "ATTACH DATABASE $path AS fixtureSource";
                attach.Parameters.AddWithValue("$path", ReadOnlyUri(sourcePath));
                await attach.ExecuteNonQueryAsync();
            }
            Assert.Equal(1, SQLitePCL.raw.sqlite3_db_readonly(connection.Handle, "fixtureSource"));
            async Task<HashSet<string>> Columns(string schema, string table)
            {
                await using var query = connection.CreateCommand();
                query.CommandText = $"PRAGMA {schema}.table_info(\"{table}\")";
                var result = new HashSet<string>(StringComparer.Ordinal);
                await using var rows = await query.ExecuteReaderAsync();
                while (await rows.ReadAsync()) result.Add(rows.GetString(1));
                return result;
            }
            await using var transaction = await fixture.Db.Database.BeginTransactionAsync();
            var staged = (factAdmissionRuns ?? []).Select(run => {
                Assert.Equal(AgentRunStatus.Running, run.Status);
                return AgentRunStoreMapping.ToRecord(run);
            }).ToDictionary(run => run.AgentRunId, StringComparer.Ordinal);
            // Cyclic identity references are checked at commit, with FK enforcement on.
            await fixture.Db.Database.ExecuteSqlRawAsync("PRAGMA defer_foreign_keys=ON");
            foreach (var table in tables)
            {
                var destination = await Columns("main", table);
                var original = await Columns("fixtureSource", table);
                Assert.True(destination.Count > 0, "Missing historical table: " + table);
                Assert.True(original.Count > 0, "Missing source table: " + table);
                var common = destination.Intersect(original).Order(StringComparer.Ordinal).ToArray();
                Assert.NotEmpty(common);
                var names = string.Join(",", common.Select(Quote));
                var values = names;
                if (table == "SessionSnapshots" && destination.Contains("ModelHasExplicitReasoningEffort") && !original.Contains("ModelHasExplicitReasoningEffort"))
                {
                    // The older physical model captured resolved reasoning as a
                    // boolean/value pair; no current preference column is imported.
                    names += ",\"ModelHasExplicitReasoningEffort\"";
                    values += ",CASE WHEN ModelReasoningEffort IS NULL THEN 0 ELSE 1 END";
                }
                var seedFilter = table == "ModelPresets"
                    ? " WHERE PresetId NOT IN (SELECT PresetId FROM main.ModelPresets)" : "";
                if (table == "AgentRuns" && staged.Count > 0)
                    seedFilter = " WHERE AgentRunId NOT IN (SELECT value FROM json_each({0}))";
                var insertSql = $"INSERT INTO main.{Quote(table)} ({names}) SELECT {values} FROM fixtureSource.{Quote(table)}{seedFilter}";
                await fixture.Db.Database.ExecuteSqlRawAsync(insertSql, table == "AgentRuns" && staged.Count > 0
                    ? [System.Text.Json.JsonSerializer.Serialize(staged.Keys)] : Array.Empty<object>());
                if (table == "AgentRuns" && staged.Count > 0)
                {
                    // Facts originally committed while these exact captured Runs were
                    // live. Recreate that insertion order, then restore their real
                    // settled source rows after inserting facts; guards remain enabled.
                    var properties = sourceDb.Model.FindEntityType(typeof(AgentRunRecord))!.GetProperties()
                        .ToDictionary(property => property.GetColumnName(), StringComparer.Ordinal);
                    foreach (var row in staged.Values)
                    {
                        var originalRow = await sourceDb.AgentRuns.AsNoTracking().SingleAsync(run => run.AgentRunId == row.AgentRunId);
                        Assert.Equal(originalRow.TenantId, row.TenantId); Assert.Equal(originalRow.SessionId, row.SessionId);
                        Assert.Equal(originalRow.AgentInstanceId, row.AgentInstanceId); Assert.Equal(originalRow.ExecutorPrincipalId, row.ExecutorPrincipalId);
                        Assert.Equal(originalRow.ActivationId, row.ActivationId); Assert.Equal(originalRow.RootActivationId, row.RootActivationId);
                        await using var insert = connection.CreateCommand();
                        insert.Transaction = (SqliteTransaction)transaction.GetDbTransaction();
                        var parameterList = string.Join(',', common.Select((_, index) => "$p" + index));
                        insert.CommandText = $"INSERT INTO main.AgentRuns ({names}) VALUES ({parameterList})";
                        for (var index = 0; index < common.Length; index++)
                        {
                            var property = properties[common[index]];
                            insert.Parameters.Add(property.GetRelationalTypeMapping().CreateParameter(insert, "$p" + index,
                                property.PropertyInfo!.GetValue(row), property.IsNullable));
                        }
                        Assert.Equal(1, await insert.ExecuteNonQueryAsync());
                    }
                    continue; // Full source parity is checked after restoring below.
                }
                // Every selected physical value and row survives the projection.
                var selected = string.Join(",", common.Select(Quote));
                var missingSql = $"SELECT count(*) AS Value FROM (SELECT {selected} FROM fixtureSource.{Quote(table)} EXCEPT SELECT {selected} FROM main.{Quote(table)})";
                var extraSql = $"SELECT count(*) AS Value FROM (SELECT {selected} FROM main.{Quote(table)} EXCEPT SELECT {selected} FROM fixtureSource.{Quote(table)})";
                Assert.Equal(0, await fixture.Db.Database.SqlQueryRaw<int>(missingSql).SingleAsync());
                Assert.Equal(0, await fixture.Db.Database.SqlQueryRaw<int>(extraSql).SingleAsync());
            }
            foreach (var runId in staged.Keys) await fixture.RefreshRunAsync(Guid.Parse(runId));
            Assert.Empty(await fixture.Db.Database.SqlQueryRaw<string>("SELECT \"table\" || ' -> ' || parent AS Value FROM pragma_foreign_key_check").ToArrayAsync());
            await transaction.CommitAsync();
            // Historical migration SQL contains unqualified DROP IF EXISTS names.
            // Never leave the source attached while a caller applies a migration.
            await fixture.Db.Database.ExecuteSqlRawAsync("DETACH DATABASE fixtureSource");
            Assert.Equal(0, await fixture.Db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM pragma_database_list WHERE name='fixtureSource'").SingleAsync());
            Assert.Equal(1, (await fixture.Db.Database.SqlQueryRaw<int>("PRAGMA foreign_keys").ToArrayAsync()).Single());
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    internal static string ReadOnlyUri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri + "?mode=ro";

    // Copy the real source store's cancellation into its corresponding historical
    // row; the original generation/identity and every shared physical value remain checked.
    internal async Task RefreshRunAsync(Guid runId)
    {
        var connection = (SqliteConnection)Db.Database.GetDbConnection();
        var attachedHere = Db.Database.CurrentTransaction is null;
        if (attachedHere)
        {
            await using var attach = connection.CreateCommand();
            attach.CommandText = "ATTACH DATABASE $path AS fixtureSource";
            attach.Parameters.AddWithValue("$path", ReadOnlyUri(sourcePath));
            await attach.ExecuteNonQueryAsync();
            Assert.Equal(1, SQLitePCL.raw.sqlite3_db_readonly(connection.Handle, "fixtureSource"));
        }
        try
        {
            var names = new List<string>();
            await using (var columns = connection.CreateCommand())
            {
                columns.CommandText = "SELECT name FROM pragma_table_info('AgentRuns') WHERE name<>'AgentRunId' AND name IN (SELECT name FROM pragma_table_info('AgentRuns','fixtureSource'))";
                await using var rows = await columns.ExecuteReaderAsync();
                while (await rows.ReadAsync()) names.Add(Quote(rows.GetString(0)));
            }
            Assert.NotEmpty(names);
            await using var update = connection.CreateCommand();
            update.Transaction = Db.Database.CurrentTransaction?.GetDbTransaction() as SqliteTransaction;
            var fields = string.Join(',', names);
            update.CommandText = $"UPDATE main.AgentRuns SET ({fields})=(SELECT {fields} FROM fixtureSource.AgentRuns WHERE AgentRunId=$id) WHERE AgentRunId=$id";
            update.Parameters.AddWithValue("$id", runId.ToString("D"));
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
            await using var parity = connection.CreateCommand();
            parity.Transaction = update.Transaction;
            var selected = "AgentRunId," + fields;
            parity.CommandText = $"SELECT (SELECT count(*) FROM (SELECT {selected} FROM fixtureSource.AgentRuns WHERE AgentRunId=$id EXCEPT SELECT {selected} FROM main.AgentRuns WHERE AgentRunId=$id)) + (SELECT count(*) FROM (SELECT {selected} FROM main.AgentRuns WHERE AgentRunId=$id EXCEPT SELECT {selected} FROM fixtureSource.AgentRuns WHERE AgentRunId=$id))";
            parity.Parameters.AddWithValue("$id", runId.ToString("D"));
            Assert.Equal(0L, await parity.ExecuteScalarAsync());
            Assert.Empty(await Db.Database.SqlQueryRaw<string>("SELECT \"table\" || ' -> ' || parent AS Value FROM pragma_foreign_key_check").ToArrayAsync());
        }
        finally
        {
            if (attachedHere) await Db.Database.ExecuteSqlRawAsync("DETACH DATABASE fixtureSource");
        }
    }

    internal static async Task AssertApprovalUpgradeAsync(IDbContextFactory<AgentCoreDbContext> source,
        string predecessor, string target, IReadOnlyList<string>? tables = null)
    {
        await using var historical = await CreateAsync(source, predecessor, tables ?? AdmissionTables);
        var approvals = await ApprovalBytes(historical.Db);
        var runs = await RunBytes(historical.Db);
        await historical.Db.GetService<IMigrator>().MigrateAsync(target);
        Assert.Equal(target, (await historical.Db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.Equal(approvals, await ApprovalBytes(historical.Db));
        Assert.Equal(runs, await RunBytes(historical.Db));
    }

    internal static async Task AssertApprovalDowngradeRefusedAsync(IDbContextFactory<AgentCoreDbContext> source,
        string target, string predecessor, string guard, IReadOnlyList<string>? tables = null, IReadOnlyList<AgentRun>? factAdmissionRuns = null)
    {
        await using var historical = await CreateAsync(source, target, tables ?? AdmissionTables, factAdmissionRuns);
        var migrations = (await historical.Db.Database.GetAppliedMigrationsAsync()).ToArray();
        var approvals = await ApprovalBytes(historical.Db);
        var runs = await RunBytes(historical.Db);
        var error = await Assert.ThrowsAsync<SqliteException>(() => historical.Db.GetService<IMigrator>().MigrateAsync(predecessor));
        Assert.Equal(19, error.SqliteErrorCode); Assert.Contains(guard, error.Message);
        Assert.Equal(migrations, await historical.Db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(approvals, await ApprovalBytes(historical.Db));
        Assert.Equal(runs, await RunBytes(historical.Db));
    }

    private static async Task<string[]> ApprovalBytes(AgentCoreDbContext db) =>
        (await db.Database.SqlQueryRaw<string>("SELECT PayloadJson AS Value FROM TenantApprovalRequests").ToArrayAsync()).Order(StringComparer.Ordinal).ToArray();
    private static async Task<string[]> RunBytes(AgentCoreDbContext db) =>
        (await db.Database.SqlQueryRaw<string>("SELECT AgentRunId || ':' || Status || ':' || PayloadJson AS Value FROM AgentRuns").ToArrayAsync()).Order(StringComparer.Ordinal).ToArray();

    public async ValueTask DisposeAsync()
    {
        // Clear the actual URI-based pool before returning its connection.
        // A plain path creates a different pool identity.
        SqliteConnection.ClearPool((SqliteConnection)Db.Database.GetDbConnection());
        await Db.DisposeAsync();
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
    }
}
