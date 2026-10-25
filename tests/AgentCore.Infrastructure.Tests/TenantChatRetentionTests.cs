using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed class TenantChatRetentionTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task Expiry_retires_work_and_private_files_but_preserves_uncertain_effect_evidence(bool sqlite, bool uncertain)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        await using var f = await Fixture.Create(sqlite, clock, attachmentAuthority: true, artifactAuthority: true);
        var account = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var actor = new TenantSessionInitiator(account.AccountId, login.SessionId, account.OwnerPrincipalId);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(account.TenantId))!.Tenant.Revision;
        var agent = await f.Agents.CreateAsync(actor.PrincipalId, account.TenantId, "examiner", 1, await Revision());
        var space = (await ((ITenantSpaceStore)f.Iam).FindPrivateSpaceAsync(account.TenantId, agent.Instance.InstanceId))!;
        var role = await f.Access.SaveRoleAsync(actor.PrincipalId, account.TenantId, null, "Exact Private Space read", ["spaces.read"], await Revision());
        await f.Access.AssignRoleAsync(actor.PrincipalId, account.TenantId, agent.PrincipalId, role.RoleId, TenantSpaceService.Scope(account.TenantId, space.SpaceId), await Revision());
        var chat = await f.Sessions.CreateAsync(account.TenantId, agent.Instance.InstanceId, actor, SessionMode.Text, await Revision(), 1, retentionKind: SessionRetentionKind.Temporary);
        var files = (ITenantAttachmentStore)f.Attachments;
        var attachmentService = new TenantAttachmentService(f.Iam, (ITenantSessionStore)f.Iam, (ITenantAgentStore)f.Iam, files,
            (ITenantAttachmentMutationStore)f.Iam, f.Identity, f.Authorizer, f.Ids, clock);
        var attachment = await attachmentService.UploadAsync(account.TenantId, chat.Owner.SessionId, actor, Guid.NewGuid(), await Revision(), chat.Revision,
            "private.txt", "text/plain", new MemoryStream("retained private file"u8.ToArray()));
        var receipt = await f.RunService().SubmitMessageAsync(account.TenantId, chat.Owner.SessionId, actor, Guid.NewGuid(), "Temporary queued request", [attachment], await Revision(), chat.Revision);
        var runs = (ITenantRunStore)f.Iam; var runtime = (ITenantRuntimeStore)f.Iam;
        var run = (await runs.FindTenantRunAsync(account.TenantId, receipt.RunId))!;
        run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentCore.Application.Execution.AgentRunCommand.Claim(run.Revision, clock.GetUtcNow(), Guid.NewGuid(), clock.GetUtcNow().AddMinutes(5)), Guid.NewGuid());
        var artifacts = (ITenantArtifactStore)f.Artifacts;
        var artifact = await artifacts.CreateTenantAsync(new(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation), Guid.NewGuid(), "output.txt", "text/plain", "private output"u8.ToArray());
        if (uncertain)
        {
            run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
                new AgentCore.Application.Execution.AgentRunCommand.MarkSideEffect(run.Revision, clock.GetUtcNow(), run.Claim!.Generation,
                    AgentRunSideEffectDisposition.Prepared, "retention-fixture-effect", new string('a', 64)), Guid.NewGuid());
            run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
                new AgentCore.Application.Execution.AgentRunCommand.MarkSideEffect(run.Revision, clock.GetUtcNow(), run.Claim!.Generation,
                    AgentRunSideEffectDisposition.InFlight, "retention-fixture-effect", new string('a', 64)), Guid.NewGuid());
        }
        clock.Advance(TimeSpan.FromHours(24));
        Assert.Empty(await runtime.ListRunnableTenantRunsAsync(clock.GetUtcNow(), 10));
        Assert.Null(await files.FindTenantAsync(account.TenantId, chat.Owner.SessionId, attachment));
        Assert.Null(await artifacts.FindTenantAsync(account.TenantId, chat.Owner.SessionId, artifact.ArtifactId));
        await Assert.ThrowsAsync<AccessDeniedException>(() => files.ReadTenantContentAsync(account.TenantId, chat.Owner.SessionId, attachment).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => artifacts.ReadTenantContentAsync(account.TenantId, chat.Owner.SessionId, artifact.ArtifactId).AsTask());
        var sessions = (ITenantSessionStore)f.Iam; var target = Assert.Single(await sessions.ListDueSessionsAsync(null, 10));
        Assert.Equal(!uncertain, await sessions.RetireDueSessionAsync(target, Guid.NewGuid()));
        AgentRun retained; SessionSnapshot snapshot;
        if (sqlite)
        {
            await using var db = f.Contexts!.CreateDbContext();
            retained = AgentRunStoreMapping.ToDomain(await db.AgentRuns.SingleAsync(r => r.AgentRunId == run.AgentRunId.ToString("D")));
            snapshot = SqliteMemoryStore.ToSnapshot(await db.Sessions.Include(s => s.Snapshot).SingleAsync(s => s.SessionId == chat.Owner.SessionId.ToString("D")), []);
        }
        else
        {
            var memory = (InMemoryMemoryStore)f.Memory;
            retained = memory.ReadRetainedTenantOperationRun(account.TenantId, run.AgentRunId)!;
            snapshot = memory.ReadRetainedTenantBackgroundSession(account.TenantId, chat.Owner.SessionId)!.Snapshot;
        }
        Assert.Equal(SessionLifecycleStatus.Expired, snapshot.LifecycleStatus);
        if (uncertain)
        {
            Assert.Null(snapshot.DurablyDeletedAt); Assert.True(retained.CancellationRequested);
            Assert.Equal(AgentRunSideEffectDisposition.InFlight, retained.SideEffect.Disposition);
            var retry = Assert.Single(await sessions.ListDueSessionsAsync(null, 10));
            Assert.False(await sessions.RetireDueSessionAsync(retry, Guid.NewGuid()));
            Assert.Equal(retry.Revision, Assert.Single(await sessions.ListDueSessionsAsync(null, 10)).Revision);
        }
        else
        {
            Assert.NotNull(snapshot.DurablyDeletedAt); Assert.Equal(AgentRunStatus.Cancelled, retained.Status);
            var cleanup = (ITenantAttachmentCleanupStore)f.Attachments;
            var fileTarget = Assert.Single((await cleanup.ListAttachmentCleanupAsync(null, 100)).Items, t => t.SessionId == chat.Owner.SessionId);
            Assert.True(await cleanup.CleanupAttachmentContentAsync(fileTarget)); Assert.True(await cleanup.CleanupAttachmentContentAsync(fileTarget));
            var outputs = (ITenantArtifactCleanupStore)f.Artifacts;
            var outputTarget = Assert.Single((await outputs.ListDeletedArtifactOwnersAsync(null, 100)).Items);
            Assert.True(await outputs.CleanupDeletedArtifactsAsync(outputTarget)); Assert.True(await outputs.CleanupDeletedArtifactsAsync(outputTarget));
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Temporary_access_expires_at_exact_deadline_before_scan_and_retirement_is_idempotent(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        await using var f = await Fixture.Create(sqlite, clock);
        var account = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var actor = new TenantSessionInitiator(account.AccountId, login.SessionId, account.OwnerPrincipalId);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(account.TenantId))!.Tenant.Revision;
        var agent = await f.Agents.CreateAsync(account.OwnerPrincipalId, account.TenantId, "examiner", 1, await Revision());
        var created = await f.Sessions.CreateAsync(account.TenantId, agent.Instance.InstanceId, actor, SessionMode.Text, await Revision(), 1,
            retentionKind: SessionRetentionKind.Temporary);
        var store = (ITenantSessionStore)f.Iam;
        Assert.Equal(created.CreatedAt.AddHours(24), created.RetentionDueAtUtc);
        Assert.Empty((await store.ListTenantSessionsAsync(account.TenantId, null, 0, 30, surface: SessionSurface.ChatList)).Items);
        clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromMilliseconds(1));
        Assert.NotNull(await store.FindTenantSessionAsync(account.TenantId, created.Owner.SessionId));
        Assert.Empty(await store.ListDueSessionsAsync(null, 10));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Null(await store.FindTenantSessionAsync(account.TenantId, created.Owner.SessionId));
        Assert.Null(await ((ITenantConversationStore)f.Iam).ReadTenantConversationAsync(account.TenantId, created.Owner.SessionId, null, null, 20));
        var freshLogin = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var freshActor = actor with { AuthenticationSessionId = freshLogin.SessionId };
        var expired = await Assert.ThrowsAsync<AccessDeniedException>(() => f.Sessions.ReadAsync(account.TenantId, created.Owner.SessionId, freshActor).AsTask());
        Assert.Equal("session_expired", expired.ReasonCode);
        var target = Assert.Single(await store.ListDueSessionsAsync(null, 10));
        Assert.False(await store.RetireDueSessionAsync(target with { Revision = target.Revision + 1 }, Guid.NewGuid()));
        Assert.True(await store.RetireDueSessionAsync(target, Guid.NewGuid()));
        Assert.False(await store.RetireDueSessionAsync(target, Guid.NewGuid()));
        Assert.Empty(await store.ListDueSessionsAsync(null, 10));
        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!, clock: clock);
            Assert.Null(await reopened.FindTenantSessionAsync(account.TenantId, created.Owner.SessionId));
            await using var db = f.Contexts!.CreateDbContext();
            var row = await db.Sessions.Include(s => s.Snapshot).SingleAsync(s => s.SessionId == created.Owner.SessionId.ToString("D"));
            Assert.NotNull(row.DurablyDeletedAtUtc); Assert.Equal("Expired", row.Snapshot!.LifecycleStatus);
            Assert.False(db.Database.HasPendingModelChanges());
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Saved_policy_is_pinned_once_and_due_only_after_terminal_completion(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var account = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var actor = new TenantSessionInitiator(account.AccountId, login.SessionId, account.OwnerPrincipalId);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(account.TenantId))!.Tenant.Revision;
        var agent = await f.Agents.CreateAsync(account.OwnerPrincipalId, account.TenantId, "examiner", 1, await Revision());
        var policies = new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, f.Clock);
        Assert.Null((await policies.ReadChatRetentionAsync(account.TenantId, actor)).SavedChatRetentionDays);
        await Assert.ThrowsAsync<ArgumentException>(() => policies.SaveChatRetentionAsync(account.TenantId, actor, 0, 1).AsTask());
        await policies.SaveChatRetentionAsync(account.TenantId, actor, 7, await Revision());
        var created = await f.Sessions.CreateAsync(account.TenantId, agent.Instance.InstanceId, actor, SessionMode.Text, await Revision(), 1);
        var store = (ITenantSessionStore)f.Iam;
        var active = (await store.FindTenantSessionAsync(account.TenantId, created.Owner.SessionId))!.Snapshot;
        Assert.Equal(7, active.RetainAfterTerminalDays); Assert.Null(active.RetentionDueAtUtc);
        await policies.SaveChatRetentionAsync(account.TenantId, actor, 1, await Revision());
        await f.Sessions.ChangeAsync(account.TenantId, actor, new(created.Owner.SessionId, created.Revision, TenantSessionAction.Complete), await Revision());
        var ended = (await store.FindTenantSessionAsync(account.TenantId, created.Owner.SessionId))!.Snapshot;
        Assert.Equal(7, ended.RetainAfterTerminalDays); Assert.Equal(ended.LifecycleChangedAt!.Value.AddDays(7), ended.RetentionDueAtUtc);
        await Assert.ThrowsAsync<SecurityConflictException>(() => policies.SaveChatRetentionAsync(account.TenantId, actor, 30, 1).AsTask());
    }
}
