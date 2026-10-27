using AgentCore.Application.Goals;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Execution;
using AgentCore.Application.Events;
using AgentCore.Application.Testing;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Goals;
using AgentCore.Domain.Evaluations;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Infrastructure.Tests;
public sealed partial class TenantCredentialIntegrationTests
{
    private static GoalPlan HumanGoal(string name = "Deliver a reviewed outcome", bool final = false) => new(name, "Deliver the requested result", "All required criteria hold",
        [new(Guid.NewGuid(), "Reviewed result", "A qualified human accepts the delivered result", true, [], [new(GoalVerificationKind.HumanAcceptance)])], RequireFinalAcceptance: final);
    private static TenantSessionGoalService GoalService(Fixture f) => new(f.Iam, (ITenantSessionStore)f.Iam, (ITenantSessionGoalStore)f.Iam, (ITenantAgentStore)f.Iam,
        (ITenantSpaceStore)f.Iam, f.Identity, f.Authorizer, f.Ids, f.Clock, f.RunService());

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Goals_real_human_plan_focus_progress_acceptance_CAS_replay_and_retention(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Goals", "goals");
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var human = new TenantSessionInitiator(owner.AccountId, login.SessionId, owner.OwnerPrincipalId); var tenant = owner.TenantId;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var agent = await f.Agents.CreateAsync(human.PrincipalId, tenant, "examiner", 1, await Revision());
        var session = await f.Sessions.CreateAsync(tenant, agent.Instance.InstanceId, human, SessionMode.Text, await Revision(), agent.Instance.Revision);
        var service = GoalService(f); var id = Guid.NewGuid(); var plan = HumanGoal(final: true);
        var create = new GoalCommand(GoalCommandKind.Create, "create-once", await Revision(), 0, 0, plan);
        var saved = await service.MutateAsync(tenant, session.Owner.SessionId, id, human, create);
        Assert.Equal(GoalStatus.Proposed, saved.Goal.Status); Assert.Null((await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(tenant, session.Owner.SessionId))!.Snapshot.FocusedGoalId);
        var replay = await service.MutateAsync(tenant, session.Owner.SessionId, id, human, create); Assert.True(replay.Replayed); Assert.Equal(saved.Goal.Revision, replay.Goal.Revision);
        await Assert.ThrowsAsync<SecurityConflictException>(() => service.MutateAsync(tenant, session.Owner.SessionId, id, human, create with { Plan = plan with { Title = "Different request" } }).AsTask());
        async Task<SessionGoal> Change(GoalCommandKind kind, string key, SessionGoal current, string? action = null, string? note = null, Guid? checkpoint = null, GoalPlan? changed = null)
            => (await service.MutateAsync(tenant, session.Owner.SessionId, id, human, new(kind, key, await Revision(), current.Revision, current.PlanRevision, changed, checkpoint, note, action))).Goal;
        var goal = await Change(GoalCommandKind.PlanDecision, "start", saved.Goal, "accept"); Assert.Equal(GoalStatus.Active, goal.Status);
        var view = await service.ListAsync(tenant, session.Owner.SessionId, human);
        var focused = await service.FocusAsync(tenant, session.Owner.SessionId, id, human, Guid.NewGuid(), view.SecurityRevision, view.SessionRevision, goal.Revision, default);
        Assert.Equal(id, (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(tenant, session.Owner.SessionId))!.Snapshot.FocusedGoalId);
        Assert.Equal(1, focused.FocusRevision);
        var cp = plan.Checkpoints[0].CheckpointId;
        var reported = await Change(GoalCommandKind.Progress, "report", goal, note: "Delivered; needs human verification", checkpoint: cp);
        Assert.Equal(GoalCheckpointStatus.InProgress, reported.Checkpoints[0].Status); Assert.Equal(GoalStatus.Active, reported.Status);
        await Assert.ThrowsAsync<SecurityConflictException>(() => Change(GoalCommandKind.Progress, "stale", goal, note: "stale report", checkpoint: cp));
        await Assert.ThrowsAsync<ArgumentException>(() => Change(GoalCommandKind.Progress, "skip-required", reported, "skip", "No waiver", cp));
        var accepted = await Change(GoalCommandKind.Acceptance, "accept-checkpoint", reported, "accept", "Inspected delivered outcome", cp);
        Assert.Equal(GoalCheckpointStatus.Verified, accepted.Checkpoints[0].Status); Assert.Equal(GoalStatus.Active, accepted.Status);
        var completed = await Change(GoalCommandKind.Acceptance, "accept-goal", accepted, "accept", "All overall criteria are satisfied");
        Assert.Equal(GoalStatus.Completed, completed.Status); Assert.Equal(human.PrincipalId, completed.FinalAcceptedBy);
        Assert.Equal(SessionLifecycleStatus.Active, (await f.Sessions.ReadAsync(tenant, session.Owner.SessionId, human)).LifecycleStatus);
        Assert.Equal(5, (await service.ActivityAsync(tenant, session.Owner.SessionId, id, human, 0, 20, default)).Total);
        if (sqlite)
        {
            var reopened = (ITenantSessionGoalStore)new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock);
            Assert.Equal(GoalStatus.Completed, (await reopened.FindGoalAsync(tenant, session.Owner.SessionId, id))!.Status);
        }
        var latest = await f.Sessions.ReadAsync(tenant, session.Owner.SessionId, human);
        await f.Sessions.ChangeAsync(tenant, human, new(session.Owner.SessionId, latest.Revision, TenantSessionAction.Delete), await Revision());
        Assert.Null(await ((ITenantSessionGoalStore)f.Iam).FindGoalAsync(tenant, session.Owner.SessionId, id));
        Assert.Empty((await ((ITenantSessionGoalStore)f.Iam).ListGoalActivityAsync(tenant, session.Owner.SessionId, id, 0, 20)).Items);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Goals_real_Synthetic_Run_binding_and_exact_Evaluation_pass_complete_without_ending_Session(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true, artifactAuthority: true);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Goals", "goals");
        var login = await SandboxHuman(f, owner.OwnerPrincipalId); var human = new TenantSessionInitiator(login.AccountId, login.AuthenticationSessionId, login.PrincipalId); var tenant = owner.TenantId;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        using var http = new ProviderHttpFixture(); var probe = new GlobalModelProbe(); await using var services = RuntimeServices(f, http, probe, "Synthetic");
        var evals = services.GetRequiredService<AgentCore.Application.Evaluations.TenantEvaluationService>();
        var suite = await evals.SaveSuiteAsync(human.PrincipalId, tenant, Guid.NewGuid(), new("Exact completed Run", [new("completed", "Run completed", EvaluatorKind.Script, true, EvaluationPredicate.RunCompleted)], 0, await Revision()), human);
        var agent = await f.Agents.CreateAsync(human.PrincipalId, tenant, "customer-support", 3, await Revision());
        await GrantModelFixtureSpaceRead(f, tenant, human.PrincipalId, agent.PrincipalId);
        var role = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Goal runtime", ["sessions.read", "agent-runs.read", "goals.read", "goals.manage", "agent-runs.create", "agents.chat", "artifacts.create"], await Revision());
        await f.Access.AssignRoleAsync(human.PrincipalId, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision());
        var session = await services.GetRequiredService<TenantSessionService>().CreateAsync(tenant, agent.Instance.InstanceId, human, SessionMode.Text, await Revision(), agent.Instance.Revision);
        var service = GoalService(f); var id = Guid.NewGuid(); var cp = Guid.NewGuid();
        var plan = HumanGoal() with { Checkpoints = [new(cp, "Verified Synthetic result", "Exact completed Run evaluation passes", true, [], [new(GoalVerificationKind.EvaluationPass, suite.SuiteId, suite.Version, suite.ContentHash), new(GoalVerificationKind.SourceReceipt)])] };
        var goal = (await service.MutateAsync(tenant, session.Owner.SessionId, id, human, new(GoalCommandKind.Create, "create-evaluation-goal", await Revision(), 0, 0, plan, Confirm: true))).Goal;
        var view = await service.ListAsync(tenant, session.Owner.SessionId, human);
        await service.FocusAsync(tenant, session.Owner.SessionId, id, human, Guid.NewGuid(), view.SecurityRevision, view.SessionRevision, goal.Revision, default);
        view = await service.ListAsync(tenant, session.Owner.SessionId, human);
        var receipt = await service.ContinueAsync(tenant, session.Owner.SessionId, id, human, Guid.NewGuid(), view.SecurityRevision, view.SessionRevision, goal.Revision, goal.PlanRevision, default);
        var runs = (ITenantRunStore)f.Iam; var storage = (ITenantRuntimeStore)f.Iam; var run = (await runs.FindTenantRunAsync(tenant, receipt.RunId))!;
        Assert.Equal(id, run.Admission.GoalWork!.GoalId); Assert.Equal(cp, run.Admission.GoalWork.CheckpointId);
        run = await storage.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var publication = await services.GetRequiredService<TenantArtifactOperationService>().ExecuteAsync(new(tenant, run.AgentRunId), run.Claim!.Generation, "goal-publication-once", "{\"displayName\":\"briefing.md\",\"content\":\"# Independently reviewed Synthetic briefing\"}");
        Assert.Equal(OperationDecisionKind.Allowed, publication.Decision); Assert.NotNull(publication.Receipt);
        var published = await ((ITenantArtifactStore)f.Artifacts).FindTenantAsync(tenant, run.SessionId, publication.Receipt.OperationId);
        Assert.NotNull(published); Assert.Equal(run.AgentRunId, published.AgentRunId);
        var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation); var output = new CapturingSessionOutput();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using (var runtime = services.GetRequiredService<SessionRuntimeFactory>().CreateTenant((await storage.LoadTenantRuntimeAsync(frame))!, output, frame, storage, runs,
            services.GetRequiredService<TenantRunExecutionAuthority>(), services.GetRequiredService<TenantToolAuthorization>()))
        {
            Assert.True(await runtime.DispatchAgentRunAsync(run.AgentRunId, true, deadline.Token));
            await output.WaitForAsync(x => x.Payload is ResponseCompletedOutput or ErrorOutput, deadline.Token); await runtime.WaitUntilIdleAsync(deadline.Token);
        }
        run = (await runs.FindTenantRunAsync(tenant, receipt.RunId))!; Assert.True(run.Status == AgentRunStatus.Completed, $"{run.Failure?.Code}: {run.Failure?.Summary}\n{string.Join("\n", probe.Diagnostics)}");
        var attempt = await evals.RunAsync(human.PrincipalId, tenant, new(EvaluationSubjectKind.AgentRun, run.AgentRunId, run.Revision, suite.SuiteId, suite.Version, "goal-proof", await Revision()), human);
        Assert.Equal(EvaluationOutcome.Passed, attempt.Outcome);
        goal = await service.ReadAsync(tenant, session.Owner.SessionId, id, human);
        goal = (await service.MutateAsync(tenant, session.Owner.SessionId, id, human, new(GoalCommandKind.Verify, "verify-publication-only", await Revision(), goal.Revision, goal.PlanRevision, CheckpointId: cp,
            Evidence: new(GoalVerificationKind.SourceReceipt, published.ArtifactId, run.AgentRunId)))).Goal;
        Assert.Equal(GoalCheckpointStatus.VerificationPending, goal.Checkpoints[0].Status); // Publication alone does not satisfy the independent Evaluation policy.
        var otherSuite = await evals.SaveSuiteAsync(human.PrincipalId, tenant, Guid.NewGuid(), new("Unaccepted Suite", [new("completed", "Run completed", EvaluatorKind.Script, true, EvaluationPredicate.RunCompleted)], 0, await Revision()), human);
        var wrong = await evals.RunAsync(human.PrincipalId, tenant, new(EvaluationSubjectKind.AgentRun, run.AgentRunId, run.Revision, otherSuite.SuiteId, otherSuite.Version, "wrong-suite", await Revision()), human);
        Assert.Equal(EvaluationOutcome.Passed, wrong.Outcome);
        goal = (await service.MutateAsync(tenant, session.Owner.SessionId, id, human, new(GoalCommandKind.Verify, "verify-wrong-suite", await Revision(), goal.Revision, goal.PlanRevision, CheckpointId: cp,
            Evidence: new(GoalVerificationKind.EvaluationPass, wrong.AttemptId, run.AgentRunId)))).Goal;
        Assert.Equal(GoalCheckpointStatus.VerificationPending, goal.Checkpoints[0].Status);
        await Assert.ThrowsAsync<AccessDeniedException>(async () => await service.MutateAsync(tenant, session.Owner.SessionId, id, human, new(GoalCommandKind.Verify, "wrong-run", await Revision(), goal.Revision, goal.PlanRevision, CheckpointId: cp,
            Evidence: new(GoalVerificationKind.EvaluationPass, attempt.AttemptId, Guid.NewGuid()))));
        var reviewerAccount = await f.Identity.RegisterAsync("goal-source-reviewer@example.test", "Reviewer", "strong-reviewer-password");
        var reviewerRole = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Goal source limited", ["sessions.read", "spaces.read", "goals.read", "goals.manage", "agent-runs.read"], await Revision());
        var invite = await f.Access.InviteAsync(human.PrincipalId, tenant, reviewerAccount.LoginKey, reviewerRole.RoleId, await Revision());
        var reviewerUser = await f.Access.AcceptInvitationAsync(reviewerAccount.AccountId, tenant, invite.Invitation.InvitationId, invite.Token);
        var reviewerLogin = (await f.Identity.SignInAsync(reviewerAccount.LoginKey, "strong-reviewer-password"))!.Session;
        var reviewer = new TenantSessionInitiator(reviewerAccount.AccountId, reviewerLogin.SessionId, reviewerUser.PrincipalId);
        goal = (await service.MutateAsync(tenant, session.Owner.SessionId, id, reviewer, new(GoalCommandKind.Verify, "denied-source-read", await Revision(), goal.Revision, goal.PlanRevision, CheckpointId: cp,
            Evidence: new(GoalVerificationKind.EvaluationPass, attempt.AttemptId, run.AgentRunId)))).Goal;
        Assert.Equal(GoalStatus.Active, goal.Status); Assert.Equal(GoalCheckpointStatus.VerificationPending, goal.Checkpoints[0].Status);
        goal = (await service.MutateAsync(tenant, session.Owner.SessionId, id, human, new(GoalCommandKind.Verify, "verify-exact", await Revision(), goal.Revision, goal.PlanRevision, CheckpointId: cp,
            Evidence: new(GoalVerificationKind.EvaluationPass, attempt.AttemptId, run.AgentRunId)))).Goal;
        Assert.Equal(GoalStatus.Completed, goal.Status); Assert.Equal(GoalCheckpointStatus.Verified, goal.Checkpoints.Single().Status);
        Assert.Equal(SessionLifecycleStatus.Active, (await f.Sessions.ReadAsync(tenant, session.Owner.SessionId, human)).LifecycleStatus); Assert.Empty(http.Requests);
    }
}
