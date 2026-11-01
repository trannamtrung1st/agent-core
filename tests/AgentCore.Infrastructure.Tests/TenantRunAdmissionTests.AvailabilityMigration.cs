using AgentCore.Application.Ports;
using AgentCore.Application.Execution;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    [Fact]
    public async Task Legacy_availability_combinations_upgrade_atomically_with_identity_and_reopen()
    {
        await using var f = await Fixture.Create(true, targetMigration: "20261028190100_FormalWorkflowRetention");
        var setup = await Setup(f);
        var submitted = await f.RunService().SubmitTextAsync(setup.Tenant, setup.Session, setup.Owner, f.Ids.NewId(), "Retain upgrade history",
            (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, 1);
        var runtime = (ITenantRuntimeStore)f.Iam; var runs = (ITenantRunStore)f.Iam;
        var queued = (await runs.FindTenantRunAsync(setup.Tenant, submitted.RunId))!;
        var running = await runtime.ApplyTenantRuntimeCommandAsync(queued.Owner, queued.AgentRunId,
            new AgentRunCommand.Claim(queued.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var frame = new TenantRuntimeFrame(running.Owner, running.SessionId, running.AgentRunId, running.Claim!.Generation);
        var snapshot = (await runtime.LoadTenantRuntimeAsync(frame))!;
        var partial = new ConversationEntry(f.Ids.NewId(), snapshot.DurableLastEntrySequence + 1, null, ConversationRole.Assistant,
            "Retained upgrade prefix", running.ResponseId, EntryStatus.Streaming, SessionMode.Text, 0, 23, f.Clock.GetUtcNow());
        await runtime.SaveTenantRuntimeAsync(frame, snapshot with { Revision = snapshot.Revision + 1,
            Entries = [..snapshot.Entries, partial], LastEntrySequence = partial.Sequence }, snapshot.Revision);
        await using (var before = f.Contexts!.CreateDbContext())
            await before.Database.ExecuteSqlInterpolatedAsync($"UPDATE AgentInstances SET ShowInChat=0 WHERE InstanceId={setup.Agent.Instance.InstanceId.ToString("D")}");
        var ids = new List<Guid>();
        foreach (var (lifecycle, visible) in new[] { ("Active", 1), ("Active", 0), ("Archived", 1), ("Archived", 0) })
        {
            var agent = await f.Agents.CreateAsync(setup.Owner.PrincipalId, setup.Tenant, "examiner", 1,
                (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
            ids.Add(agent.Instance.InstanceId);
            await using var db = f.Contexts!.CreateDbContext();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AgentInstances SET Lifecycle={lifecycle},ShowInChat={visible} WHERE InstanceId={agent.Instance.InstanceId.ToString("D")}");
        }
        await using (var db = f.Contexts!.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync();
            Assert.False(db.Database.HasPendingModelChanges());
            var retained = await db.Entries.SingleAsync(e => e.EntryId == partial.EntryId.ToString("D"));
            Assert.Equal("Interrupted", retained.Status); Assert.Equal("agentDisabled", retained.InterruptReason);
            Assert.Equal(partial.Text, retained.Text); Assert.Equal(partial.ReceivedTextEndExclusive, retained.ReceivedTextEndExclusive);
            var cancelled = (await runs.FindTenantRunAsync(setup.Tenant, running.AgentRunId))!;
            Assert.Equal(AgentRunStatus.Cancelled, cancelled.Status); Assert.Null(cancelled.Claim); Assert.True(cancelled.CancellationRequested);
            await using var command = db.Database.GetDbConnection().CreateCommand();
            await db.Database.OpenConnectionAsync(); command.CommandText = "SELECT count(*) FROM pragma_table_info('AgentInstances') WHERE name='ShowInChat'";
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }
        var reopened = new SqliteIamStore(f.Contexts!);
        var state = (await reopened.ReadTenantAsync(setup.Tenant))!;
        for (var i = 0; i < ids.Count; i++)
        {
            var agent = (await reopened.FindTenantAgentAsync(setup.Tenant, ids[i]))!;
            Assert.Equal(i == 0 ? AgentInstanceLifecycle.Active : AgentInstanceLifecycle.Disabled, agent.Instance.Lifecycle);
            var expected = i == 0 ? IdentityStatus.Active : IdentityStatus.Suspended;
            Assert.Equal(expected, state.SystemIdentities.Single(s => s.AgentInstanceId == ids[i]).Status);
            Assert.Equal(expected, state.Principals.Single(s => s.PrincipalId == agent.PrincipalId).Status);
        }
    }
}
