using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Definitions;
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
        var agentInstanceId = Guid.NewGuid();
        var agent = await f.CreateAgentAsync(first.OwnerPrincipalId, first.TenantId, agentInstanceId, alpha.Tenant.Revision);
        Assert.False((await f.Authorizer.CheckAsync(agent.PrincipalId, "automations.delete", AccessScope.ForTenant(first.TenantId))).Allowed);
        alpha = (await f.Store.ReadTenantAsync(first.TenantId))!;
        var ownerRole = alpha.Roles.Single(r => r.Key == "owner");
        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => f.Access.AssignRoleAsync(first.OwnerPrincipalId, first.TenantId,
            agent.PrincipalId, ownerRole.RoleId, AccessScope.ForTenant(first.TenantId), alpha.Tenant.Revision).AsTask());
        Assert.Equal("human_only", denied.ReasonCode);
        var custom = await f.Access.SaveRoleAsync(first.OwnerPrincipalId, first.TenantId, null, "Agent maintenance", ["agents.update"], alpha.Tenant.Revision);
        alpha = (await f.Store.ReadTenantAsync(first.TenantId))!;
        var assignment = await f.Access.AssignRoleAsync(first.OwnerPrincipalId, first.TenantId, agent.PrincipalId,
            custom.RoleId, AccessScope.ForResource(first.TenantId, "agent", agentInstanceId.ToString("D")), alpha.Tenant.Revision);
        Assert.True((await f.Authorizer.CheckAsync(agent.PrincipalId, "agents.update", AccessScope.ForResource(first.TenantId, "agent", agentInstanceId.ToString("D")))).Allowed);
        Assert.False((await f.Authorizer.CheckAsync(agent.PrincipalId, "agents.update", AccessScope.ForResource(first.TenantId, "agent", Guid.NewGuid().ToString("D")))).Allowed);
        Assert.False((await f.Authorizer.CheckAsync(agent.PrincipalId, "iam.roles.assign", AccessScope.ForTenant(first.TenantId))).Allowed);
        alpha = (await f.Store.ReadTenantAsync(first.TenantId))!;
        await f.Access.RevokeAssignmentAsync(first.OwnerPrincipalId, first.TenantId, assignment.AssignmentId, alpha.Tenant.Revision);
        Assert.False((await f.Authorizer.CheckAsync(agent.PrincipalId, "agents.update", AccessScope.ForResource(first.TenantId, "agent", agentInstanceId.ToString("D")))).Allowed);

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resource_grants_resolve_real_ownership_and_cannot_fabricate_or_reparent_targets(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var first = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var a = (await f.Store.ReadTenantAsync(first.TenantId))!;
        var memberId = a.Users.Single().UserId;
        var system = (await f.Store.ReadSystemAsync()).Principals.Single();
        var b = await f.Access.CreateTenantAsync(system.PrincipalId, first.AccountId, "Beta", "beta");
        var instanceId = Guid.NewGuid();
        var agent = await f.CreateAgentAsync(first.OwnerPrincipalId, first.TenantId, instanceId, a.Tenant.Revision);
        var betaAudit = (await f.Store.ReadAuditAsync(b.Tenant.TenantId, 200)).Count;
        var duplicate = await Assert.ThrowsAsync<SecurityConflictException>(() => f.CreateAgentAsync(b.Users.Single().PrincipalId,
            b.Tenant.TenantId, instanceId, b.Tenant.Revision).AsTask());
        Assert.Equal("agent_identity_exists", duplicate.ReasonCode);
        Assert.Equal(betaAudit, (await f.Store.ReadAuditAsync(b.Tenant.TenantId, 200)).Count);
        Assert.Empty((await f.Store.ReadTenantAsync(b.Tenant.TenantId))!.SystemIdentities);

        a = (await f.Store.ReadTenantAsync(first.TenantId))!;
        var role = await f.Access.SaveRoleAsync(first.OwnerPrincipalId, first.TenantId, null, "One member inspector",
            ["members.read", "iam.roles.read"], a.Tenant.Revision);
        a = (await f.Store.ReadTenantAsync(first.TenantId))!;
        var scope = IamResourceScopes.Create(first.TenantId, "member", memberId.ToString("N"));
        Assert.Equal(memberId.ToString("D"), scope.ResourceId);
        var grant = await f.Access.AssignRoleAsync(first.OwnerPrincipalId, first.TenantId, agent.PrincipalId, role.RoleId, scope, a.Tenant.Revision);
        Assert.True((await f.Authorizer.CheckAsync(agent.PrincipalId, "members.read", scope)).Allowed);
        Assert.False((await f.Authorizer.CheckAsync(agent.PrincipalId, "members.read", AccessScope.ForTenant(first.TenantId))).Allowed);
        var projection = await f.Access.ReadResourceAccessAsync(agent.PrincipalId, first.TenantId, "member", memberId.ToString("D"));
        Assert.Contains(projection.Assignments, x => x.AssignmentId == grant.AssignmentId);
        Assert.Contains(projection.Assignments, x => x.PrincipalId == first.OwnerPrincipalId && x.Scope.Kind == AccessScopeKind.Tenant);
        var firstPage = await f.Access.ReadResourceAccessAsync(agent.PrincipalId, first.TenantId, "member", memberId.ToString("D"), pageSize: 1);
        var secondPage = await f.Access.ReadResourceAccessAsync(agent.PrincipalId, first.TenantId, "member", memberId.ToString("D"), page: 1, pageSize: 1);
        Assert.Equal(projection.Assignments.Count, firstPage.Total);
        Assert.Single(firstPage.Assignments); Assert.Single(secondPage.Assignments);
        Assert.NotEqual(firstPage.Assignments[0].AssignmentId, secondPage.Assignments[0].AssignmentId);
        Assert.Equal("resource_type_mismatch", (await f.Authorizer.CheckAsync(first.OwnerPrincipalId, "members.read",
            IamResourceScopes.Create(first.TenantId, "role", role.RoleId.ToString("D")))).ReasonCode);
        Assert.Equal("resource_unavailable", (await f.Authorizer.CheckAsync(first.OwnerPrincipalId, "agents.read",
            AccessScope.ForResource(first.TenantId, "agent", Guid.NewGuid().ToString("D")))).ReasonCode);
        Assert.Equal("resource_unavailable", (await f.Authorizer.CheckAsync(first.OwnerPrincipalId, "automations.read",
            AccessScope.ForResource(first.TenantId, "automation", "fabricated"))).ReasonCode);
        var foreign = AccessScope.ForResource(first.TenantId, "member", b.Users.Single().UserId.ToString("D"));
        Assert.Equal("resource_unavailable", (await f.Authorizer.CheckAsync(first.OwnerPrincipalId, "members.read", foreign)).ReasonCode);
        a = (await f.Store.ReadTenantAsync(first.TenantId))!;
        var auditCount = (await f.Store.ReadAuditAsync(first.TenantId, 200)).Count;
        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => f.Access.AssignRoleAsync(first.OwnerPrincipalId,
            first.TenantId, agent.PrincipalId, role.RoleId, foreign, a.Tenant.Revision).AsTask());
        Assert.Equal("resource_unavailable", denied.ReasonCode);
        var forged = a with { Tenant = a.Tenant with { Revision = a.Tenant.Revision + 1 },
            Assignments = a.Assignments.Append(grant with { AssignmentId = Guid.NewGuid(), Scope = foreign }).ToArray() };
        await Assert.ThrowsAsync<ArgumentException>(() => f.Store.TrySaveTenantAsync(forged, a.Tenant.Revision,
            new(Guid.NewGuid(), first.TenantId, first.OwnerPrincipalId, "test.forged", "safe-test", "allowed", "test", f.Time.GetUtcNow())).AsTask());
        Assert.Equal(auditCount, (await f.Store.ReadAuditAsync(first.TenantId, 200)).Count);
        Assert.Equal(a.Tenant.Revision, (await f.Store.ReadTenantAsync(first.TenantId))!.Tenant.Revision);
        await f.Access.RevokeAssignmentAsync(first.OwnerPrincipalId, first.TenantId, grant.AssignmentId, a.Tenant.Revision);
        Assert.False((await f.Authorizer.CheckAsync(agent.PrincipalId, "members.read", scope)).Allowed);
        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!);
            Assert.False((await new IamAuthorizer(reopened).CheckAsync(agent.PrincipalId, "members.read", scope)).Allowed);
            Assert.Single((await reopened.ReadTenantAsync(first.TenantId))!.SystemIdentities);
            Assert.Empty((await reopened.ReadTenantAsync(b.Tenant.TenantId))!.SystemIdentities);
            // The database also rejects a duplicate Agent Instance owner if a writer bypasses services.
            await using var db = await f.Contexts!.CreateDbContextAsync();
            var identityId = Guid.NewGuid(); var principalId = Guid.NewGuid();
            db.Add(new Principal(principalId, PrincipalKind.SystemIdentity, b.Tenant.TenantId, null, null, identityId, IdentityStatus.Active));
            db.Add(new IamSystemIdentityRow(identityId, b.Tenant.TenantId, principalId, SystemIdentityKind.Agent, instanceId.ToString("D"), IdentityStatus.Active));
            var collision = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            var sql = Assert.IsType<SqliteException>(collision.InnerException);
            Assert.Equal(19, sql.SqliteErrorCode);
            Assert.Contains("SystemIdentities.AgentInstanceId", sql.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_instances_have_atomic_identity_scoped_inventory_and_archive_revocation(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var a = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var system = (await f.Store.ReadSystemAsync()).Principals.Single();
        var b = await f.Access.CreateTenantAsync(system.PrincipalId, a.AccountId, "Beta", "beta");
        var service = f.AgentService();
        var first = await service.CreateAsync(a.OwnerPrincipalId, a.TenantId, "examiner", 1, 1);
        var state = (await f.Store.ReadTenantAsync(a.TenantId))!;
        Assert.DoesNotContain(state.Assignments, x => x.PrincipalId == first.PrincipalId);
        Assert.NotNull(await f.Instances.FindAsync(first.Instance.InstanceId));
        Assert.Null(await ((ITenantAgentStore)f.Store).FindTenantAgentAsync(b.Tenant.TenantId, first.Instance.InstanceId));
        Assert.Equal(0, (await service.ListAsync(b.Users.Single().PrincipalId, b.Tenant.TenantId)).Total);
        Assert.Equal(0, (await service.ListAsync(first.PrincipalId, a.TenantId)).Total);
        var second = await service.CreateAsync(a.OwnerPrincipalId, a.TenantId, "general-assistant", 32, state.Tenant.Revision);
        Assert.Equal("browser.record.lookup", Assert.Single((await f.Instances.ReadSkillsAsync(second.Instance.InstanceId)).DefinitionStates).DefinitionSkillId);
        var page0 = await service.ListAsync(a.OwnerPrincipalId, a.TenantId, pageSize: 1);
        var page1 = await service.ListAsync(a.OwnerPrincipalId, a.TenantId, page: 1, pageSize: 1);
        Assert.Equal(2, page0.Total); Assert.Single(page0.Items); Assert.Single(page1.Items);
        Assert.NotEqual(page0.Items[0].Instance.InstanceId, page1.Items[0].Instance.InstanceId);
        state = (await f.Store.ReadTenantAsync(a.TenantId))!;
        await f.Access.AssignRoleAsync(a.OwnerPrincipalId, a.TenantId, first.PrincipalId,
            state.Roles.Single(r => r.Key == "contributor").RoleId,
            AccessScope.ForResource(a.TenantId, "agent", first.Instance.InstanceId.ToString("D")), state.Tenant.Revision);
        var ownList = await service.ListAsync(first.PrincipalId, a.TenantId);
        Assert.Equal(1, ownList.Total); Assert.Equal(first.Instance.InstanceId, Assert.Single(ownList.Items).Instance.InstanceId);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(first.PrincipalId, a.TenantId, second.Instance.InstanceId).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(b.Users.Single().PrincipalId, b.Tenant.TenantId, first.Instance.InstanceId).AsTask());
        state = (await f.Store.ReadTenantAsync(a.TenantId))!;
        var archived = await service.SetLifecycleAsync(a.OwnerPrincipalId, a.TenantId, first.Instance.InstanceId,
            AgentInstanceLifecycle.Archived, state.Tenant.Revision, first.Instance.Revision);
        Assert.Equal(AgentInstanceLifecycle.Archived, (await f.Instances.FindAsync(first.Instance.InstanceId))!.Lifecycle);
        Assert.False((await f.Authorizer.CheckAsync(first.PrincipalId, "agents.read", AccessScope.ForResource(a.TenantId, "agent", first.Instance.InstanceId.ToString("D")))).Allowed);
        var auditCount = (await f.Store.ReadAuditAsync(a.TenantId, 200)).Count;
        await Assert.ThrowsAsync<SecurityConflictException>(() => service.SetLifecycleAsync(a.OwnerPrincipalId, a.TenantId,
            first.Instance.InstanceId, AgentInstanceLifecycle.Active, state.Tenant.Revision, archived.Instance.Revision).AsTask());
        Assert.Equal(auditCount, (await f.Store.ReadAuditAsync(a.TenantId, 200)).Count);
        state = (await f.Store.ReadTenantAsync(a.TenantId))!;
        var restored = await service.SetLifecycleAsync(a.OwnerPrincipalId, a.TenantId, first.Instance.InstanceId,
            AgentInstanceLifecycle.Active, state.Tenant.Revision, archived.Instance.Revision);
        Assert.Equal(first.PrincipalId, restored.PrincipalId); Assert.Equal(first.SystemIdentityId, restored.SystemIdentityId);
        Assert.True((await f.Authorizer.CheckAsync(first.PrincipalId, "agents.read", AccessScope.ForResource(a.TenantId, "agent", first.Instance.InstanceId.ToString("D")))).Allowed);
        var legacy = await Assert.ThrowsAsync<AccessDeniedException>(() => f.Instances.UpdateWithExpectedRevisionAsync(
            new(first.Instance.InstanceId, restored.Instance.Revision, Lifecycle: AgentInstanceLifecycle.Archived), f.Time.GetUtcNow()).AsTask());
        Assert.Equal("tenant_context_required", legacy.ReasonCode);
        state = (await f.Store.ReadTenantAsync(a.TenantId))!;
        var rebound = state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 },
            SystemIdentities = state.SystemIdentities.Select(i => i.SystemIdentityId == second.SystemIdentityId ? i with { AgentInstanceId = Guid.NewGuid() } : i).ToArray() };
        await Assert.ThrowsAsync<ArgumentException>(() => f.Store.TrySaveTenantAsync(rebound, state.Tenant.Revision,
            new(f.Ids.NewId(), a.TenantId, a.OwnerPrincipalId, "test.reparent", "test", "allowed", "test", f.Time.GetUtcNow())).AsTask());
        var missingBacking = f.Ids.NewId(); var missingIdentity = f.Ids.NewId(); var missingPrincipal = f.Ids.NewId();
        var orphan = state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 },
            Principals = state.Principals.Append(new(missingPrincipal, PrincipalKind.SystemIdentity, a.TenantId, null, null, missingIdentity, IdentityStatus.Active)).ToArray(),
            SystemIdentities = state.SystemIdentities.Append(new(missingIdentity, a.TenantId, missingPrincipal, SystemIdentityKind.Agent, missingBacking, IdentityStatus.Active)).ToArray() };
        await Assert.ThrowsAsync<ArgumentException>(() => f.Store.TrySaveTenantAsync(orphan, state.Tenant.Revision,
            new(f.Ids.NewId(), a.TenantId, a.OwnerPrincipalId, "test.orphan", "test", "allowed", "test", f.Time.GetUtcNow())).AsTask());
        Assert.Equal(state.Tenant.Revision, (await f.Store.ReadTenantAsync(a.TenantId))!.Tenant.Revision);
        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!);
            var saved = await reopened.FindTenantAgentAsync(a.TenantId, first.Instance.InstanceId);
            Assert.Equal(first.PrincipalId, saved!.PrincipalId); Assert.Equal(AgentInstanceLifecycle.Active, saved.Instance.Lifecycle);
            await using var db = await f.Contexts!.CreateDbContextAsync();
            db.AgentInstances.Remove((await db.AgentInstances.FindAsync(first.Instance.InstanceId.ToString("D")))!);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        else
        {
            Assert.Throws<AgentCore.Application.Sessions.AgentCoreException>(() =>
                ((InMemoryAgentInstanceStore)f.Instances).RemoveForDeletion(first.Instance.InstanceId, restored.Instance.Revision));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Instance_identity_transactions_recheck_account_status_and_lose_stale_races_without_orphans(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var a = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var account = await f.Identity.RegisterAsync("creator@example.test", "Creator", "strong-creator-password");
        var state = (await f.Store.ReadTenantAsync(a.TenantId))!;
        var invite = await f.Access.InviteAsync(a.OwnerPrincipalId, a.TenantId, account.LoginKey,
            state.Roles.Single(r => r.Key == "contributor").RoleId, state.Tenant.Revision);
        var user = await f.Access.AcceptInvitationAsync(account.AccountId, a.TenantId, invite.Invitation.InvitationId, invite.Token);
        state = (await f.Store.ReadTenantAsync(a.TenantId))!;
        (TenantSecurityState Next, AgentInstance Instance, AuthorizationAudit Audit) Prepare(TenantSecurityState before)
        {
            var instanceId = f.Ids.NewId(); var principalId = f.Ids.NewId(); var identityId = f.Ids.NewId(); var now = f.Time.GetUtcNow();
            return (before with { Tenant = before.Tenant with { Revision = before.Tenant.Revision + 1 },
                Principals = before.Principals.Append(new(principalId, PrincipalKind.SystemIdentity, a.TenantId, null, null, identityId, IdentityStatus.Active)).ToArray(),
                SystemIdentities = before.SystemIdentities.Append(new(identityId, a.TenantId, principalId, SystemIdentityKind.Agent, instanceId, IdentityStatus.Active)).ToArray() },
                new(instanceId, "examiner", 1, new("Agent", "Guide", "Helps", "Calm"), AgentInstanceLifecycle.Active, now, now),
                new(f.Ids.NewId(), a.TenantId, user.PrincipalId, "agents.create", instanceId.ToString("D"), "allowed", "test", now));
        }
        var prepared = Prepare(state);
        Assert.True((await f.Authorizer.CheckAsync(user.PrincipalId, "agents.create", AccessScope.ForTenant(a.TenantId))).Allowed);
        var system = (await f.Store.ReadSystemAsync()).Principals.Single(p => p.AccountId == a.AccountId);
        var administration = new AccountAdministrationService(f.Store, f.Authorizer, f.Tokens, f.Ids, f.Time);
        await administration.SetStatusAsync(system.PrincipalId, account.AccountId, IdentityStatus.Suspended);
        var store = (ITenantAgentStore)f.Store;
        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => store.TryCreateTenantAgentAsync(prepared.Next,
            state.Tenant.Revision, prepared.Instance, [], prepared.Audit).AsTask());
        Assert.Equal("account_unavailable", denied.ReasonCode);
        Assert.Null(await f.Instances.FindAsync(prepared.Instance.InstanceId));
        Assert.Empty((await f.Store.ReadTenantAsync(a.TenantId))!.SystemIdentities);
        Assert.Equal(state.Tenant.Revision, (await f.Store.ReadTenantAsync(a.TenantId))!.Tenant.Revision);
        await administration.SetStatusAsync(system.PrincipalId, account.AccountId, IdentityStatus.Active);
        var first = Prepare(state); var second = Prepare(state);
        var outcomes = await Task.WhenAll(store.TryCreateTenantAgentAsync(first.Next, state.Tenant.Revision, first.Instance, [], first.Audit).AsTask(),
            store.TryCreateTenantAgentAsync(second.Next, state.Tenant.Revision, second.Instance, [], second.Audit).AsTask());
        Assert.Single(outcomes, x => x);
        Assert.Equal(1, (await store.ListTenantAgentsAsync(a.TenantId, null, 0, 100)).Total);
        Assert.Single(await f.Instances.ListAsync(100));
        var current = (await f.Store.ReadTenantAsync(a.TenantId))!;
        var stale = Prepare(current);
        var roleAssignment = current.Assignments.Single(x => x.PrincipalId == user.PrincipalId);
        await f.Access.RevokeAssignmentAsync(a.OwnerPrincipalId, a.TenantId, roleAssignment.AssignmentId, current.Tenant.Revision);
        Assert.False(await store.TryCreateTenantAgentAsync(stale.Next, current.Tenant.Revision, stale.Instance, [], stale.Audit));
        Assert.Null(await f.Instances.FindAsync(stale.Instance.InstanceId));
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
        public IAgentInstanceStore Instances { get; }
        private Fixture(string? path, IIamStore store, IDbContextFactory<AgentCoreDbContext>? contexts, IAgentInstanceStore? instances = null)
        {
            _path = path; Store = store; Contexts = contexts; Ids = new SystemIdGenerator(Time);
            Identity = new(Store, Passwords, Tokens, Ids, Time); Authorizer = new(Store);
            Access = new(Store, Authorizer, Identity, Tokens, Ids, Time);
            Instances = instances ?? new SqliteAgentInstanceStore(contexts!, Ids);
        }
        public TenantAgentService AgentService()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "agents"))) directory = directory.Parent;
            var templates = new FileAgentDefinitionStore(Path.Combine(directory!.FullName, "agents"), SyntheticProviderAliases.Default);
            return new(Store, (ITenantAgentStore)Store, templates, Authorizer, Ids, Time);
        }
        public async ValueTask<Principal> CreateAgentAsync(Guid actor, Guid tenant, Guid instanceId, long expected)
        {
            var state = (await Store.ReadTenantAsync(tenant))!;
            var principalId = Ids.NewId(); var identityId = Ids.NewId(); var now = Time.GetUtcNow();
            var principal = new Principal(principalId, PrincipalKind.SystemIdentity, tenant, null, null, identityId, IdentityStatus.Active);
            var next = state with { Tenant = state.Tenant with { Revision = expected + 1 },
                Principals = state.Principals.Append(principal).ToArray(),
                SystemIdentities = state.SystemIdentities.Append(new(identityId, tenant, principalId, SystemIdentityKind.Agent, instanceId, IdentityStatus.Active)).ToArray() };
            var instance = new AgentInstance(instanceId, "test-template", 1, new("Test agent", "Test", "Test", "Clear"), AgentInstanceLifecycle.Active, now, now);
            Assert.True(await ((ITenantAgentStore)Store).TryCreateTenantAgentAsync(next, expected, instance, [],
                new(Ids.NewId(), tenant, actor, "agents.create", instanceId.ToString("D"), "allowed", "test", now)));
            return principal;
        }
        public static async Task<Fixture> CreateAsync(bool sqlite)
        {
            if (!sqlite) { var instances = new InMemoryAgentInstanceStore(); return new(null, new InMemoryIamStore(instances, new InMemoryAgentDefinitionAdminStore(new SystemIdGenerator(TimeProvider.System))), null, instances); }
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
