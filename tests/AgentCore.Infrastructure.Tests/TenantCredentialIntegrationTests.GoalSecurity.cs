using AgentCore.Application.Goals;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Goals;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
namespace AgentCore.Infrastructure.Tests;
public sealed partial class TenantCredentialIntegrationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Goals_current_human_read_permissions_CAS_material_review_tenant_and_exact_expiry_are_enforced(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Goals", "goals");
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var human = new TenantSessionInitiator(owner.AccountId, login.SessionId, owner.OwnerPrincipalId); var tenant = owner.TenantId;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var agent = await f.Agents.CreateAsync(human.PrincipalId, tenant, "examiner", 1, await Revision());
        var session = await f.Sessions.CreateAsync(tenant, agent.Instance.InstanceId, human, SessionMode.Text, await Revision(), agent.Instance.Revision, retentionKind: SessionRetentionKind.Temporary); var sid = session.Owner.SessionId;
        var id = Guid.NewGuid(); var service = GoalService(f); var plan = HumanGoal();
        var goal = (await service.MutateAsync(tenant, sid, id, human, new(GoalCommandKind.Create, "create", await Revision(), 0, 0, plan, Confirm: true))).Goal;
        var account = await f.Identity.RegisterAsync("goal-reader@example.test", "Reader", "strong-reader-password");
        var role = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Session only", ["sessions.read", "spaces.read"], await Revision());
        var invite = await f.Access.InviteAsync(human.PrincipalId, tenant, account.LoginKey, role.RoleId, await Revision());
        var user = await f.Access.AcceptInvitationAsync(account.AccountId, tenant, invite.Invitation.InvitationId, invite.Token);
        var readerLogin = (await f.Identity.SignInAsync(account.LoginKey, "strong-reader-password"))!.Session;
        var reader = new TenantSessionInitiator(account.AccountId, readerLogin.SessionId, user.PrincipalId);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ListAsync(tenant, sid, reader).AsTask());
        var goalReader = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Goal reader", ["goals.read"], await Revision());
        await f.Access.AssignRoleAsync(human.PrincipalId, tenant, reader.PrincipalId, goalReader.RoleId, TenantSessionRules.Scope(tenant, sid), await Revision());
        Assert.Equal(id, Assert.Single((await service.ListAsync(tenant, sid, reader)).Goals.Items).GoalId);
        await Assert.ThrowsAsync<AccessDeniedException>(async () => await service.MutateAsync(tenant, sid, id, reader, new(GoalCommandKind.Progress, "reader-write", await Revision(), goal.Revision, goal.PlanRevision, CheckpointId: plan.Checkpoints[0].CheckpointId, Note: "Denied")));
        await Assert.ThrowsAsync<AccessDeniedException>(async () => await service.MutateAsync(tenant, sid, id, human with { PrincipalId = agent.PrincipalId }, new(GoalCommandKind.Acceptance, "forged-human", await Revision(), goal.Revision, goal.PlanRevision, CheckpointId: plan.Checkpoints[0].CheckpointId, Note: "Forged", Action: "accept")));
        var acceptanceOnly = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Human Goal reviewer", ["goals.accept"], await Revision());
        await f.Access.AssignRoleAsync(human.PrincipalId, tenant, reader.PrincipalId, acceptanceOnly.RoleId, TenantSessionRules.Scope(tenant, sid), await Revision());
        await Assert.ThrowsAsync<AccessDeniedException>(async () => await service.MutateAsync(tenant, sid, Guid.NewGuid(), reader, new(GoalCommandKind.Create, "accept-cannot-create", await Revision(), 0, 0, HumanGoal(), Confirm: true)));
        var assignments = (await f.Iam.ReadTenantAsync(tenant))!.Assignments;
        var acceptanceAssignment = assignments.Single(a => a.PrincipalId == reader.PrincipalId && a.RoleId == acceptanceOnly.RoleId);
        await f.Access.RevokeAssignmentAsync(human.PrincipalId, tenant, acceptanceAssignment.AssignmentId, await Revision());
        var stateRevision = await Revision();
        async Task<bool> Race(string key)
        {
            try { await service.MutateAsync(tenant, sid, id, human, new(GoalCommandKind.Progress, key, stateRevision, goal.Revision, goal.PlanRevision, CheckpointId: plan.Checkpoints[0].CheckpointId, Note: "Concurrent progress")); return true; }
            catch (SecurityConflictException) { return false; }
        }
        Assert.Single(await Task.WhenAll(Race("race-a"), Race("race-b")), won => won);
        goal = await service.ReadAsync(tenant, sid, id, human);
        var revised = plan with { SuccessCriteria = "Human explicitly reviews changed criteria", Checkpoints = [plan.Checkpoints[0] with { Required = false }, new(Guid.NewGuid(), "Replacement criterion", "An explicitly accepted new outcome", true, [], [new(GoalVerificationKind.HumanAcceptance)])] };
        goal = (await service.MutateAsync(tenant, sid, id, human, new(GoalCommandKind.Plan, "propose-material", await Revision(), goal.Revision, goal.PlanRevision, Plan: revised))).Goal;
        Assert.True(goal.Plan.Checkpoints[0].Required); Assert.False(goal.PendingPlan!.Checkpoints[0].Required);
        await Assert.ThrowsAsync<AccessDeniedException>(async () => await service.MutateAsync(tenant, sid, id, reader, new(GoalCommandKind.PlanDecision, "reader-accept", await Revision(), goal.Revision, goal.PlanRevision, Action: "accept")));
        goal = (await service.MutateAsync(tenant, sid, id, human, new(GoalCommandKind.PlanDecision, "accept-material", await Revision(), goal.Revision, goal.PlanRevision, Action: "accept"))).Goal;
        Assert.Equal(2, goal.PlanRevision); Assert.Null(goal.PendingPlan); Assert.False(goal.Plan.Checkpoints[0].Required);
        var system = (await f.Iam.ReadSystemAsync()).Principals.Single(p => p.AccountId == owner.AccountId); var beta = await f.Access.CreateTenantAsync(system.PrincipalId, owner.AccountId, "Beta", "beta");
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(beta.Tenant.TenantId, sid, id, human with { PrincipalId = beta.Users.Single().PrincipalId }).AsTask());
        Assert.NotNull(await ((ITenantSessionGoalStore)f.Iam).FindGoalAsync(tenant, sid, id));
        clock.Advance(TimeSpan.FromHours(24));
        Assert.Null(await ((ITenantSessionGoalStore)f.Iam).FindGoalAsync(tenant, sid, id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ListAsync(tenant, sid, human).AsTask());
        if (sqlite) { await using var db = f.Contexts!.CreateDbContext(); Assert.Equal(1, await db.Set<SessionGoalRow>().CountAsync()); }
        var target = Assert.Single(await ((ITenantSessionStore)f.Iam).ListDueSessionsAsync(null, 10));
        Assert.True(await ((ITenantSessionStore)f.Iam).RetireDueSessionAsync(target, Guid.NewGuid()));
        if (sqlite) { await using var db = f.Contexts!.CreateDbContext(); Assert.Empty(await db.Set<SessionGoalRow>().ToArrayAsync()); Assert.Empty(await db.Set<GoalActivityRow>().ToArrayAsync()); }
        await f.Identity.SignOutAsync(login.SessionId);
        await Assert.ThrowsAsync<AccessDeniedException>(async () => await service.MutateAsync(tenant, sid, id, human, new(GoalCommandKind.Control, "logged-out", await Revision(), goal.Revision, goal.PlanRevision, Action: "cancel")));
    }
}
