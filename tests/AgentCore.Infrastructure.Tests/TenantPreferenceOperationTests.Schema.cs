using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantPreferenceOperationTests
{
    [Fact]
    public async Task Direct_receipt_schema_enforces_source_dedupe_and_owned_Run_lineage()
    {
        await using var f = await Fixture.Create(true); var setup = await Setup(f);
        await Service(f).ExecuteAsync(new(setup.Tenant, setup.Run.AgentRunId), setup.Run.Claim!.Generation, "schema-call", Preferences);
        await using var read = await f.Contexts!.CreateDbContextAsync();
        var receipt = await read.Set<TenantOperationReceiptRecord>().AsNoTracking().SingleAsync();
        foreach (var variant in new[] {
            receipt with { OperationId = f.Ids.NewId() },
            receipt with { OperationId = f.Ids.NewId(), SourceCallId = null },
            receipt with { OperationId = f.Ids.NewId(), SourceCallId = "another", ParametersHash = null },
            receipt with { OperationId = f.Ids.NewId(), SourceCallId = "another", RequestId = f.Ids.NewId() },
            receipt with { OperationId = f.Ids.NewId(), SourceCallId = "another", RootActivationId = f.Ids.NewId().ToString("D") },
            receipt with { OperationId = f.Ids.NewId(), SourceCallId = "another", ExecutorPrincipalId = f.Ids.NewId() },
            receipt with { OperationId = f.Ids.NewId(), SourceCallId = "another", TenantId = f.Ids.NewId() } })
        {
            await using var db = await f.Contexts.CreateDbContextAsync(); db.Add(variant);
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(19, Assert.IsType<SqliteException>(error.InnerException).SqliteErrorCode);
        }
        Assert.Single(await read.Set<TenantOperationReceiptRecord>().AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Receipt_migration_round_trips_empty_source_but_refuses_to_discard_direct_receipts(bool hasReceipt)
    {
        await using var f = await Fixture.Create(true);
        if (hasReceipt)
        {
            var setup = await Setup(f);
            await Service(f).ExecuteAsync(new(setup.Tenant, setup.Run.AgentRunId), setup.Run.Claim!.Generation, "downgrade-call", Preferences);
        }
        await using var db = await f.Contexts!.CreateDbContextAsync(); var migrator = db.GetService<IMigrator>();
        const string previous = "20261017024144_TenantApprovedLocalOperationReceipts";
        if (hasReceipt)
        {
            var error = await Assert.ThrowsAsync<SqliteException>(() => migrator.MigrateAsync(previous));
            Assert.Equal(19, error.SqliteErrorCode);
            // Later migrations may already have reverted before the direct-receipt guard refuses.
            // Inspect the retained fact without assuming current-model columns still exist.
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM TenantOperationReceipts").SingleAsync());
            Assert.Contains("20261017052609_TenantDirectLocalOperationReceipts", await db.Database.GetAppliedMigrationsAsync());
            await migrator.MigrateAsync();
            Assert.Single(await db.Set<TenantOperationReceiptRecord>().AsNoTracking().ToArrayAsync());
        }
        else
        {
            await migrator.MigrateAsync(previous); await migrator.MigrateAsync();
            Assert.False(db.Database.HasPendingModelChanges());
            Assert.Empty(await db.Set<TenantOperationReceiptRecord>().ToArrayAsync());
        }
    }
}
