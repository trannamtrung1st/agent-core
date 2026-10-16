using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Infrastructure.Tests;

public sealed class IamIdentityIntegrationTests
{
    // Representative vertical scenario: one login, two separate tenant Users, independent agent IAM,
    // revocation, last-Owner safety, and persistence reopen. Both supported stores run this workflow.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Same_account_has_distinct_tenant_authority_and_no_platform_or_agent_shortcut(bool sqlite)
    {
        await using var fixture = await Fixture.CreateAsync(sqlite);
        var f = fixture;
        var first = await f.Identity.BootstrapAsync("boss@example.test", "Boss", "strong-bootstrap-password", "Alpha", "alpha");
        var alpha = (await f.Store.ReadTenantAsync(first.TenantId))!;
        var system = (await f.Store.ReadSystemAsync()).Principals.Single();
        var second = await f.Access.CreateTenantAsync(system.PrincipalId, first.AccountId, "Beta", "beta");
        // The same Account is Owner in Alpha but only Reader in Beta; it keeps one global login.
        var betaOwner = await f.Identity.RegisterAsync("beta-owner@example.test", "Beta owner", "strong-beta-owner-password");
        var betaPrincipal = second.Users.Single().PrincipalId;
        var betaInvite = await f.Access.InviteAsync(betaPrincipal, second.Tenant.TenantId, betaOwner.LoginKey,
            second.Roles.Single(r => r.Key == "owner").RoleId, second.Tenant.Revision);
        await f.Access.AcceptInvitationAsync(betaOwner.AccountId, second.Tenant.TenantId, betaInvite.Invitation.InvitationId, betaInvite.Token);
        second = (await f.Store.ReadTenantAsync(second.Tenant.TenantId))!;
        await f.Access.AssignRoleAsync(betaPrincipal, second.Tenant.TenantId, betaPrincipal, second.Roles.Single(r => r.Key == "reader").RoleId,
            AccessScope.ForTenant(second.Tenant.TenantId), second.Tenant.Revision);
        second = (await f.Store.ReadTenantAsync(second.Tenant.TenantId))!;
        await f.Access.RevokeAssignmentAsync(betaPrincipal, second.Tenant.TenantId,
            second.Assignments.Single(a => a.PrincipalId == betaPrincipal && a.RoleId == second.Roles.Single(r => r.Key == "owner").RoleId).AssignmentId, second.Tenant.Revision);
        Assert.True((await f.Authorizer.CheckAsync(betaPrincipal, "agents.read", AccessScope.ForTenant(second.Tenant.TenantId))).Allowed);
        Assert.False((await f.Authorizer.CheckAsync(betaPrincipal, "agents.create", AccessScope.ForTenant(second.Tenant.TenantId))).Allowed);
        var login = await f.Identity.SignInAsync("boss@example.test", "strong-bootstrap-password");
        Assert.NotNull(login);
        Assert.Equal(first.AccountId, (await f.Identity.ValidateSessionAsync(login.Session.SessionId))!.AccountId);
        var memberships = await f.Store.ListMembershipsAsync(first.AccountId);
        Assert.Equal(2, memberships.Count);
        Assert.NotEqual(memberships[0].User.PrincipalId, memberships[1].User.PrincipalId);
        Assert.True((await f.Authorizer.CheckAsync(first.OwnerPrincipalId, "agents.create", AccessScope.ForTenant(alpha.Tenant.TenantId))).Allowed);
        Assert.False((await f.Authorizer.CheckAsync(first.OwnerPrincipalId, "agents.create", AccessScope.ForTenant(second.Tenant.TenantId))).Allowed);
        Assert.False((await f.Authorizer.CheckAsync(system.PrincipalId, "sessions.read", AccessScope.ForTenant(alpha.Tenant.TenantId))).Allowed);
        Assert.False((await f.Authorizer.CheckAsync(first.OwnerPrincipalId, "system.tenants.manage", AccessScope.System)).Allowed);

        var reader = await f.Identity.RegisterAsync("reader@example.test", "Reader", "strong-reader-password");
        var invite = await f.Access.InviteAsync(first.OwnerPrincipalId, first.TenantId, reader.LoginKey,
            alpha.Roles.Single(r => r.Key == "reader").RoleId, alpha.Tenant.Revision);
        var member = await f.Access.AcceptInvitationAsync(reader.AccountId, first.TenantId, invite.Invitation.InvitationId, invite.Token);
        Assert.True((await f.Authorizer.CheckAsync(member.PrincipalId, "agents.read", AccessScope.ForTenant(first.TenantId))).Allowed);
        Assert.False((await f.Authorizer.CheckAsync(member.PrincipalId, "agents.chat", AccessScope.ForTenant(first.TenantId))).Allowed);
        Assert.False((await f.Authorizer.CheckAsync(member.PrincipalId, "agents.read", AccessScope.ForTenant(second.Tenant.TenantId))).Allowed);

        alpha = (await f.Store.ReadTenantAsync(first.TenantId))!;
        var agent = await f.Access.RegisterAgentAsync(first.OwnerPrincipalId, first.TenantId, Guid.NewGuid(), alpha.Tenant.Revision);
        Assert.False((await f.Authorizer.CheckAsync(agent.PrincipalId, "automations.delete", AccessScope.ForTenant(first.TenantId))).Allowed);
        alpha = (await f.Store.ReadTenantAsync(first.TenantId))!;
        var ownerRole = alpha.Roles.Single(r => r.Key == "owner");
        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => f.Access.AssignRoleAsync(first.OwnerPrincipalId, first.TenantId,
            agent.PrincipalId, ownerRole.RoleId, AccessScope.ForTenant(first.TenantId), alpha.Tenant.Revision).AsTask());
        Assert.Equal("human_only", denied.ReasonCode);
        var custom = await f.Access.SaveRoleAsync(first.OwnerPrincipalId, first.TenantId, null, "Automation maintenance", ["automations.delete"], alpha.Tenant.Revision);
        alpha = (await f.Store.ReadTenantAsync(first.TenantId))!;
        var assignment = await f.Access.AssignRoleAsync(first.OwnerPrincipalId, first.TenantId, agent.PrincipalId,
            custom.RoleId, AccessScope.ForResource(first.TenantId, "automation", "exact-target"), alpha.Tenant.Revision);
        Assert.True((await f.Authorizer.CheckAsync(agent.PrincipalId, "automations.delete", AccessScope.ForResource(first.TenantId, "automation", "exact-target"))).Allowed);
        Assert.False((await f.Authorizer.CheckAsync(agent.PrincipalId, "automations.delete", AccessScope.ForResource(first.TenantId, "automation", "other-target"))).Allowed);
        Assert.False((await f.Authorizer.CheckAsync(agent.PrincipalId, "iam.roles.assign", AccessScope.ForTenant(first.TenantId))).Allowed);
        alpha = (await f.Store.ReadTenantAsync(first.TenantId))!;
        await f.Access.RevokeAssignmentAsync(first.OwnerPrincipalId, first.TenantId, assignment.AssignmentId, alpha.Tenant.Revision);
        Assert.False((await f.Authorizer.CheckAsync(agent.PrincipalId, "automations.delete", AccessScope.ForResource(first.TenantId, "automation", "exact-target"))).Allowed);

