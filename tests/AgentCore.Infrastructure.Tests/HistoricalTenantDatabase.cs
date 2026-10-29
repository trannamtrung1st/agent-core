using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

// Projects an explicitly selected test graph into a fresh, real migration target.
// The current source stays intact; neither migration history nor guards are copied.
internal sealed class HistoricalTenantDatabase : IAsyncDisposable
{
    private readonly string path;
    public AgentCoreDbContext Db { get; }
    private HistoricalTenantDatabase(string path)
    {
        this.path = path;
        Db = new(new DbContextOptionsBuilder<AgentCoreDbContext>()
            .UseSqlite($"Data Source={path};Foreign Keys=True;Default Timeout=10").Options);
    }

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

    public static async Task<HistoricalTenantDatabase> CreateAsync(
        IDbContextFactory<AgentCoreDbContext> source, string target, IReadOnlyList<string> tables)
    {
        await using var sourceDb = source.CreateDbContext();
        var sourcePath = new SqliteConnectionStringBuilder(sourceDb.Database.GetConnectionString()).DataSource;
        var fixture = new HistoricalTenantDatabase(sourcePath + ".historical-" + Guid.NewGuid().ToString("N"));
        using (new FileStream(fixture.path, FileMode.CreateNew, FileAccess.Write)) { }
        try
        {
            await fixture.Db.GetService<IMigrator>().MigrateAsync(target);
            await fixture.Db.Database.OpenConnectionAsync();
            var connection = (SqliteConnection)fixture.Db.Database.GetDbConnection();
            await using (var attach = connection.CreateCommand())
            {
                attach.CommandText = "ATTACH DATABASE $path AS fixtureSource";
                attach.Parameters.AddWithValue("$path", sourcePath);
                await attach.ExecuteNonQueryAsync();
            }
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
            // Cyclic identity references are checked at commit, with FK enforcement on.
            await fixture.Db.Database.ExecuteSqlRawAsync("PRAGMA defer_foreign_keys=ON");
            foreach (var table in tables)
            {
                var destination = await Columns("main", table);
                var original = await Columns("fixtureSource", table);
                Assert.NotEmpty(destination); Assert.NotEmpty(original);
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
                var insertSql = $"INSERT INTO main.{Quote(table)} ({names}) SELECT {values} FROM fixtureSource.{Quote(table)}";
                await fixture.Db.Database.ExecuteSqlRawAsync(insertSql);
                // Every selected physical value and row survives the projection.
                var selected = string.Join(",", common.Select(Quote));
                var missingSql = $"SELECT count(*) AS Value FROM (SELECT {selected} FROM fixtureSource.{Quote(table)} EXCEPT SELECT {selected} FROM main.{Quote(table)})";
                var extraSql = $"SELECT count(*) AS Value FROM (SELECT {selected} FROM main.{Quote(table)} EXCEPT SELECT {selected} FROM fixtureSource.{Quote(table)})";
                Assert.Equal(0, await fixture.Db.Database.SqlQueryRaw<int>(missingSql).SingleAsync());
                Assert.Equal(0, await fixture.Db.Database.SqlQueryRaw<int>(extraSql).SingleAsync());
            }
            Assert.Empty(await fixture.Db.Database.SqlQueryRaw<string>("SELECT \"table\" || ' -> ' || parent AS Value FROM pragma_foreign_key_check").ToArrayAsync());
            await transaction.CommitAsync();
            Assert.Equal(1, (await fixture.Db.Database.SqlQueryRaw<int>("PRAGMA foreign_keys").ToArrayAsync()).Single());
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    internal static async Task AssertApprovalUpgradeAsync(IDbContextFactory<AgentCoreDbContext> source,
        string predecessor, string target)
    {
        await using var historical = await CreateAsync(source, predecessor, AdmissionTables);
        var approvals = await ApprovalBytes(historical.Db);
        var runs = await RunBytes(historical.Db);
        await historical.Db.GetService<IMigrator>().MigrateAsync(target);
        Assert.Equal(target, (await historical.Db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.Equal(approvals, await ApprovalBytes(historical.Db));
        Assert.Equal(runs, await RunBytes(historical.Db));
    }

    internal static async Task AssertApprovalDowngradeRefusedAsync(IDbContextFactory<AgentCoreDbContext> source,
        string target, string predecessor, string guard)
    {
        await using var historical = await CreateAsync(source, target, AdmissionTables);
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
        await Db.DisposeAsync();
        using (var connection = new SqliteConnection("Data Source=" + path)) SqliteConnection.ClearPool(connection);
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
    }
}
