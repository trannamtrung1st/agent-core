using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory] [InlineData("clean")] [InlineData("child")] [InlineData("definition")]
    public async Task Tasks_actual_predecessor_requires_explicit_cutover_and_retains_matching_backup(string boundary)
    {
        var obsoleteChild = boundary == "child"; var obsolete = boundary != "clean";
        await using var f = await Fixture.Create(true, builtInAuthority: true);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Cutover", "task-cutover");
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var human = new TenantSessionInitiator(owner.AccountId, login.SessionId, owner.OwnerPrincipalId);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var agent = await f.Agents.CreateAsync(human.PrincipalId, owner.TenantId, "examiner", 1, await Revision());
        var session = await f.Sessions.CreateAsync(owner.TenantId, agent.Instance.InstanceId, human, SessionMode.Text, await Revision(), 1);
        var sid = session.Owner.SessionId.ToString("D");
        await using (var db = f.Contexts!.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261027040000_SessionGoals");
            if (boundary == "definition")
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AgentInstances SET DefinitionId='general-assistant',ActiveVersion=34 WHERE InstanceId={agent.Instance.InstanceId.ToString("D")}");
            if (obsoleteChild)
            {
                // An unmappable installed child witness must remain untouched, even
                // when the caller explicitly runs the migration outside startup.
                var origin = JsonSerializer.Serialize(new { kind = 1, originatingSessionId = Guid.NewGuid(), originatingAgentRunId = Guid.NewGuid(),
                    initialBackgroundAgentRunId = Guid.NewGuid(), initialTitle = "Retained predecessor child" });
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Sessions SET OriginJson={origin} WHERE SessionId={sid}");
            }
        }
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => new SqliteMemoryStore(f.Contexts!, f.Clock).EnsureCreatedAsync().AsTask());
        Assert.Contains("task_cutover_reset_required", refused.Message); Assert.Contains("Do not delete customer data", refused.Message);
        var backupPath = Path.Combine(f.Root, "task-predecessor-backup.db");
        await using (var source = new SqliteConnection($"Data Source={f.Root}/store.db"))
        await using (var backup = new SqliteConnection($"Data Source={backupPath}"))
        { await source.OpenAsync(); await backup.OpenAsync(); source.BackupDatabase(backup); }
        await using (var db = f.Contexts!.CreateDbContext())
        {
            Assert.Equal("20261027040000_SessionGoals", (await db.Database.GetAppliedMigrationsAsync()).Last());
            if (obsolete)
            {
                var failure = await Assert.ThrowsAsync<SqliteException>(() => db.Database.MigrateAsync());
                Assert.Contains("task_cutover_reset_required", failure.Message);
                Assert.DoesNotContain("20261027060000_TaskDelegation", await db.Database.GetAppliedMigrationsAsync());
            }
            else
            {
                await db.Database.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges());
                await db.GetService<IMigrator>().MigrateAsync("20261027040000_SessionGoals");
                await db.Database.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges());
                Assert.NotNull(await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(owner.TenantId, session.Owner.SessionId));
            }
        }
        await using var restored = new SqliteConnection($"Data Source={backupPath}"); await restored.OpenAsync();
        await using var inspect = restored.CreateCommand();
        inspect.CommandText = "SELECT json_extract(OriginJson,'$.kind') FROM Sessions WHERE SessionId=$id"; inspect.Parameters.AddWithValue("$id", sid);
        Assert.Equal(obsoleteChild ? 1L : 0L, await inspect.ExecuteScalarAsync());
    }
}