        alpha = (await f.Store.ReadTenantAsync(first.TenantId))!;
        var lastOwner = await Assert.ThrowsAsync<SecurityConflictException>(() => f.Access.SetMemberStatusAsync(first.OwnerPrincipalId,
            first.TenantId, alpha.Users.Single(u => u.PrincipalId == first.OwnerPrincipalId).UserId, IdentityStatus.Suspended, alpha.Tenant.Revision).AsTask());
        Assert.Equal("last_owner", lastOwner.ReasonCode);
        await f.Access.SetMemberStatusAsync(first.OwnerPrincipalId, first.TenantId, member.UserId, IdentityStatus.Removed, alpha.Tenant.Revision);
        Assert.Null(await f.Identity.ResolveUserAsync(reader.AccountId, first.TenantId));
        Assert.False((await f.Authorizer.CheckAsync(member.PrincipalId, "agents.read", AccessScope.ForTenant(first.TenantId))).Allowed);
        Assert.NotNull(await f.Identity.ResolveUserAsync(first.AccountId, second.Tenant.TenantId));
        await f.Identity.SignOutAsync(login.Session.SessionId);
        Assert.Null(await f.Identity.ValidateSessionAsync(login.Session.SessionId));

        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!);
            var identity = new IdentityService(reopened, f.Passwords, f.Tokens, f.Ids, f.Time);
            Assert.True(await reopened.HasBootstrapAsync());
            Assert.Equal(2, (await reopened.ListMembershipsAsync(first.AccountId)).Count);
            Assert.Null(await identity.ValidateSessionAsync(login.Session.SessionId));
            Assert.Null(await identity.ResolveUserAsync(reader.AccountId, first.TenantId));
            Assert.True((await new IamAuthorizer(reopened).CheckAsync(first.OwnerPrincipalId, "agents.read", AccessScope.ForTenant(first.TenantId))).Allowed);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_owner_removal_commits_only_one_and_stale_changes_leave_audit_unchanged(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var bootstrap = await f.Identity.BootstrapAsync("first@example.test", "First", "strong-first-password", "Organization", "organization");
        var state = (await f.Store.ReadTenantAsync(bootstrap.TenantId))!;
        var account = await f.Identity.RegisterAsync("second@example.test", "Second", "strong-second-password");
        var invitation = await f.Access.InviteAsync(bootstrap.OwnerPrincipalId, bootstrap.TenantId, account.LoginKey,
            state.Roles.Single(r => r.Key == "owner").RoleId, state.Tenant.Revision);
        var second = await f.Access.AcceptInvitationAsync(account.AccountId, bootstrap.TenantId, invitation.Invitation.InvitationId, invitation.Token);
        state = (await f.Store.ReadTenantAsync(bootstrap.TenantId))!;
        var first = state.Users.Single(u => u.AccountId == bootstrap.AccountId);
        async Task<bool> Remove(Guid actor, Guid target)
        {
            try { await f.Access.SetMemberStatusAsync(actor, bootstrap.TenantId, target, IdentityStatus.Removed, state.Tenant.Revision); return true; }
            catch (SecurityConflictException) { return false; }
            catch (AccessDeniedException) { return false; }
        }
        var outcomes = await Task.WhenAll(Remove(first.PrincipalId, first.UserId), Remove(second.PrincipalId, second.UserId));
        Assert.Single(outcomes, ok => ok);
        var current = (await f.Store.ReadTenantAsync(bootstrap.TenantId))!;
        Assert.Single(current.Users, u => u.Status == IdentityStatus.Active);
        var audit = await f.Store.ReadAuditAsync(bootstrap.TenantId, 200);
        var active = current.Users.Single(u => u.Status == IdentityStatus.Active);
        var stale = await Assert.ThrowsAsync<SecurityConflictException>(() => f.Access.SetMemberStatusAsync(active.PrincipalId,
            bootstrap.TenantId, active.UserId, IdentityStatus.Active, state.Tenant.Revision).AsTask());
        Assert.Equal("stale_revision", stale.ReasonCode);
        Assert.Equal(audit.Count, (await f.Store.ReadAuditAsync(bootstrap.TenantId, 200)).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bootstrap_is_one_use_invites_are_account_bound_expire_and_cannot_be_replayed(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        await Assert.ThrowsAsync<SecurityConflictException>(() => f.Identity.RegisterAsync("early@example.test", "Early", "strong-early-password").AsTask());
        var first = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "A", "alpha");
        await Assert.ThrowsAsync<SecurityConflictException>(() => f.Identity.BootstrapAsync("other@example.test", "Other", "strong-other-password", "B", "beta").AsTask());
        Assert.Null(await f.Identity.SignInAsync("owner@example.test", "wrong-password"));
        Assert.Null(await f.Identity.SignInAsync("unknown@example.test", "wrong-password"));
        var invited = await f.Identity.RegisterAsync("invitee@example.test", "Invitee", "strong-invitee-password");
        var stranger = await f.Identity.RegisterAsync("stranger@example.test", "Stranger", "strong-stranger-password");
        var state = (await f.Store.ReadTenantAsync(first.TenantId))!;
        var invitation = await f.Access.InviteAsync(first.OwnerPrincipalId, first.TenantId, invited.LoginKey,
            state.Roles.Single(r => r.Key == "reader").RoleId, state.Tenant.Revision);
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Access.AcceptInvitationAsync(stranger.AccountId, first.TenantId, invitation.Invitation.InvitationId, invitation.Token).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Access.AcceptInvitationAsync(invited.AccountId, first.TenantId, invitation.Invitation.InvitationId, "wrong-token").AsTask());
        var user = await f.Access.AcceptInvitationAsync(invited.AccountId, first.TenantId, invitation.Invitation.InvitationId, invitation.Token);
        Assert.Equal(user, await f.Access.AcceptInvitationAsync(invited.AccountId, first.TenantId, invitation.Invitation.InvitationId, invitation.Token));
        var login = await f.Identity.SignInAsync(invited.LoginKey, "strong-invitee-password");
        f.Time.Advance(TimeSpan.FromDays(8));
        Assert.Null(await f.Identity.ValidateSessionAsync(login!.Session.SessionId));
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Access.AcceptInvitationAsync(invited.AccountId, first.TenantId, invitation.Invitation.InvitationId, invitation.Token).AsTask());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Password_rotation_revokes_all_sessions_and_platform_suspension_preserves_last_active_owner(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var bootstrap = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-original-password", "Organization", "organization");
        var accountId = bootstrap.AccountId;
        var first = (await f.Identity.SignInAsync("owner@example.test", "strong-original-password"))!;
        var second = (await f.Identity.SignInAsync("owner@example.test", "strong-original-password"))!;
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Identity.ChangePasswordAsync(accountId, "wrong-password", "strong-replacement-password").AsTask());
        Assert.NotNull(await f.Identity.ValidateSessionAsync(first.Session.SessionId));
        await f.Identity.ChangePasswordAsync(accountId, "strong-original-password", "strong-replacement-password");
        Assert.Null(await f.Identity.ValidateSessionAsync(first.Session.SessionId));
        Assert.Null(await f.Identity.ValidateSessionAsync(second.Session.SessionId));
        Assert.Null(await f.Identity.SignInAsync("owner@example.test", "strong-original-password"));
        Assert.NotNull(await f.Identity.SignInAsync("owner@example.test", "strong-replacement-password"));
        var system = (await f.Store.ReadSystemAsync()).Principals.Single(p => p.AccountId == accountId);
        var administration = new AccountAdministrationService(f.Store, f.Authorizer, f.Tokens, f.Ids, f.Time);
        var lastOwner = await Assert.ThrowsAsync<SecurityConflictException>(() => administration.SetStatusAsync(system.PrincipalId, accountId, IdentityStatus.Suspended).AsTask());
        Assert.Equal("last_owner", lastOwner.ReasonCode);
        var other = await f.Identity.RegisterAsync("other@example.test", "Other", "strong-other-password");
        var otherPrincipal = (await f.Store.ReadSystemAsync()).Principals.Single(p => p.AccountId == other.AccountId);
        Assert.False((await f.Authorizer.CheckAsync(otherPrincipal.PrincipalId, "system.accounts.manage", AccessScope.System)).Allowed);
        var state = (await f.Store.ReadTenantAsync(bootstrap.TenantId))!;
        var invitation = await f.Access.InviteAsync(bootstrap.OwnerPrincipalId, bootstrap.TenantId, other.LoginKey,
            state.Roles.Single(r => r.Key == "owner").RoleId, state.Tenant.Revision);
        var otherMember = await f.Access.AcceptInvitationAsync(other.AccountId, bootstrap.TenantId, invitation.Invitation.InvitationId, invitation.Token);
        // Two Owner memberships permit suspending one Account. It cannot be used as the last Owner
        // witness when the other human tries to revoke their own membership afterward.
        await administration.SetStatusAsync(system.PrincipalId, accountId, IdentityStatus.Suspended);
        Assert.Null(await f.Identity.SignInAsync("owner@example.test", "strong-replacement-password"));
        Assert.False((await f.Authorizer.CheckAsync(bootstrap.OwnerPrincipalId, "agents.read", AccessScope.ForTenant(bootstrap.TenantId))).Allowed);
        state = (await f.Store.ReadTenantAsync(bootstrap.TenantId))!;
        var blocked = await Assert.ThrowsAsync<SecurityConflictException>(() => f.Access.SetMemberStatusAsync(otherMember.PrincipalId,
            bootstrap.TenantId, otherMember.UserId, IdentityStatus.Removed, state.Tenant.Revision).AsTask());
        Assert.Equal("last_owner", blocked.ReasonCode);
        Assert.Equal(IdentityStatus.Active, (await f.Identity.ResolveUserAsync(other.AccountId, bootstrap.TenantId))!.Status);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string? _path;
        public IIamStore Store { get; }
        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero));
        public IAccountPasswordHasher Passwords { get; } = new AccountPasswordHasher();
        public IIdentityTokens Tokens { get; } = new IdentityTokens();
        public IIdGenerator Ids { get; }
        public IDbContextFactory<AgentCoreDbContext>? Contexts { get; }
        public IdentityService Identity { get; }
        public IamAuthorizer Authorizer { get; }
        public TenantAccessService Access { get; }
        private Fixture(string? path, IIamStore store, IDbContextFactory<AgentCoreDbContext>? contexts)
        {
            _path = path; Store = store; Contexts = contexts; Ids = new SystemIdGenerator(Time);
            Identity = new(Store, Passwords, Tokens, Ids, Time); Authorizer = new(Store);
            Access = new(Store, Authorizer, Identity, Tokens, Ids, Time);
        }
        public static async Task<Fixture> CreateAsync(bool sqlite)
        {
            if (!sqlite) return new(null, new InMemoryIamStore(), null);
            var path = Path.Combine(Path.GetTempPath(), $"iam-security-{Guid.NewGuid():N}.db");
            var contexts = new ContextFactory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path};Foreign Keys=True;Default Timeout=10").Options);
            await using (var db = contexts.CreateDbContext()) await db.Database.MigrateAsync();
            return new(path, new SqliteIamStore(contexts), contexts);
        }
        public ValueTask DisposeAsync()
        {
            if (_path is not null) { SqliteConnection.ClearAllPools(); File.Delete(_path); File.Delete(_path + "-shm"); File.Delete(_path + "-wal"); }
            return ValueTask.CompletedTask;
        }
    }
    private sealed class ContextFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    { public AgentCoreDbContext CreateDbContext() => new(options); }
}
