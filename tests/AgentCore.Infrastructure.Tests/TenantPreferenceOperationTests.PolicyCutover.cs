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
        await using var db = f.Contexts!.CreateDbContext();
        const string predecessor = "20261025090000_CanonicalModelIdentity";
        await db.GetService<IMigrator>().MigrateAsync(predecessor);
        var row = await db.AgentInstances.SingleAsync(x => x.InstanceId == setup.Agent.Instance.InstanceId.ToString("D"));
        var body = JsonNode.Parse(row.HarnessManagementJson!)!.AsObject(); body.Remove("overrides");
        var legacy = body.ToJsonString(); row.HarnessManagementJson = legacy; await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<SqliteException>(() => db.Database.MigrateAsync());
        Assert.Contains("SelfManagementNormalizationRequired_StopHost_SettleRunsAndApprovals", error.Message);
        Assert.Equal(predecessor, (await db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.Equal(legacy, (await db.AgentInstances.AsNoTracking().SingleAsync(x => x.InstanceId == row.InstanceId)).HarnessManagementJson);
        Assert.Equal(setup.Run.Revision, (await db.AgentRuns.SingleAsync(x => x.AgentRunId == setup.Run.AgentRunId.ToString("D"))).Revision);
    }
}
