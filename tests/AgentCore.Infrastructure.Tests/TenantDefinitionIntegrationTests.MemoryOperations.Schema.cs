using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Memory;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;
public sealed partial class TenantDefinitionIntegrationTests
{
    [Fact]
    public async Task Tenant_memory_operation_migration_preserves_populated_owned_facts_and_guards_and_refuses_lossy_downgrade()
    {
        await using var f = await Fixture.Create(true); var s = await MemoryOperationFixture(f);
        await using (var historical = await HistoricalTenantDatabase.CreateAsync(f.Contexts!,
            "20261021164125_TenantLearnedMemory", HistoricalTenantDatabase.MemoryAdmissionTables, [s.LearnedRun]))
        {
            var db = historical.Db; var migration = db.GetService<IMigrator>();
            Assert.Equal(6, await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM TenantLearnedMemories").SingleAsync());
            await migration.MigrateAsync("20261021185601_TenantMemoryOperations"); Assert.Equal(6, await db.Set<TenantLearnedMemoryRecord>().CountAsync());
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM TenantLearnedMemories"));
        }
        var services = MemoryServices(f); var request = await PrepareMemoryReview(f, s.Turn.Frame, Consolidate(s.Sources), services.Approvals);
        await using (var db = f.Contexts!.CreateDbContext())
        {
            await HistoricalTenantDatabase.AssertApprovalDowngradeRefusedAsync(f.Contexts!,
                "20261021185601_TenantMemoryOperations", "20261021164125_TenantLearnedMemory", "Rows=0",
                HistoricalTenantDatabase.MemoryAdmissionTables, [s.LearnedRun]);
            Assert.Equal(6, await db.Set<TenantLearnedMemoryRecord>().CountAsync());
            Assert.True(await db.Set<TenantApprovalRecord>().AnyAsync(r => r.RequestId == request.RequestId));
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantLearnedMemories SET SourceMemoryIdsJson='[]', Revision=Revision+1, Status=1 WHERE MemoryId={s.Sources[0].MemoryId}"));
            Assert.Equal("text", await db.Database.SqlQuery<string>($"SELECT json_type(PayloadJson,'$.Operation.MemoryOperation.UserPrincipalId') AS Value FROM TenantApprovalRequests WHERE RequestId={request.RequestId}").SingleAsync());
            Assert.Contains("MemoryOperation", await db.Database.SqlQueryRaw<string>("SELECT sql AS Value FROM sqlite_master WHERE type='trigger' AND name='TR_TenantApprovals_Memory_Update'").SingleAsync());
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET PayloadJson=json_set(PayloadJson,'$.Operation.MemoryOperation.UserPrincipalId',NULL) WHERE RequestId={request.RequestId}"));
            // The isolated historical refusal leaves the current source usable.
            await db.GetService<IMigrator>().MigrateAsync();
            Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
            Assert.False(db.Database.HasPendingModelChanges());
        }
        var reloaded = MemoryServices(f, new SqliteIamStore(f.Contexts!, f.Content));
        var view = await reloaded.Approvals.ReadViewAsync(s.Owner.Tenant, request.RequestId, new(s.Owner.Human.AccountId, s.Owner.Human.AuthenticationSessionId, s.Owner.Human.PrincipalId));
        Assert.True(view.Review!.CanDecide); Assert.Contains(view.Review.Changes, c => c.Field == "Exact current source values" && ((string)c.After!).Contains(s.Sources[0].Content));
    }
}
