using AgentCore.Application.Evaluations;
using AgentCore.Application.Goals;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Evaluations;
using AgentCore.Domain.Goals;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
namespace AgentCore.Infrastructure.Tests;
public sealed partial class TenantCredentialIntegrationTests
{
    private static async Task<(Guid Tenant, Guid Session, TenantSessionInitiator Human)> GoalStabilizationSetup(Fixture f, bool runnable = false)
    {
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Goal stabilization", "goal-stabilization");
        var login = await SandboxHuman(f, owner.OwnerPrincipalId); var human = new TenantSessionInitiator(login.AccountId, login.AuthenticationSessionId, login.PrincipalId);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var agent = await f.Agents.CreateAsync(human.PrincipalId, owner.TenantId, "examiner", 1, await Revision());
        if (runnable)
        {
            await GrantModelFixtureSpaceRead(f, owner.TenantId, human.PrincipalId, agent.PrincipalId);
            var role = await f.Access.SaveRoleAsync(human.PrincipalId, owner.TenantId, null, "Goal execution", ["agents.read", "agents.chat", "sessions.read", "agent-runs.read", "goals.read", "goals.manage"], await Revision());
            await f.Access.AssignRoleAsync(human.PrincipalId, owner.TenantId, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(owner.TenantId), await Revision());
        }
        var session = await f.Sessions.CreateAsync(owner.TenantId, agent.Instance.InstanceId, human, SessionMode.Text, await Revision(), agent.Instance.Revision);
        return (owner.TenantId, session.Owner.SessionId, human);
    }
    private static async Task AssertGoalUnchanged(Fixture f, Guid tenant, Guid session, Guid id, TenantSessionInitiator human, SessionGoal before, int activityCount)
    {
        var service = GoalService(f); var actual = await service.ReadAsync(tenant, session, id, human);
        Assert.Equal(GoalRules.Hash(before), GoalRules.Hash(actual));
        Assert.Equal(activityCount, (await service.ActivityAsync(tenant, session, id, human, 0, 100, default)).Total);
        if (f.Contexts is not null)
        {
            var reopened = (ITenantSessionGoalStore)new SqliteIamStore(f.Contexts, builtIns: f.BuiltIns, clock: f.Clock);
            Assert.Equal(GoalRules.Hash(before), GoalRules.Hash(await reopened.FindGoalAsync(tenant, session, id)));
        }
    }
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Goals_stabilization_decisions_require_pending_revision_and_preserve_progress(bool sqlite, bool paused)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var (tenant, session, human) = await GoalStabilizationSetup(f); var service = GoalService(f); var id = Guid.NewGuid();
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var first = HumanGoal().Checkpoints[0]; var second = first with { CheckpointId = Guid.NewGuid(), Title = "Blocked result" };
        var third = first with { CheckpointId = Guid.NewGuid(), Title = "Awaiting source", Verification = [new(GoalVerificationKind.SourceReceipt)] };
        var plan = HumanGoal() with { Checkpoints = [first, second, third] };
        var goal = (await service.MutateAsync(tenant, session, id, human, new(GoalCommandKind.Create, "create", await Revision(), 0, 0, plan))).Goal;
        var start = new GoalCommand(GoalCommandKind.PlanDecision, "initial-accept", await Revision(), goal.Revision, goal.PlanRevision, Action: "accept");
        goal = (await service.MutateAsync(tenant, session, id, human, start)).Goal;
        Assert.True((await service.MutateAsync(tenant, session, id, human, start)).Replayed); Assert.Equal(GoalStatus.Active, goal.Status);
        goal = (await service.MutateAsync(tenant, session, id, human, new(GoalCommandKind.Plan, "propose", await Revision(), goal.Revision, goal.PlanRevision, plan with { Title = "Reviewed revision" }))).Goal;
        var accept = new GoalCommand(GoalCommandKind.PlanDecision, "accept-revision", await Revision(), goal.Revision, goal.PlanRevision, Action: "accept");
        goal = (await service.MutateAsync(tenant, session, id, human, accept)).Goal;
        Assert.Equal(2, goal.PlanRevision); Assert.True((await service.MutateAsync(tenant, session, id, human, accept)).Replayed);
        goal = (await service.MutateAsync(tenant, session, id, human, new(GoalCommandKind.Progress, "report", await Revision(), goal.Revision, goal.PlanRevision, CheckpointId: first.CheckpointId, Note: "Work remains recorded"))).Goal;
        goal = (await service.MutateAsync(tenant, session, id, human, new(GoalCommandKind.Progress, "block", await Revision(), goal.Revision, goal.PlanRevision, CheckpointId: second.CheckpointId, Note: "Missing independent input", Action: "blocked"))).Goal;
        goal = (await service.MutateAsync(tenant, session, id, human, new(GoalCommandKind.Verify, "pending", await Revision(), goal.Revision, goal.PlanRevision, CheckpointId: third.CheckpointId, Evidence: new(GoalVerificationKind.SourceReceipt, Guid.NewGuid())))).Goal;
        if (paused) goal = (await service.MutateAsync(tenant, session, id, human, new(GoalCommandKind.Control, "pause", await Revision(), goal.Revision, goal.PlanRevision, Action: "pause"))).Goal;
        Assert.Equal(new[] { GoalCheckpointStatus.InProgress, GoalCheckpointStatus.Blocked, GoalCheckpointStatus.VerificationPending }, goal.Checkpoints.Select(c => c.Status));
        var count = (await service.ActivityAsync(tenant, session, id, human, 0, 100, default)).Total; var security = await Revision();
        foreach (var decision in new[] { "accept", "reject" })
        {
            await Assert.ThrowsAsync<SecurityConflictException>(async () => await service.MutateAsync(tenant, session, id, human, new(GoalCommandKind.PlanDecision, "repeat-" + decision, security, goal.Revision, goal.PlanRevision, Action: decision)));
            await AssertGoalUnchanged(f, tenant, session, id, human, goal, count); Assert.Equal(security, await Revision());
        }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Goals_stabilization_verified_proof_survives_duplicate_failed_stale_and_denied_reverification(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var (tenant, session, human) = await GoalStabilizationSetup(f, true); var service = GoalService(f); var id = Guid.NewGuid();
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        using var http = new ProviderHttpFixture(); var probe = new GlobalModelProbe(); await using var services = RuntimeServices(f, http, probe, "Synthetic"); var evals = services.GetRequiredService<TenantEvaluationService>();
        var suite = await evals.SaveSuiteAsync(human.PrincipalId, tenant, Guid.NewGuid(), new("Accepted proof", [new("complete", "Completed", EvaluatorKind.Script, true, EvaluationPredicate.RunCompleted)], 0, await Revision()), human);
        var first = HumanGoal().Checkpoints[0] with { Verification = [new(GoalVerificationKind.EvaluationPass, suite.SuiteId, suite.Version, suite.ContentHash)] };
        var plan = HumanGoal() with { Checkpoints = [first, HumanGoal().Checkpoints[0] with { Dependencies = [first.CheckpointId] }] };
        var goal = (await service.MutateAsync(tenant, session, id, human, new(GoalCommandKind.Create, "create", await Revision(), 0, 0, plan, Confirm: true))).Goal;
        var view = await service.ListAsync(tenant, session, human); await service.FocusAsync(tenant, session, id, human, Guid.NewGuid(), view.SecurityRevision, view.SessionRevision, goal.Revision, default);
        view = await service.ListAsync(tenant, session, human); var work = await service.ContinueAsync(tenant, session, id, human, Guid.NewGuid(), view.SecurityRevision, view.SessionRevision, goal.Revision, goal.PlanRevision, default);
        var run = await ExecuteGoalRun(f, services, tenant, work.RunId, probe);
        var attempt = await evals.RunAsync(human.PrincipalId, tenant, new(EvaluationSubjectKind.AgentRun, run.AgentRunId, run.Revision, suite.SuiteId, suite.Version, "accepted-proof", await Revision()), human); Assert.Equal(EvaluationOutcome.Passed, attempt.Outcome);
        var proof = new GoalEvidence(GoalVerificationKind.EvaluationPass, attempt.AttemptId, run.AgentRunId);
        goal = await service.ReadAsync(tenant, session, id, human);
        goal = (await service.MutateAsync(tenant, session, id, human, new(GoalCommandKind.Verify, "verify", await Revision(), goal.Revision, goal.PlanRevision, CheckpointId: first.CheckpointId, Evidence: proof))).Goal;
        var verified = GoalRules.Hash(goal.Checkpoints[0]); Assert.Equal(GoalCheckpointStatus.Verified, goal.Checkpoints[0].Status); Assert.Equal(GoalStatus.Active, goal.Status);
        var duplicate = new GoalCommand(GoalCommandKind.Verify, "duplicate-proof", await Revision(), goal.Revision, goal.PlanRevision, CheckpointId: first.CheckpointId, Evidence: proof);
        goal = (await service.MutateAsync(tenant, session, id, human, duplicate)).Goal; Assert.Equal(verified, GoalRules.Hash(goal.Checkpoints[0]));
        Assert.True((await service.MutateAsync(tenant, session, id, human, duplicate)).Replayed);
        var failedSuite = await evals.SaveSuiteAsync(human.PrincipalId, tenant, Guid.NewGuid(), new("Failed proof", [new("manual", "Independent assessment", EvaluatorKind.Manual, true) ], 0, await Revision()), human);
        var failed = await evals.RunAsync(human.PrincipalId, tenant, new(EvaluationSubjectKind.AgentRun, run.AgentRunId, run.Revision, failedSuite.SuiteId, failedSuite.Version, "failed-proof", await Revision()), human);
        failed = await evals.ReviewAsync(human.PrincipalId, tenant, failed.AttemptId, new("manual", EvaluationOutcome.Failed, "The independently inspected result does not satisfy this alternate criterion", await Revision(), failed.Revision), human);
        Assert.Equal(EvaluationOutcome.Failed, failed.Outcome);
        var count = (await service.ActivityAsync(tenant, session, id, human, 0, 100, default)).Total;
        foreach (var replacement in new[] { proof with { SourceId = failed.AttemptId }, proof with { RunId = Guid.NewGuid() }, proof with { SourceId = Guid.NewGuid() } })
        {
            await Assert.ThrowsAsync<SecurityConflictException>(async () => await service.MutateAsync(tenant, session, id, human, new(GoalCommandKind.Verify, Guid.NewGuid().ToString("D"), await Revision(), goal.Revision, goal.PlanRevision, CheckpointId: first.CheckpointId, Evidence: replacement)));
            await AssertGoalUnchanged(f, tenant, session, id, human, goal, count);
        }
        var account = await f.Identity.RegisterAsync("proof-reader@example.test", "Limited reviewer", "strong-reviewer-password");
        var role = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Goal access without source access", ["sessions.read", "spaces.read", "goals.read", "goals.manage"], await Revision());
        var invitation = await f.Access.InviteAsync(human.PrincipalId, tenant, account.LoginKey, role.RoleId, await Revision()); var user = await f.Access.AcceptInvitationAsync(account.AccountId, tenant, invitation.Invitation.InvitationId, invitation.Token);
        var login = (await f.Identity.SignInAsync(account.LoginKey, "strong-reviewer-password"))!.Session; var limited = new TenantSessionInitiator(account.AccountId, login.SessionId, user.PrincipalId);
        goal = (await service.MutateAsync(tenant, session, id, limited, duplicate with { RequestKey = "denied-source-duplicate", ExpectedSecurityRevision = await Revision(), ExpectedGoalRevision = goal.Revision })).Goal;
        Assert.Equal(verified, GoalRules.Hash(goal.Checkpoints[0])); Assert.Empty(http.Requests);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Goals_stabilization_optional_prerequisites_cannot_be_skipped_but_independent_optional_work_can(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var (tenant, session, human) = await GoalStabilizationSetup(f); var service = GoalService(f); var id = Guid.NewGuid();
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var first = HumanGoal().Checkpoints[0] with { Required = false }; var second = first with { CheckpointId = Guid.NewGuid(), Dependencies = [first.CheckpointId] };
        var required = first with { CheckpointId = Guid.NewGuid(), Required = true, Dependencies = [second.CheckpointId] }; var unrelated = first with { CheckpointId = Guid.NewGuid() };
        var goal = (await service.MutateAsync(tenant, session, id, human, new(GoalCommandKind.Create, "create", await Revision(), 0, 0, HumanGoal() with { Checkpoints = [first, second, required, unrelated] }, Confirm: true))).Goal;
        var count = (await service.ActivityAsync(tenant, session, id, human, 0, 100, default)).Total;
        await Assert.ThrowsAsync<ArgumentException>(async () => await service.MutateAsync(tenant, session, id, human, new(GoalCommandKind.Progress, "skip-transitive", await Revision(), goal.Revision, goal.PlanRevision, CheckpointId: first.CheckpointId, Note: "Do not strand the required result", Action: "skip")));
        await AssertGoalUnchanged(f, tenant, session, id, human, goal, count);
        goal = (await service.MutateAsync(tenant, session, id, human, new(GoalCommandKind.Progress, "skip-independent", await Revision(), goal.Revision, goal.PlanRevision, CheckpointId: unrelated.CheckpointId, Note: "Not needed for delivery", Action: "skip"))).Goal;
        Assert.Equal(GoalCheckpointStatus.Skipped, goal.Checkpoints.Single(c => c.CheckpointId == unrelated.CheckpointId).Status);
        goal = (await service.MutateAsync(tenant, session, id, human, new(GoalCommandKind.Acceptance, "accept-first", await Revision(), goal.Revision, goal.PlanRevision, CheckpointId: first.CheckpointId, Note: "First prerequisite is satisfied", Action: "accept"))).Goal;
        await Assert.ThrowsAsync<ArgumentException>(async () => await service.MutateAsync(tenant, session, id, human, new(GoalCommandKind.Progress, "skip-direct", await Revision(), goal.Revision, goal.PlanRevision, CheckpointId: second.CheckpointId, Note: "Required dependency cannot be waived", Action: "skip")));
        goal = (await service.MutateAsync(tenant, session, id, human, new(GoalCommandKind.Acceptance, "accept-second", await Revision(), goal.Revision, goal.PlanRevision, CheckpointId: second.CheckpointId, Note: "Second prerequisite is satisfied", Action: "accept"))).Goal;
        goal = (await service.MutateAsync(tenant, session, id, human, new(GoalCommandKind.Acceptance, "accept-required", await Revision(), goal.Revision, goal.PlanRevision, CheckpointId: required.CheckpointId, Note: "Required delivery is satisfied", Action: "accept"))).Goal;
        Assert.Equal(GoalStatus.Completed, goal.Status); Assert.Equal(SessionLifecycleStatus.Active, (await f.Sessions.ReadAsync(tenant, session, human)).LifecycleStatus);
    }
}
