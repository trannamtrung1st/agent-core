using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Application.Execution;
using Microsoft.EntityFrameworkCore;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed class TenantSessionLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Typed_lifecycle_preserves_ownership_and_policy_and_delete_retires_grants_without_ID_reuse(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var actor = new TenantSessionInitiator(owner.AccountId, login.SessionId, owner.OwnerPrincipalId);
        var agent = await f.Agents.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "examiner", 1, await Revision());
        var item = await f.Sessions.CreateAsync(owner.TenantId, agent.Instance.InstanceId, actor, SessionMode.Text, await Revision(), 1);
        var id = item.Owner.SessionId; var scope = TenantSessionRules.Scope(owner.TenantId, id);
        var initial = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(owner.TenantId, id))!;
        var original = item.Owner;
        async Task<TenantSessionMetadata> Change(TenantSessionAction action, string? title = null) =>
            (await f.Sessions.ChangeAsync(owner.TenantId, actor, new(id, item.Revision, action, title), await Revision()))!;
        item = await Change(TenantSessionAction.Rename, "  Project review  "); Assert.Equal("Project review", item.Title);
        item = await Change(TenantSessionAction.Pause); Assert.Equal(SessionLifecycleStatus.Paused, item.LifecycleStatus);
        item = await Change(TenantSessionAction.Resume); Assert.Equal(SessionLifecycleStatus.Active, item.LifecycleStatus);
        item = await Change(TenantSessionAction.Archive); Assert.NotNull(item.ArchivedAt);
        await Assert.ThrowsAsync<AgentCoreException>(() => Change(TenantSessionAction.Resume));
        item = await Change(TenantSessionAction.Restore); Assert.Null(item.ArchivedAt);
        item = await Change(TenantSessionAction.Complete); Assert.Equal(SessionLifecycleStatus.Completed, item.LifecycleStatus);
        await Assert.ThrowsAsync<AgentCoreException>(() => Change(TenantSessionAction.Resume));
        Assert.Equal(original, item.Owner); Assert.DoesNotContain("Definition", System.Text.Json.JsonSerializer.Serialize(item));
        var retained = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(owner.TenantId, id))!;
        Assert.Equal(LifecycleTransitionSource.User, retained.Snapshot.LifecycleSource); Assert.Equal(initial.Snapshot.PinnedPersona, retained.Snapshot.PinnedPersona);
        var role = await f.Access.SaveRoleAsync(owner.OwnerPrincipalId, owner.TenantId, null, "Direct Session access", ["sessions.read"], await Revision());
        await f.Access.AssignRoleAsync(owner.OwnerPrincipalId, owner.TenantId, owner.OwnerPrincipalId, role.RoleId, scope, await Revision());
        Assert.Null(await f.Sessions.ChangeAsync(owner.TenantId, actor, new(id, item.Revision, TenantSessionAction.Delete), await Revision()));
        Assert.False(await f.Iam.OwnsResourceAsync(scope)); Assert.Null(await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(owner.TenantId, id));
        Assert.Equal(0, (await f.Sessions.ListAsync(owner.OwnerPrincipalId, owner.TenantId)).Total);
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!; Assert.DoesNotContain(state.Assignments, a => a.Scope == scope);
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Sessions.ReadAsync(owner.OwnerPrincipalId, owner.TenantId, id).AsTask());
        Assert.False(await ((ITenantSessionStore)f.Iam).TryCreateTenantSessionAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision,
            original, initial.Snapshot, actor, 1, new(Guid.NewGuid(), owner.TenantId, owner.OwnerPrincipalId, "sessions.create", id.ToString("D"), "allowed", "test", original.CreatedAt)));
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Memory.SaveAsync(initial.Snapshot, 0).AsTask());
        if (sqlite)
        {
            Assert.Null(await new SqliteIamStore(f.Contexts!).FindTenantSessionAsync(owner.TenantId, id));
            await using var db = f.Contexts!.CreateDbContext();
            Assert.Equal(original.SessionId.ToString("D"), (await db.Set<TenantSessionRecord>().SingleAsync()).SessionId);
            Assert.NotNull((await db.Sessions.SingleAsync()).DurablyDeletedAtUtc); Assert.Empty(await db.Entries.ToArrayAsync());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scoped_management_requires_read_resume_requires_chat_and_delete_can_withhold_metadata(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        var member = await Member(f, setup.Tenant, setup.Owner); var id = setup.Item.Owner.SessionId; var scope = TenantSessionRules.Scope(setup.Tenant, id);
        var login = (await f.Identity.SignInAsync("lifecycle@example.test", "strong-lifecycle-password"))!.Session;
        var actor = new TenantSessionInitiator(member.AccountId, login.SessionId, member.PrincipalId);
        async Task<Guid> Grant(string name, string[] permissions, AccessScope target)
        {
            var role = await f.Access.SaveRoleAsync(setup.Owner, setup.Tenant, null, name, permissions, await Revision(f, setup.Tenant));
            return (await f.Access.AssignRoleAsync(setup.Owner, setup.Tenant, member.PrincipalId, role.RoleId, target, await Revision(f, setup.Tenant))).AssignmentId;
        }
        var manage = await Grant("Manage one", ["sessions.manage"], scope); var witness = await Revision(f, setup.Tenant);
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Sessions.ChangeAsync(setup.Tenant, actor, new(id, 1, TenantSessionAction.Rename, "Not visible"), witness).AsTask());
        var read = await Grant("Read one", ["sessions.read"], scope);
        var item = (await f.Sessions.ChangeAsync(setup.Tenant, actor, new(id, 1, TenantSessionAction.Pause), await Revision(f, setup.Tenant)))!;
        witness = await Revision(f, setup.Tenant);
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Sessions.ChangeAsync(setup.Tenant, actor, new(id, item.Revision, TenantSessionAction.Resume), witness).AsTask());
        await Grant("Chat one", ["agents.chat"], AccessScope.ForResource(setup.Tenant, "agent", setup.Item.Owner.AgentInstanceId.ToString("D")));
        item = (await f.Sessions.ChangeAsync(setup.Tenant, actor, new(id, item.Revision, TenantSessionAction.Resume), await Revision(f, setup.Tenant)))!;
        await f.Access.RevokeAssignmentAsync(setup.Owner, setup.Tenant, manage, await Revision(f, setup.Tenant));
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Sessions.ChangeAsync(setup.Tenant, actor, new(id, 1, TenantSessionAction.Rename, "Revoked"), 1).AsTask());
        await Grant("Delete one", ["sessions.delete"], scope); await f.Access.RevokeAssignmentAsync(setup.Owner, setup.Tenant, read, await Revision(f, setup.Tenant));
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Sessions.ReadAsync(actor.PrincipalId, setup.Tenant, id).AsTask());
        Assert.Null(await f.Sessions.ChangeAsync(setup.Tenant, actor, new(id, item.Revision, TenantSessionAction.Delete), await Revision(f, setup.Tenant)));
        Assert.DoesNotContain((await f.Iam.ReadTenantAsync(setup.Tenant))!.Assignments, a => a.Scope == scope);
    }

    [Theory]
    [InlineData(false, "revoke")]
    [InlineData(true, "revoke")]
    [InlineData(false, "logout")]
    [InlineData(true, "logout")]
    [InlineData(false, "compete")]
    [InlineData(true, "compete")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    public async Task Prepared_mutation_cannot_commit_after_live_authority_or_revision_changes(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        var member = await Member(f, setup.Tenant, setup.Owner); var id = setup.Item.Owner.SessionId;
        var login = (await f.Identity.SignInAsync("lifecycle@example.test", "strong-lifecycle-password"))!.Session;
        var role = await f.Access.SaveRoleAsync(setup.Owner, setup.Tenant, null, "One manager", ["sessions.read", "sessions.manage"], await Revision(f, setup.Tenant));
        var assignment = await f.Access.AssignRoleAsync(setup.Owner, setup.Tenant, member.PrincipalId, role.RoleId, TenantSessionRules.Scope(setup.Tenant, id), await Revision(f, setup.Tenant));
        var gated = new MutationGate((ITenantSessionStore)f.Iam); using var cancel = new CancellationTokenSource();
        var task = f.SessionService(gated).ChangeAsync(setup.Tenant, new(member.AccountId, login.SessionId, member.PrincipalId),
            new(id, 1, TenantSessionAction.Rename, "Losing write"), await Revision(f, setup.Tenant), cancel.Token).AsTask();
        try
        {
            await gated.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (boundary == "revoke") await f.Access.RevokeAssignmentAsync(setup.Owner, setup.Tenant, assignment.AssignmentId, await Revision(f, setup.Tenant));
            if (boundary == "logout") await f.Identity.SignOutAsync(login.SessionId);
            if (boundary == "compete") await f.Sessions.ChangeAsync(setup.Tenant, setup.Actor, new(id, 1, TenantSessionAction.Rename, "Winning write"), await Revision(f, setup.Tenant));
            if (boundary == "cancel") cancel.Cancel();
        }
        finally { gated.Proceed.TrySetResult(); }
        if (boundary == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        else if (boundary == "logout") await Assert.ThrowsAsync<AccessDeniedException>(() => task);
        else await Assert.ThrowsAsync<SecurityConflictException>(() => task);
        var retained = await f.Sessions.ReadAsync(setup.Owner, setup.Tenant, id);
        Assert.Equal(boundary == "compete" ? 2 : 1, retained.Revision); Assert.NotEqual("Losing write", retained.Title);
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Action == "sessions.rename" && a.ActorPrincipalId == member.PrincipalId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Active_Run_fences_metadata_writes_and_terminal_Run_releases_the_guard(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f); var id = setup.Item.Owner.SessionId;
        var source = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(setup.Tenant, id))!.Snapshot;
        var snapshot = AgentCore.Tests.Shared.AgentRunTestFixtures.Snapshot(new(source.AgentInstanceId, Guid.NewGuid()), source.Definition, source.CreatedAt, id);
        var run = AgentCore.Tests.Shared.AgentRunTestFixtures.Run(snapshot, source.CreatedAt);
        if (sqlite)
        {
            await using var db = f.Contexts!.CreateDbContext();
            db.Activations.Add(AgentRunStoreMapping.ToActivationRecord(snapshot, run)); db.AgentRuns.Add(AgentRunStoreMapping.ToRecord(run)); await db.SaveChangesAsync();
        }
        else ((InMemoryMemoryStore)f.Memory).AgentRuns.Runs.Add(run.AgentRunId, run);
        var witness = await Revision(f, setup.Tenant);
        var failure = await Assert.ThrowsAsync<SecurityConflictException>(() => f.Sessions.ChangeAsync(setup.Tenant, setup.Actor, new(id, 1, TenantSessionAction.Rename, "Blocked"), witness).AsTask());
        Assert.Equal("session_in_use", failure.ReasonCode);
        var cancelled = new AgentRunCommand.RequestCancellation(run.Revision, source.CreatedAt.AddSeconds(1), null).Apply(run, Guid.NewGuid);
        Assert.True(cancelled.IsTerminal);
        if (sqlite)
        {
            await using var db = f.Contexts!.CreateDbContext(); var row = await db.AgentRuns.SingleAsync(); AgentRunStoreMapping.Apply(row, cancelled); await db.SaveChangesAsync();
        }
        else ((InMemoryMemoryStore)f.Memory).AgentRuns.Runs[run.AgentRunId] = cancelled;
        var saved = await f.Sessions.ChangeAsync(setup.Tenant, setup.Actor, new(id, 1, TenantSessionAction.Rename, "Allowed"), await Revision(f, setup.Tenant)); Assert.Equal("Allowed", saved!.Title);
    }

    [Fact]
    public async Task Typed_user_transitions_cannot_override_stored_completion_policy_or_attached_Runtime()
    {
        await using var f = await Fixture.Create(false); var setup = await Setup(f);
        var snapshot = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(setup.Tenant, setup.Item.Owner.SessionId))!.Snapshot;
        var protectedPolicy = snapshot with { CompletionPolicy = new(AgentCompletionAuthority.Disabled, false, false) };
        foreach (var action in new[] { TenantSessionAction.Complete, TenantSessionAction.Cancel })
            Assert.Throws<AgentCoreException>(() => TenantSessionMutations.Apply(protectedPolicy, new(snapshot.SessionId, 1, action), snapshot.CreatedAt));
        Assert.Throws<SecurityConflictException>(() => TenantSessionMutations.Apply(snapshot with { Status = SessionStatus.Attached }, new(snapshot.SessionId, 1, TenantSessionAction.Delete), snapshot.CreatedAt));
        Assert.Throws<AgentCoreException>(() => TenantSessionMutations.Apply(snapshot, new(snapshot.SessionId, 1, TenantSessionAction.Rename, "bad\nname"), snapshot.CreatedAt));
        var user = new ConversationEntry(Guid.NewGuid(), 1, Guid.NewGuid(), ConversationRole.User, "private body", null, EntryStatus.Completed, SessionMode.Text, 0, 12, snapshot.CreatedAt);
        var redacted = TenantSessionMutations.RedactEntry(user); Assert.Equal(user.EntryId, redacted.EntryId); Assert.Empty(redacted.Text); Assert.Equal(0, redacted.ReceivedTextEndExclusive);
    }

    [Fact]
    public async Task SQLite_delete_redacts_bodies_and_preserves_referenced_Activation_entry_identity_after_reopen()
    {
        await using var f = await Fixture.Create(true); var setup = await Setup(f); var id = setup.Item.Owner.SessionId;
        var source = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(setup.Tenant, id))!.Snapshot;
        var snapshot = AgentCore.Tests.Shared.AgentRunTestFixtures.Snapshot(new(source.AgentInstanceId, Guid.NewGuid()), source.Definition, source.CreatedAt, id);
        var run = AgentCore.Tests.Shared.AgentRunTestFixtures.Run(snapshot, source.CreatedAt);
        var terminal = new AgentRunCommand.RequestCancellation(run.Revision, source.CreatedAt.AddSeconds(1), null).Apply(run, Guid.NewGuid);
        var entry = snapshot.Entries.Single();
        await using (var db = f.Contexts!.CreateDbContext())
        {
            db.Entries.Add(new EntryRecord { EntryId = entry.EntryId.ToString("D"), SessionId = id.ToString("D"), EntrySequence = 1, Role = "User", Text = "private body",
                Status = "Completed", DeliveryMode = "Text", CreatedAtUtc = source.CreatedAt.ToUnixTimeMilliseconds(), EnvelopeJson = "private-envelope", UserPartsJson = "private-parts",
                FailureReferenceJson = "private-failure", AttachmentRefsJson = "private-attachment", SourceAdmissionFingerprint = "private-fingerprint" });
            db.Activations.Add(AgentRunStoreMapping.ToActivationRecord(snapshot, run)); db.AgentRuns.Add(AgentRunStoreMapping.ToRecord(terminal));
            db.ActivationSourceEntries.Add(new ActivationSourceEntryRecord { SessionId = id.ToString("D"), EntryId = entry.EntryId.ToString("D"), ActivationId = run.ActivationId.ToString("D"), Ordinal = 0 });
            var row = await db.Sessions.Include(s => s.Snapshot).SingleAsync(); row.Snapshot!.Summary = "private-summary"; await db.SaveChangesAsync();
        }
        await f.Sessions.ChangeAsync(setup.Tenant, setup.Actor, new(id, 1, TenantSessionAction.Delete), await Revision(f, setup.Tenant));
        Assert.Null(await new SqliteIamStore(f.Contexts!).FindTenantSessionAsync(setup.Tenant, id));
        await using (var db = f.Contexts!.CreateDbContext())
        {
            var retained = await db.Entries.SingleAsync(); Assert.Equal(entry.EntryId.ToString("D"), retained.EntryId); Assert.Empty(retained.Text);
            Assert.Null(retained.UserPartsJson); Assert.Null(retained.EnvelopeJson); Assert.Null(retained.FailureReferenceJson); Assert.Null(retained.AttachmentRefsJson); Assert.Null(retained.SourceAdmissionFingerprint);
            Assert.Equal(1, await db.ActivationSourceEntries.CountAsync()); Assert.Equal(1, await db.AgentRuns.CountAsync()); Assert.Empty((await db.Sessions.Include(s => s.Snapshot).SingleAsync()).Snapshot!.Summary);
        }
    }

    private static async Task<(Guid Tenant, Guid Owner, TenantSessionInitiator Actor, TenantSessionMetadata Item)> Setup(Fixture f)
    {
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var actor = new TenantSessionInitiator(owner.AccountId, login.SessionId, owner.OwnerPrincipalId);
        var agent = await f.Agents.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "examiner", 1, await Revision(f, owner.TenantId));
        var item = await f.Sessions.CreateAsync(owner.TenantId, agent.Instance.InstanceId, actor, SessionMode.Text, await Revision(f, owner.TenantId), 1);
        return (owner.TenantId, owner.OwnerPrincipalId, actor, item);
    }
    private static async Task<(Guid AccountId, Guid PrincipalId)> Member(Fixture f, Guid tenant, Guid actor)
    {
        var account = await f.Identity.RegisterAsync("lifecycle@example.test", "Lifecycle user", "strong-lifecycle-password");
        var state = (await f.Iam.ReadTenantAsync(tenant))!;
        var role = await f.Access.SaveRoleAsync(actor, tenant, null, "Directory", ["members.read"], state.Tenant.Revision);
        var invite = await f.Access.InviteAsync(actor, tenant, account.LoginKey, role.RoleId, await Revision(f, tenant));
        var user = await f.Access.AcceptInvitationAsync(account.AccountId, tenant, invite.Invitation.InvitationId, invite.Token); return (account.AccountId, user.PrincipalId);
    }
    private static async Task<long> Revision(Fixture f, Guid tenant) => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
    private sealed class MutationGate(ITenantSessionStore inner) : ITenantSessionStore
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Proceed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<TenantSession?> FindTenantSessionAsync(Guid tenantId, Guid sessionId, CancellationToken ct = default) => inner.FindTenantSessionAsync(tenantId, sessionId, ct);
        public ValueTask<TenantSessionPage> ListTenantSessionsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedIds, int page, int size, CancellationToken ct = default, bool includeArchived = true) => inner.ListTenantSessionsAsync(tenantId, allowedIds, page, size, ct, includeArchived);
        public ValueTask<bool> TryCreateTenantSessionAsync(TenantSecurityState next, long expectedSecurityRevision, TenantSessionRoot owner, SessionSnapshot snapshot, TenantSessionInitiator initiator, long expectedInstanceRevision, AuthorizationAudit audit, CancellationToken ct = default) => inner.TryCreateTenantSessionAsync(next, expectedSecurityRevision, owner, snapshot, initiator, expectedInstanceRevision, audit, ct);
        public async ValueTask<TenantSession?> TryChangeTenantSessionAsync(TenantSecurityState next, long expectedSecurityRevision, TenantSessionChange change, TenantSessionInitiator initiator, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default)
        { Entered.TrySetResult(); await Proceed.Task.WaitAsync(ct); return await inner.TryChangeTenantSessionAsync(next, expectedSecurityRevision, change, initiator, at, audit, ct); }
    }
}
