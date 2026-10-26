using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Workspaces;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantChatRetentionTests
{
    private const string PrivateRunContent = "PRIVATE-CONVERSATION-RETENTION-9cafe";

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task Settled_run_conversation_content_is_removed_on_delete_or_expiry_and_stays_removed_after_reopen(bool sqlite, bool expiry)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        await using var f = await Fixture.Create(sqlite, clock);
        var account = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var actor = new TenantSessionInitiator(account.AccountId, login.SessionId, account.OwnerPrincipalId);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(account.TenantId))!.Tenant.Revision;
        var agent = await f.Agents.CreateAsync(actor.PrincipalId, account.TenantId, "examiner", 1, await Revision());
        var space = (await ((ITenantSpaceStore)f.Iam).FindPrivateSpaceAsync(account.TenantId, agent.Instance.InstanceId))!;
        var role = await f.Access.SaveRoleAsync(actor.PrincipalId, account.TenantId, null, "Private Space read", ["spaces.read"], await Revision());
        await f.Access.AssignRoleAsync(actor.PrincipalId, account.TenantId, agent.PrincipalId, role.RoleId, TenantSpaceService.Scope(account.TenantId, space.SpaceId), await Revision());
        var chat = await f.Sessions.CreateAsync(account.TenantId, agent.Instance.InstanceId, actor, SessionMode.Text, await Revision(), 1,
            retentionKind: SessionRetentionKind.Temporary);
        var receipt = await f.RunService().SubmitTextAsync(account.TenantId, chat.Owner.SessionId, actor, Guid.NewGuid(), PrivateRunContent, await Revision(), chat.Revision);
        var runtime = (ITenantRuntimeStore)f.Iam;
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(account.TenantId, receipt.RunId))!;
        run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Claim(run.Revision, clock.GetUtcNow(), Guid.NewGuid(), clock.GetUtcNow().AddMinutes(5)), Guid.NewGuid());
        var checkpoint = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write([
            new(ModelRole.User, PrivateRunContent),
            new(ModelRole.Assistant, "", ToolCalls: [new("private-call", "fixture.private", JsonSerializer.Serialize(new { value = PrivateRunContent }), PrivateRunContent)]),
            new(ModelRole.Tool, PrivateRunContent, ToolCallId: "private-call", Name: "fixture.private")]), 3, 123, 1000);
        run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Checkpoint(run.Revision, clock.GetUtcNow(), run.Claim!.Generation, checkpoint, PrivateRunContent), Guid.NewGuid());
        run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Complete(run.Revision, clock.GetUtcNow(), run.Claim!.Generation, PrivateRunContent, AgentRunOutcomeKind.NoAction, null), Guid.NewGuid());
        Assert.Contains(PrivateRunContent, await RetainedPayload(f, run));
        var sessions = (ITenantSessionStore)f.Iam;
        if (expiry)
        {
            clock.Advance(TimeSpan.FromHours(24));
            var target = Assert.Single(await sessions.ListDueSessionsAsync(null, 10));
            Assert.True(await sessions.RetireDueSessionAsync(target, Guid.NewGuid()));
            Assert.False(await sessions.RetireDueSessionAsync(target, Guid.NewGuid()));
        }
        else
        {
            var current = (await sessions.FindTenantSessionAsync(account.TenantId, chat.Owner.SessionId))!;
            await f.Sessions.ChangeAsync(account.TenantId, actor, new(chat.Owner.SessionId, current.Snapshot.Revision, TenantSessionAction.Delete), await Revision());
        }
        Assert.DoesNotContain(PrivateRunContent, await RetainedPayload(f, run));
        // Seed the pre-fix installed state: a deleted owner with an old full Run payload.
        // The existing startup cleanup scan must repair it without admitting another Run.
        var oldRow = AgentRunStoreMapping.ToRecord(run);
        if (sqlite)
        {
            await using var db = f.Contexts!.CreateDbContext();
            await db.Database.CloseConnectionAsync();
            var reopened = new SqliteIamStore(f.Contexts!, clock: clock);
            Assert.Null(await reopened.FindTenantSessionAsync(account.TenantId, chat.Owner.SessionId));
            Assert.DoesNotContain(PrivateRunContent, (await db.AgentRuns.AsNoTracking().SingleAsync(r => r.AgentRunId == run.AgentRunId.ToString("D"))).PayloadJson);
            Assert.All(await db.Entries.AsNoTracking().Where(e => e.SessionId == chat.Owner.SessionId.ToString("D")).ToArrayAsync(), e => Assert.DoesNotContain(PrivateRunContent, e.Text));
            var stored = await db.AgentRuns.SingleAsync(r => r.AgentRunId == oldRow.AgentRunId);
            stored.PayloadJson = oldRow.PayloadJson;
            await db.SaveChangesAsync();
        }
        else
        {
            var memory = (InMemoryMemoryStore)f.Memory;
            var retained = memory.ReadRetainedTenantOperationRun(account.TenantId, run.AgentRunId)!;
            oldRow.Revision = retained.Revision + 1;
            memory.SaveRetainedTenantOperationRun(AgentRunStoreMapping.ToDomain(oldRow), retained.Revision);
        }
        var cleanupStore = sqlite ? (ITenantWorkingCleanupStore)new SqliteIamStore(f.Contexts!, clock: clock) : (ITenantWorkingCleanupStore)f.Iam;
        var filesystem = new FileSessionWorkspace(Path.Combine(f.Root, "workspaces"), Path.Combine(f.Root, "templates"),
            sessions: f.Memory, instances: f.Instances, sandboxes: f.Sandboxes);
        var recovery = new TenantWorkingCleanupService(cleanupStore, filesystem);
        Assert.Equal(1, await recovery.RecoverAsync());
        var minimized = await RetainedPayload(f, run);
        Assert.DoesNotContain(PrivateRunContent, minimized);
        Assert.Equal(1, await recovery.RecoverAsync());
        Assert.Equal(minimized, await RetainedPayload(f, run));
    }

    private static async Task<string> RetainedPayload(Fixture f, AgentRun run)
    {
        if (f.Contexts is null) return AgentRunStoreMapping.ToRecord(((InMemoryMemoryStore)f.Memory).ReadRetainedTenantOperationRun(run.Owner.TenantId!.Value, run.AgentRunId)!).PayloadJson;
        await using var db = f.Contexts.CreateDbContext();
        return (await db.AgentRuns.AsNoTracking().SingleAsync(r => r.AgentRunId == run.AgentRunId.ToString("D"))).PayloadJson;
    }
}
