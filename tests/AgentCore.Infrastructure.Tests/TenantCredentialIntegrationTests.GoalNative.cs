using AgentCore.Application.Evaluations;
using AgentCore.Application.Goals;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Evaluations;
using AgentCore.Domain.Goals;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Sandbox;
using Microsoft.Extensions.DependencyInjection;
namespace AgentCore.Infrastructure.Tests;
public sealed partial class TenantCredentialIntegrationTests
{
    [OpenSandboxFact]
    public Task Goals_native_saved_Sandbox_pass_verifies_SQLite_checkpoint_without_replaying_effects() => GoalNative(false);
    [OpenSandboxFact]
    public Task Goals_native_saved_Sandbox_failure_never_verifies_SQLite_checkpoint() => GoalNative(true);
    [OpenSandboxFact]
    public Task Goals_native_timeout_is_saved_Inconclusive_and_cannot_verify() => GoalNative(false, true);
    private async Task GoalNative(bool fails, bool inconclusive = false)
    {
        var clock = new NativeEvaluationClock(); await using var f = await Fixture.Create(true, clock, builtInAuthority: true);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Goal native evidence", "goal-native-evidence");
        var login = await SandboxHuman(f, owner.OwnerPrincipalId); var human = new TenantSessionInitiator(login.AccountId, login.AuthenticationSessionId, login.PrincipalId); var tenant = owner.TenantId;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        using var http = new ProviderHttpFixture(); var probe = new GlobalModelProbe(); await using var services = RuntimeServices(f, http, probe, "Synthetic");
        var options = services.GetRequiredService<OpenSandboxOptions>(); options.Enabled = true; options.AllowLocalImageIds = true;
        options.Endpoint = Environment.GetEnvironmentVariable("AGENTCORE_OSB_PROOF_URL") ?? "http://127.0.0.1:18080"; options.ApiKey = Environment.GetEnvironmentVariable("AGENTCORE_OSB_PROOF_KEY");
        var root = Path.Combine(Environment.GetEnvironmentVariable("AGENTCORE_OSB_PROOF_MOUNT_ROOT")!, Guid.NewGuid().ToString("N")); options.CanonicalWorkspaceRoot = root; options.WorkspaceDockerHostRoot = root;
        var profile = (await services.GetRequiredService<TenantSandboxProfileService>().SaveAsync(tenant, null, human,
            SandboxInput("Goal native validation") with { ImageReference = Environment.GetEnvironmentVariable("AGENTCORE_OSB_PROOF_IMAGE")!, InitializationScript = null }, await Revision(), 0)).Profile;
        var verified = await services.GetRequiredService<TenantSandboxVerificationService>().VerifyAsync(tenant, profile.Id, human, await Revision(), profile.Revision);
        Assert.True(verified.Attempt!.Result!.Passed, verified.Attempt.Result.Message);
        var evals = services.GetRequiredService<TenantEvaluationService>();
        var script = await evals.SaveScriptAsync(human.PrincipalId, tenant, Guid.NewGuid(), new("Goal immutable Run assertion", ScriptKind.Sandbox, EvaluationPredicate.OutputPresent, null,
            inconclusive ? "sleep 3\nexit 0" : fails ? "exit 7" : "set -eu\ngrep '\"status\":\"Completed\"' /home/input.json >/dev/null\ntest ! -e /var/run/docker.sock\nexit 0", [EvaluationHook.Manual], 0, await Revision(), SandboxProfileId: profile.Id, SandboxTimeoutSeconds: inconclusive ? 1 : 10), human);
        var suite = await evals.SaveSuiteAsync(human.PrincipalId, tenant, Guid.NewGuid(), new("Goal exact native proof", [new("native", "Validate immutable completed Run", EvaluatorKind.Script, true, Script: new(script.ScriptId, script.Version, script.ContentHash))], 0, await Revision()), human);
        var agent = await f.Agents.CreateAsync(human.PrincipalId, tenant, "examiner", 1, await Revision()); await GrantModelFixtureSpaceRead(f, tenant, human.PrincipalId, agent.PrincipalId);
        var role = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Goal native executor", ["agents.read", "agents.chat", "sessions.read", "agent-runs.read", "goals.read", "goals.manage", "evaluations.read", "evaluations.run", "scripts.read", "scripts.execute", "sandbox.read", "sandbox.execute"], await Revision());
        await f.Access.AssignRoleAsync(human.PrincipalId, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision());
        var session = await f.Sessions.CreateAsync(tenant, agent.Instance.InstanceId, human, SessionMode.Text, await Revision(), agent.Instance.Revision); var sid = session.Owner.SessionId; var id = Guid.NewGuid(); var cp = Guid.NewGuid(); var goals = GoalService(f);
        var plan = HumanGoal() with { Checkpoints = [new(cp, "Native validation", "The exact independently reviewed native Script passes", true, [], [new(GoalVerificationKind.EvaluationPass, suite.SuiteId, suite.Version, suite.ContentHash)])] };
        var goal = (await goals.MutateAsync(tenant, sid, id, human, new(GoalCommandKind.Create, "create", await Revision(), 0, 0, plan, Confirm: true))).Goal;
        var view = await goals.ListAsync(tenant, sid, human); await goals.FocusAsync(tenant, sid, id, human, Guid.NewGuid(), view.SecurityRevision, view.SessionRevision, goal.Revision, default);
        view = await goals.ListAsync(tenant, sid, human); var work = await goals.ContinueAsync(tenant, sid, id, human, Guid.NewGuid(), view.SecurityRevision, view.SessionRevision, goal.Revision, goal.PlanRevision, default);
        var run = await ExecuteGoalRun(f, services, tenant, work.RunId, probe);
        var attempt = await evals.RunAsync(human.PrincipalId, tenant, new(EvaluationSubjectKind.AgentRun, run.AgentRunId, run.Revision, suite.SuiteId, suite.Version, "goal-native-once", await Revision()), human);
        Assert.Equal(EvaluationStatus.Pending, attempt.Status);
        goal = await goals.ReadAsync(tenant, sid, id, human);
        var command = new GoalCommand(GoalCommandKind.Verify, "verify-pending", await Revision(), goal.Revision, goal.PlanRevision, CheckpointId: cp, Evidence: new(GoalVerificationKind.EvaluationPass, attempt.AttemptId, run.AgentRunId));
        goal = (await goals.MutateAsync(tenant, sid, id, human, command)).Goal; Assert.Equal(GoalCheckpointStatus.VerificationPending, goal.Checkpoints[0].Status);
        var evidence = Assert.Single(await evals.SandboxEvidenceAsync(human.PrincipalId, tenant, attempt.AttemptId, human));
        var approvals = services.GetRequiredService<TenantApprovalService>(); var approval = await approvals.ReadAsync(tenant, evidence.ApprovalRequestId, human);
        await approvals.DecideAsync(tenant, approval.RequestId, human, true, approval.Operation.ActionHash, await Revision(), approval.Revision);
        var native = services.GetRequiredService<OpenSandboxService>(); var commands = 0; native.EvaluationCheckpoint = phase => { if (phase == "commandStarted") commands++; };
        await approvals.DispatchPendingAsync(); await evals.ObserveTerminalsAsync(8, default);
        var settled = await evals.ReadAsync(human.PrincipalId, tenant, attempt.AttemptId, human); Assert.Equal(inconclusive ? EvaluationOutcome.Inconclusive : fails ? EvaluationOutcome.Failed : EvaluationOutcome.Passed, settled.Outcome);
        command = command with { RequestKey = "verify-settled", ExpectedSecurityRevision = await Revision(), ExpectedGoalRevision = goal.Revision };
        goal = (await goals.MutateAsync(tenant, sid, id, human, command)).Goal;
        Assert.Equal(fails || inconclusive ? GoalStatus.Active : GoalStatus.Completed, goal.Status); Assert.Equal(fails || inconclusive ? GoalCheckpointStatus.VerificationPending : GoalCheckpointStatus.Verified, goal.Checkpoints[0].Status);
        Assert.True((await goals.MutateAsync(tenant, sid, id, human, command)).Replayed); await evals.ObserveTerminalsAsync(8, default);
        Assert.Equal(1, commands); native.EvaluationCheckpoint = null;
        var receipt = Assert.Single(await evals.SandboxEvidenceAsync(human.PrincipalId, tenant, attempt.AttemptId, human)); Assert.True(receipt.TerminationConfirmed); Assert.False(receipt.CleanupPending);
        if (fails)
        {
            var repairedScript = await evals.SaveScriptAsync(human.PrincipalId, tenant, script.ScriptId, new("Goal immutable Run assertion", ScriptKind.Sandbox, EvaluationPredicate.OutputPresent, null,
                "set -eu\ngrep '\"status\":\"Completed\"' /home/input.json >/dev/null\nexit 0", [EvaluationHook.Manual], script.Version, await Revision(), SandboxProfileId: profile.Id), human);
            var repairedSuite = await evals.SaveSuiteAsync(human.PrincipalId, tenant, suite.SuiteId, new("Goal exact native proof", [new("native", "Validate immutable completed Run", EvaluatorKind.Script, true, Script: new(repairedScript.ScriptId, repairedScript.Version, repairedScript.ContentHash))], suite.Version, await Revision()), human);
            var repairedPlan = goal.Plan with { Checkpoints = [goal.Plan.Checkpoints[0] with { Verification = [new(GoalVerificationKind.EvaluationPass, repairedSuite.SuiteId, repairedSuite.Version, repairedSuite.ContentHash)] }] };
            goal = (await goals.MutateAsync(tenant, sid, id, human, new(GoalCommandKind.Plan, "repair-plan", await Revision(), goal.Revision, goal.PlanRevision, Plan: repairedPlan))).Goal;
            Assert.Equal(1, goal.PlanRevision); Assert.NotNull(goal.PendingPlan);
            goal = (await goals.MutateAsync(tenant, sid, id, human, new(GoalCommandKind.PlanDecision, "accept-repair", await Revision(), goal.Revision, goal.PlanRevision, Action: "accept"))).Goal;
            view = await goals.ListAsync(tenant, sid, human);
            var repairedWork = await goals.ContinueAsync(tenant, sid, id, human, Guid.NewGuid(), view.SecurityRevision, view.SessionRevision, goal.Revision, goal.PlanRevision, default);
            var repairedRun = await ExecuteGoalRun(f, services, tenant, repairedWork.RunId, probe);
            var repairedAttempt = await evals.RunAsync(human.PrincipalId, tenant, new(EvaluationSubjectKind.AgentRun, repairedRun.AgentRunId, repairedRun.Revision, repairedSuite.SuiteId, repairedSuite.Version, "goal-native-repaired-once", await Revision()), human);
            var repairedEvidence = Assert.Single(await evals.SandboxEvidenceAsync(human.PrincipalId, tenant, repairedAttempt.AttemptId, human));
            var repairedApproval = await approvals.ReadAsync(tenant, repairedEvidence.ApprovalRequestId, human);
            await approvals.DecideAsync(tenant, repairedApproval.RequestId, human, true, repairedApproval.Operation.ActionHash, await Revision(), repairedApproval.Revision);
            native.EvaluationCheckpoint = phase => { if (phase == "commandStarted") commands++; };
            await approvals.DispatchPendingAsync(); await evals.ObserveTerminalsAsync(8, default);
            Assert.Equal(EvaluationOutcome.Passed, (await evals.ReadAsync(human.PrincipalId, tenant, repairedAttempt.AttemptId, human)).Outcome);
            goal = await goals.ReadAsync(tenant, sid, id, human);
            goal = (await goals.MutateAsync(tenant, sid, id, human, new(GoalCommandKind.Verify, "verify-repaired", await Revision(), goal.Revision, goal.PlanRevision, CheckpointId: cp, Evidence: new(GoalVerificationKind.EvaluationPass, repairedAttempt.AttemptId, repairedRun.AgentRunId)))).Goal;
            Assert.Equal(GoalStatus.Completed, goal.Status); Assert.Equal(2, goal.PlanRevision); Assert.Equal(2, commands);
            await evals.ObserveTerminalsAsync(8, default); Assert.Equal(2, commands); native.EvaluationCheckpoint = null;
            Assert.Equal(EvaluationOutcome.Failed, (await evals.ReadAsync(human.PrincipalId, tenant, attempt.AttemptId, human)).Outcome);
            repairedEvidence = Assert.Single(await evals.SandboxEvidenceAsync(human.PrincipalId, tenant, repairedAttempt.AttemptId, human)); Assert.True(repairedEvidence.TerminationConfirmed); Assert.False(repairedEvidence.CleanupPending);
        }
        Assert.Equal(SessionLifecycleStatus.Active, (await f.Sessions.ReadAsync(tenant, sid, human)).LifecycleStatus); Assert.Empty(http.Requests);
        evaluationOutput.WriteLine($"Goal native outcome={settled.Outcome}; goal={goal.Status}; commands={commands}; attempt={attempt.AttemptId}; scriptHash={script.ContentHash}; cleanupConfirmed={receipt.TerminationConfirmed && !receipt.CleanupPending}");
    }
}
