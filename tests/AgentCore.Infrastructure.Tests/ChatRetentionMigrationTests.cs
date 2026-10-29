using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed class ChatRetentionMigrationTests
{
    [Theory]
    [InlineData("Ended", true)] [InlineData("Completed", false)]
    public async Task Upgrade_preserves_history_and_only_backfills_known_host_provenance(string status, bool supported)
    {
        await using var f = await Fixture.Create(true);
        var definition = (await f.BuiltIns.ListAsync()).First();
        var snapshot = AgentRunTestFixtures.Snapshot(new(Guid.NewGuid(), Guid.NewGuid()), definition, f.Clock.GetUtcNow());
        await f.Memory.SaveAsync(snapshot, 0);
        await f.SelectHistoricalConversationAsync("20261026010000_CanonicalSelfManagement");
        await using var db = f.Contexts!.CreateDbContext();
        await db.GetService<IMigrator>().MigrateAsync("20261026010000_CanonicalSelfManagement");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE SessionSnapshots SET LifecycleSource='Legacy',LifecycleStatus={status} WHERE SessionId={snapshot.SessionId.ToString("D")}");
        if (supported)
        {
            await db.Database.MigrateAsync();
            var retained = (await f.Memory.LoadAsync(snapshot.SessionId))!;
            Assert.Equal(snapshot.Entries.Single().Text, retained.Entries.Single().Text);
            Assert.Equal(SessionRetentionKind.Saved, retained.RetentionKind); Assert.Null(retained.RetainAfterTerminalDays); Assert.Null(retained.RetentionDueAtUtc);
            Assert.Equal(LifecycleTransitionSource.Host, retained.LifecycleSource);
            Assert.False(db.Database.HasPendingModelChanges());
        }
        else
        {
            var error = await Assert.ThrowsAsync<SqliteException>(() => db.Database.MigrateAsync());
            Assert.Contains("RequiresExplicitLifecycleSourceRepair", error.Message);
            Assert.Equal("20261026010000_CanonicalSelfManagement", (await db.Database.GetAppliedMigrationsAsync()).Last());
        }
    }

    [Fact]
    public async Task Downgrade_refuses_to_discard_a_governed_tenant_policy()
    {
        await using var f = await Fixture.Create(true, targetMigration: "20261026080000_ChatRetention");
        await using var db = f.Contexts!.CreateDbContext();
        // A bounded physical fixture exercises this guard without crossing later
        // forward-only cutovers or using today's ORM against predecessor columns.
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Tenants (TenantId,Name,Slug,Status,Revision,CreatedAt,SavedChatRetentionDays) VALUES ({Guid.NewGuid()},{"Alpha"},{"alpha"},{0},{1L},{0L},{7})");
        var error = await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261026010000_CanonicalSelfManagement"));
        Assert.Contains("RetentionDowngradeRequiresExplicitExport", error.Message);
        Assert.Equal("20261026080000_ChatRetention", (await db.Database.GetAppliedMigrationsAsync()).Last());
    }
}
