using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed class TenantSessionIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Session_creation_pins_real_tenant_executor_and_human_without_global_profile_or_legacy_admission(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var session = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var agent = await f.Agents.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "examiner", 1, await Revision());
        var actor = new TenantSessionInitiator(owner.AccountId, session.SessionId, owner.OwnerPrincipalId);
        var initialRevision = await Revision();
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Sessions.CreateAsync(owner.TenantId, agent.Instance.InstanceId,
            actor with { PrincipalId = agent.PrincipalId }, SessionMode.Text, initialRevision, agent.Instance.Revision).AsTask());
        var created = await f.Sessions.CreateAsync(owner.TenantId, agent.Instance.InstanceId, actor, SessionMode.Text, await Revision(), agent.Instance.Revision);
        var snapshot = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(owner.TenantId, created.Owner.SessionId))!.Snapshot;
        Assert.DoesNotContain("Definition", System.Text.Json.JsonSerializer.Serialize(created));
        var id = created.Owner.SessionId; var scope = TenantSessionRules.Scope(owner.TenantId, id);
        Assert.Equal(agent.PrincipalId, created.Owner.ExecutorPrincipalId); Assert.Equal(owner.OwnerPrincipalId, created.Owner.CreatedByPrincipalId);
        Assert.Null(snapshot.ProfileId); Assert.Equal(agent.Instance.Persona, snapshot.PinnedPersona); Assert.NotNull(snapshot.ModelSelection);
        Assert.True(await f.Iam.OwnsResourceAsync(scope)); Assert.False((await f.Authorizer.CheckAsync(agent.PrincipalId, "sessions.read", scope)).Allowed);
        Assert.Equal(id, (await f.Sessions.ReadAsync(owner.OwnerPrincipalId, owner.TenantId, id)).Owner.SessionId);
        Assert.Equal(1, (await f.Sessions.ListAsync(owner.OwnerPrincipalId, owner.TenantId)).Total);
        Assert.Null(await f.Memory.LoadAsync(id)); Assert.Null(await f.Memory.LoadMetadataAsync(id));
        Assert.Empty(await f.Memory.ReadHistoryAsync(id, 0, 100)); Assert.Null(await f.Memory.ReadHistoryPageAsync(id, null, null, 100));
        Assert.Empty((await f.Memory.ListCatalogAsync(null, 100, true)).Items);
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Memory.SaveAsync(snapshot with { Revision = 2, ProfileId = Guid.NewGuid() }, 1).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Memory.SaveAsync(snapshot with { SessionId = Guid.NewGuid(), ProfileId = Guid.NewGuid() }, 0).AsTask());
        var legacySnapshot = AgentCore.Tests.Shared.AgentRunTestFixtures.Snapshot(new(agent.Instance.InstanceId, Guid.NewGuid()), snapshot.Definition, created.Owner.CreatedAt, id);
        var legacyRun = AgentCore.Tests.Shared.AgentRunTestFixtures.Run(legacySnapshot, created.Owner.CreatedAt);
        IAgentRunStore runs = sqlite ? new SqliteAgentRunStore(f.Contexts!, (SqliteMemoryStore)f.Memory, new Diagnostics())
            : new InMemoryAgentRunStore((InMemoryMemoryStore)f.Memory, new Diagnostics());
        await Assert.ThrowsAsync<AccessDeniedException>(() => runs.AdmitAsync(legacySnapshot with { Revision = 2 }, 1, legacyRun).AsTask());
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!; var forgedId = Guid.NewGuid();
        var forged = created.Owner with { SessionId = forgedId, ExecutorPrincipalId = owner.OwnerPrincipalId };
        await Assert.ThrowsAsync<ArgumentException>(() => ((ITenantSessionStore)f.Iam).TryCreateTenantSessionAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision,
            forged, snapshot with { SessionId = forgedId }, actor, 1, new(Guid.NewGuid(), owner.TenantId, owner.OwnerPrincipalId, "sessions.create", forgedId.ToString("D"), "allowed", "test", created.Owner.CreatedAt)).AsTask());
        Assert.Equal(state.Tenant.Revision, await Revision());
        await f.Memory.RecoverCrashedSessionsAsync(); Assert.Equal(1, (await f.Sessions.ReadAsync(owner.OwnerPrincipalId, owner.TenantId, id)).Revision);
        var system = (await f.Iam.ReadSystemAsync()).Principals.Single(); var beta = await f.Access.CreateTenantAsync(system.PrincipalId, owner.AccountId, "Beta", "beta");
        Assert.Equal(0, (await f.Sessions.ListAsync(beta.Users.Single().PrincipalId, beta.Tenant.TenantId)).Total);
        Assert.False(await f.Iam.OwnsResourceAsync(scope with { TenantId = beta.Tenant.TenantId }));
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Sessions.ReadAsync(beta.Users.Single().PrincipalId, beta.Tenant.TenantId, id).AsTask());
        if (sqlite)
        {
            var reopened = await new SqliteIamStore(f.Contexts!).FindTenantSessionAsync(owner.TenantId, id);
            Assert.NotNull(reopened); Assert.Equal(created.Owner, reopened.Owner); Assert.Null(reopened.Snapshot.ProfileId);
            await using var db = f.Contexts!.CreateDbContext(); Assert.False(db.Database.HasPendingModelChanges());
            Assert.Null((await db.Set<Principal>().SingleAsync(p => p.PrincipalId == system.PrincipalId)).TenantId);
            Assert.Equal(1, await db.Set<TenantSessionRecord>().CountAsync());
            await ((SqliteMemoryStore)f.Memory).EnsureCreatedAsync();
            var root = await db.Set<TenantSessionRecord>().SingleAsync();
            var invalidExecutor = owner.OwnerPrincipalId;
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantSessions SET ExecutorPrincipalId = {invalidExecutor} WHERE SessionId = {root.SessionId}"));
            db.ChangeTracker.Clear();
            var foreignInstance = Guid.NewGuid().ToString("D"); var key = id.ToString("D");
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Sessions SET AgentInstanceId = {foreignInstance} WHERE SessionId = {key}"));
            var foreignCreator = beta.Users.Single().PrincipalId; root = await db.Set<TenantSessionRecord>().SingleAsync();
            db.Entry(root).CurrentValues.SetValues(root with { CreatedByPrincipalId = foreignCreator });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        var live = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var suspended = live with { Tenant = live.Tenant with { Revision = live.Tenant.Revision + 1 },
            SystemIdentities = live.SystemIdentities.Select(i => i.PrincipalId == agent.PrincipalId ? i with { Status = IdentityStatus.Suspended } : i).ToArray(),
            Principals = live.Principals.Select(p => p.PrincipalId == agent.PrincipalId ? p with { Status = IdentityStatus.Suspended } : p).ToArray() };
        Assert.True(await f.Iam.TrySaveTenantAsync(suspended, live.Tenant.Revision, new(Guid.NewGuid(), owner.TenantId, owner.OwnerPrincipalId, "agent.suspend", agent.Instance.InstanceId.ToString("D"), "allowed", "test", created.Owner.CreatedAt)));
        var suspendedRevision = await Revision();
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Sessions.CreateAsync(owner.TenantId, agent.Instance.InstanceId, actor, SessionMode.Text, suspendedRevision, 1).AsTask());
    }
    private sealed class Diagnostics : IDiagnosticIdSource { public Guid NewId() => Guid.NewGuid(); }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_Session_grants_filter_before_paging_and_revoke_without_granting_chat(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var agent = await f.Agents.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "examiner", 1, await Revision());
        var initiator = new TenantSessionInitiator(owner.AccountId, login.SessionId, owner.OwnerPrincipalId);
        var first = await f.Sessions.CreateAsync(owner.TenantId, agent.Instance.InstanceId, initiator, SessionMode.Text, await Revision(), 1);
        var second = await f.Sessions.CreateAsync(owner.TenantId, agent.Instance.InstanceId, initiator, SessionMode.Text, await Revision(), 1);
        var member = await AddSessionUser(f, owner.TenantId, owner.OwnerPrincipalId);
        var role = await f.Access.SaveRoleAsync(owner.OwnerPrincipalId, owner.TenantId, null, "One Session reader", ["sessions.read"], await Revision());
        var assignment = await f.Access.AssignRoleAsync(owner.OwnerPrincipalId, owner.TenantId, member.User.PrincipalId, role.RoleId, TenantSessionRules.Scope(owner.TenantId, first.Owner.SessionId), await Revision());
        var list = await f.Sessions.ListAsync(member.User.PrincipalId, owner.TenantId, 0, 1); Assert.Equal(1, list.Total); Assert.Equal(first.Owner, Assert.Single(list.Items).Owner);
        Assert.False((await f.Authorizer.CheckAsync(member.User.PrincipalId, "agents.chat", AccessScope.ForResource(owner.TenantId, "agent", agent.Instance.InstanceId.ToString("D")))).Allowed);
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Sessions.ReadAsync(member.User.PrincipalId, owner.TenantId, second.Owner.SessionId).AsTask());
        await f.Access.RevokeAssignmentAsync(owner.OwnerPrincipalId, owner.TenantId, assignment.AssignmentId, await Revision());
        Assert.Equal(0, (await f.Sessions.ListAsync(member.User.PrincipalId, owner.TenantId)).Total);
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Sessions.ReadAsync(member.User.PrincipalId, owner.TenantId, first.Owner.SessionId).AsTask());
        var creatorRole = await f.Access.SaveRoleAsync(owner.OwnerPrincipalId, owner.TenantId, null, "Session creation", ["sessions.create", "sessions.read"], await Revision());
        await f.Access.AssignRoleAsync(owner.OwnerPrincipalId, owner.TenantId, member.User.PrincipalId, creatorRole.RoleId, AccessScope.ForTenant(owner.TenantId), await Revision());
        var memberLogin = (await f.Identity.SignInAsync("session-user@example.test", "strong-session-password"))!.Session;
        var memberActor = new TenantSessionInitiator(member.Account.AccountId, memberLogin.SessionId, member.User.PrincipalId); var withoutChatRevision = await Revision();
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Sessions.CreateAsync(owner.TenantId, agent.Instance.InstanceId, memberActor, SessionMode.Text, withoutChatRevision, 1).AsTask());
        var chatRole = await f.Access.SaveRoleAsync(owner.OwnerPrincipalId, owner.TenantId, null, "Chat with this agent", ["agents.chat"], await Revision());
        var agentScope = AccessScope.ForResource(owner.TenantId, "agent", agent.Instance.InstanceId.ToString("D"));
        await f.Access.AssignRoleAsync(owner.OwnerPrincipalId, owner.TenantId, member.User.PrincipalId, chatRole.RoleId, agentScope, await Revision());
        Assert.False((await f.Authorizer.CheckAsync(member.User.PrincipalId, "agents.read", agentScope)).Allowed);
        var admitted = await f.Sessions.CreateAsync(owner.TenantId, agent.Instance.InstanceId, memberActor, SessionMode.Text, await Revision(), 1);
        Assert.DoesNotContain("Definition", System.Text.Json.JsonSerializer.Serialize(admitted));
        Assert.Equal(member.User.PrincipalId, admitted.Owner.CreatedByPrincipalId); Assert.Equal(agent.PrincipalId, admitted.Owner.ExecutorPrincipalId);
        Assert.Equal(3, (await f.Sessions.ListAsync(member.User.PrincipalId, owner.TenantId)).Total);
    }

    [Theory]
    [InlineData(false, "revoke")]
    [InlineData(true, "revoke")]
    [InlineData(false, "logout")]
    [InlineData(true, "logout")]
    [InlineData(false, "password")]
    [InlineData(true, "password")]
    [InlineData(false, "archive")]
    [InlineData(true, "archive")]
    [InlineData(false, "compete")]
    [InlineData(true, "compete")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    public async Task Prepared_Session_cannot_commit_after_original_identity_or_admission_witness_changes(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var agent = await f.Agents.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "examiner", 1, await Revision());
        var member = await AddSessionUser(f, owner.TenantId, owner.OwnerPrincipalId);
        var login = (await f.Identity.SignInAsync("session-user@example.test", "strong-session-password"))!.Session;
        var role = await f.Access.SaveRoleAsync(owner.OwnerPrincipalId, owner.TenantId, null, "Create Sessions", ["sessions.create", "sessions.read", "agents.chat"], await Revision());
        var assignment = await f.Access.AssignRoleAsync(owner.OwnerPrincipalId, owner.TenantId, member.User.PrincipalId, role.RoleId, AccessScope.ForTenant(owner.TenantId), await Revision());
        var store = new GatedSessionStore((ITenantSessionStore)f.Iam); using var cancel = new CancellationTokenSource();
        var task = f.SessionService(store).CreateAsync(owner.TenantId, agent.Instance.InstanceId, new(member.Account.AccountId, login.SessionId, member.User.PrincipalId), SessionMode.Text, await Revision(), 1, ct: cancel.Token).AsTask();
        try
        {
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (boundary == "revoke") await f.Access.RevokeAssignmentAsync(owner.OwnerPrincipalId, owner.TenantId, assignment.AssignmentId, await Revision());
            if (boundary == "logout") await f.Identity.SignOutAsync(login.SessionId);
            if (boundary == "password") await f.Identity.ChangePasswordAsync(member.Account.AccountId, "strong-session-password", "strong-session-new-password");
            if (boundary == "archive") await f.Agents.SetLifecycleAsync(owner.OwnerPrincipalId, owner.TenantId, agent.Instance.InstanceId, AgentInstanceLifecycle.Archived, await Revision(), 1);
            if (boundary == "compete")
            {
                var ownerLogin = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
                await f.Sessions.CreateAsync(owner.TenantId, agent.Instance.InstanceId, new(owner.AccountId, ownerLogin.SessionId, owner.OwnerPrincipalId), SessionMode.Text, await Revision(), 1);
            }
            if (boundary == "cancel") cancel.Cancel();
        }
        finally { store.Proceed.TrySetResult(); }
        if (boundary == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        else if (boundary is "logout" or "password") await Assert.ThrowsAsync<AccessDeniedException>(() => task);
        else await Assert.ThrowsAsync<SecurityConflictException>(() => task);
        Assert.Null(await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(owner.TenantId, store.Owner!.SessionId));
        Assert.Equal(boundary == "compete" ? 1 : 0, (await f.Sessions.ListAsync(owner.OwnerPrincipalId, owner.TenantId)).Total);
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(owner.TenantId, 200), a => a.Action == "sessions.create" && a.Target == store.Owner.SessionId.ToString("D"));
    }

    private static async Task<(Account Account, TenantUser User)> AddSessionUser(Fixture f, Guid tenant, Guid actor)
    {
        var account = await f.Identity.RegisterAsync("session-user@example.test", "Session user", "strong-session-password");
        var state = (await f.Iam.ReadTenantAsync(tenant))!;
        var role = await f.Access.SaveRoleAsync(actor, tenant, null, "Directory access", ["members.read"], state.Tenant.Revision);
        state = (await f.Iam.ReadTenantAsync(tenant))!;
        var invite = await f.Access.InviteAsync(actor, tenant, account.LoginKey, role.RoleId, state.Tenant.Revision);
        var user = await f.Access.AcceptInvitationAsync(account.AccountId, tenant, invite.Invitation.InvitationId, invite.Token); return (account, user);
    }
    private sealed class GatedSessionStore(ITenantSessionStore inner) : ITenantSessionStore
    {
        public ValueTask<TenantSession?> TryChangeTenantSessionAsync(TenantSecurityState next, long expectedSecurityRevision, TenantSessionChange change,
            TenantSessionInitiator initiator, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default) => inner.TryChangeTenantSessionAsync(next, expectedSecurityRevision, change, initiator, at, audit, ct);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Proceed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TenantSessionRoot? Owner;
        public ValueTask<TenantSession?> FindTenantSessionAsync(Guid tenantId, Guid sessionId, CancellationToken ct = default) => inner.FindTenantSessionAsync(tenantId, sessionId, ct);
        public ValueTask<TenantSessionPage> ListTenantSessionsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedIds, int page, int pageSize, CancellationToken ct = default, bool includeArchived = true, SessionSurface? surface = null) => inner.ListTenantSessionsAsync(tenantId, allowedIds, page, pageSize, ct, includeArchived, surface);
        public async ValueTask<bool> TryCreateTenantSessionAsync(TenantSecurityState next, long expectedSecurityRevision, TenantSessionRoot owner, SessionSnapshot snapshot, TenantSessionInitiator initiator, long expectedInstanceRevision, AuthorizationAudit audit, CancellationToken ct = default)
        {
            Owner = owner; Entered.TrySetResult(); await Proceed.Task.WaitAsync(ct);
            return await inner.TryCreateTenantSessionAsync(next, expectedSecurityRevision, owner, snapshot, initiator, expectedInstanceRevision, audit, ct);
        }
    }
}
