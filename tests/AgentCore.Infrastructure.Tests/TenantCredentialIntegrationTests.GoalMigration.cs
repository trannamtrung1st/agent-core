using AgentCore.Application.Goals;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Goals;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Data.Sqlite;
namespace AgentCore.Infrastructure.Tests;
public sealed partial class TenantCredentialIntegrationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Goals_forward_cutover_converts_actual_predecessor_snapshots_or_refuses_unmappable_sources(bool unmappable)
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Goals", "goals");
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var human = new TenantSessionInitiator(owner.AccountId, login.SessionId, owner.OwnerPrincipalId); var tenant = owner.TenantId;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var agent = await f.Agents.CreateAsync(human.PrincipalId, tenant, "examiner", 1, await Revision());
        var session = await f.Sessions.CreateAsync(tenant, agent.Instance.InstanceId, human, SessionMode.Text, await Revision(), agent.Instance.Revision);
        var sid = session.Owner.SessionId.ToString("D"); var due = f.Clock.GetUtcNow().AddHours(1).ToUnixTimeMilliseconds();
        await using (var db = f.Contexts!.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261027020000_EvaluationSandboxExecutions");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE SessionSnapshots SET PurposeKind='Goal', PurposeDescription='Deliver the historical requested report', PurposeMetadataJson='{{\"source\":\"installed-snapshot\"}}', DeadlineAtUtc={due} WHERE SessionId={sid}");
            if (unmappable) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE SessionSnapshots SET PurposeDescription=NULL WHERE SessionId={sid}");
        }
        var startupRefusal = await Assert.ThrowsAsync<InvalidOperationException>(() => new SqliteMemoryStore(f.Contexts!, f.Clock).EnsureCreatedAsync().AsTask());
        Assert.Contains("Session Goal cutover required", startupRefusal.Message); Assert.Contains("Do not reset customer data", startupRefusal.Message);
        await using (var beforeMigration = f.Contexts!.CreateDbContext())
            Assert.Equal("20261027020000_EvaluationSandboxExecutions", (await beforeMigration.Database.GetAppliedMigrationsAsync()).Last());
        var backupPath = Path.Combine(f.Root, "predecessor-backup.db");
        await using (var source = new SqliteConnection($"Data Source={f.Root}/store.db"))
        await using (var backup = new SqliteConnection($"Data Source={backupPath}")) { await source.OpenAsync(); await backup.OpenAsync(); source.BackupDatabase(backup); }
        await using (var db = f.Contexts!.CreateDbContext())
        {
            if (unmappable)
            {
                var failure = await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync());
                Assert.Contains("GoalPurposeCannotMap", failure.Message); Assert.DoesNotContain("20261027040000_SessionGoals", await db.Database.GetAppliedMigrationsAsync());
                await using var connection = new SqliteConnection($"Data Source={f.Root}/store.db"); await connection.OpenAsync();
                await using var query = connection.CreateCommand(); query.CommandText = "SELECT PurposeKind FROM SessionSnapshots WHERE SessionId=$id"; query.Parameters.AddWithValue("$id", sid);
                Assert.Equal("Goal", await query.ExecuteScalarAsync()); return;
            }
            await db.GetService<IMigrator>().MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges());
        }
        var imported = (await ((ITenantSessionGoalStore)f.Iam).FindGoalAsync(tenant, session.Owner.SessionId, session.Owner.SessionId))!;
        Assert.Equal(GoalStatus.Proposed, imported.Status); Assert.Equal(GoalCheckpointStatus.Pending, imported.Checkpoints.Single().Status);
        Assert.Equal("Deliver the historical requested report", imported.Plan.Objective); Assert.True(imported.Plan.RequireFinalAcceptance);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(due), imported.Plan.DeadlineAt); Assert.False(imported.Plan.AutoContinuation!.Enabled);
        var history = await ((ITenantSessionGoalStore)f.Iam).ListGoalActivityAsync(tenant, session.Owner.SessionId, imported.GoalId, 0, 20);
        Assert.Equal("installed-snapshot", Assert.Single(history.Items).LegacyMetadata!["source"]);
        var saved = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(tenant, session.Owner.SessionId))!;
        Assert.Equal(SessionPurposeKind.Ongoing, saved.Snapshot.Purpose!.Kind); Assert.Null(saved.Snapshot.Purpose.Description);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(due), saved.Snapshot.Purpose.DeadlineAt);
        await using (var db = f.Contexts!.CreateDbContext())
            await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261027020000_EvaluationSandboxExecutions"));
        // A copied matching backup is the lossless rollback; the converted source is still present there.
        await using var restored = new SqliteConnection($"Data Source={backupPath}"); await restored.OpenAsync();
        await using var inspect = restored.CreateCommand(); inspect.CommandText = "SELECT PurposeKind || ':' || PurposeDescription FROM SessionSnapshots WHERE SessionId=$id"; inspect.Parameters.AddWithValue("$id", sid);
        Assert.Equal("Goal:Deliver the historical requested report", await inspect.ExecuteScalarAsync());
    }
}
