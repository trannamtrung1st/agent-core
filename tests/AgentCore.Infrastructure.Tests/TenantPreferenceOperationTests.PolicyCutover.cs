using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantPreferenceOperationTests
{
    [Fact]
    public async Task SelfManagement_cutover_refuses_active_hash_bound_Run_and_keeps_legacy_bytes_and_migration_history()
    {
        await using var f = await Fixture.Create(true);
        var setup = await Setup(f);
        const string predecessor = "20261025090000_CanonicalModelIdentity";
        string original;
        await using (var source = f.Contexts!.CreateDbContext())
            original = (await source.AgentInstances.SingleAsync(x => x.InstanceId == setup.Agent.Instance.InstanceId.ToString("D"))).HarnessManagementJson!;
        await using var historical = await HistoricalTenantDatabase.CreateAsync(f.Contexts!, predecessor, HistoricalTenantDatabase.SpaceAdmissionTables);
        var db = historical.Db;
        var body = JsonNode.Parse(original)!.AsObject(); body.Remove("overrides");
        var legacy = body.ToJsonString();
        var instance = setup.Agent.Instance.InstanceId.ToString("D");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AgentInstances SET HarnessManagementJson={legacy} WHERE InstanceId={instance}");
        var error = await Assert.ThrowsAsync<SqliteException>(() => db.Database.MigrateAsync());
        Assert.Contains("SelfManagementNormalizationRequired_StopHost_SettleRunsAndApprovals", error.Message);
        Assert.Equal(predecessor, (await db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.Equal(legacy, await db.Database.SqlQuery<string>($"SELECT HarnessManagementJson AS Value FROM AgentInstances WHERE InstanceId={instance}").SingleAsync());
        var run = setup.Run.AgentRunId.ToString("D");
        Assert.Equal(setup.Run.Revision, await db.Database.SqlQuery<long>($"SELECT Revision AS Value FROM AgentRuns WHERE AgentRunId={run}").SingleAsync());
    }
}
