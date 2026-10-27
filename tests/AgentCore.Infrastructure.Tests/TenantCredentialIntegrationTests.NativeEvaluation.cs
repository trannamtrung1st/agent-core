using AgentCore.Application.Evaluations;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Evaluations;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Sandbox;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [OpenSandboxFact]
    public Task Native_evaluation_executes_exact_reviewed_script_without_source_claim_or_workspace_mutation() => NativeEvaluation(false);

    [OpenSandboxFact]
    public Task Native_evaluation_executes_exact_reviewed_script_with_sqlite() => NativeEvaluation(true);

    private async Task NativeEvaluation(bool sqlite)
    {
        var clock = new NativeEvaluationClock();
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Native evaluations", "native-evaluations");
        var login = await SandboxHuman(f, owner.OwnerPrincipalId); var human = new TenantSessionInitiator(login.AccountId, login.AuthenticationSessionId, login.PrincipalId); var tenant = owner.TenantId;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        using var http = new ProviderHttpFixture(); await using var services = RuntimeServices(f, http, new(), "Synthetic");
        var options = services.GetRequiredService<OpenSandboxOptions>(); options.Enabled = true; options.AllowLocalImageIds = true;
        options.Endpoint = Environment.GetEnvironmentVariable("AGENTCORE_OSB_PROOF_URL") ?? "http://127.0.0.1:18080";
        options.ApiKey = Environment.GetEnvironmentVariable("AGENTCORE_OSB_PROOF_KEY");
        var root = Path.Combine(Environment.GetEnvironmentVariable("AGENTCORE_OSB_PROOF_MOUNT_ROOT")!, f.Ids.NewId().ToString("N"));
        options.CanonicalWorkspaceRoot = root; options.WorkspaceDockerHostRoot = root;
        var profiles = services.GetRequiredService<TenantSandboxProfileService>();
        var profile = (await profiles.SaveAsync(tenant, null, human, SandboxInput("Native evaluation image") with {
            ImageReference = Environment.GetEnvironmentVariable("AGENTCORE_OSB_PROOF_IMAGE")!, InitializationScript = null
        }, await Revision(), 0)).Profile;
        var verification = await services.GetRequiredService<TenantSandboxVerificationService>().VerifyAsync(tenant, profile.Id, human, await Revision(), profile.Revision);
        Assert.True(verification.Attempt!.Result!.Passed, verification.Attempt.Result.Message);
        var adapter = services.GetRequiredService<OpenSandboxService>();
        var evals = services.GetRequiredService<TenantEvaluationService>(); var defs = services.GetRequiredService<TenantDefinitionService>();
        const string assertion = """
            set -eu
            grep '"status":"Failed"' /home/input.json >/dev/null || exit 31
            test ! -e /home/source-secret || exit 32
            if touch /home/must-not-write 2>/dev/null; then exit 8; fi
            test ! -e /var/run/docker.sock || exit 33
            command -v python >/dev/null || exit 35
            python -I -c "import os; assert all((name + '_API_' + 'KEY') not in os.environ for name in ('OPENAI','OPENROUTER','OPENSANDBOX'))" || exit 36
            python -I -c "import os; assert os.listdir('/home') == ['input.json']" || exit 37
            python -I -c "import socket; s=socket.socket(); s.settimeout(1); assert s.connect_ex(('1.1.1.1',80)) != 0" || exit 38
            test ! -e /working/check.txt
            printf scratch > /working/check.txt
            exit 0
            """;
        var script = await evals.SaveScriptAsync(human.PrincipalId, tenant, f.Ids.NewId(), new("Native assertion", ScriptKind.Sandbox, EvaluationPredicate.OutputPresent, null,
            assertion, [EvaluationHook.Manual, EvaluationHook.AfterRun, EvaluationHook.OnFailure], 0, await Revision(), SandboxProfileId: profile.Id), human);
        var suite = await evals.SaveSuiteAsync(human.PrincipalId, tenant, f.Ids.NewId(), new("Native check", [new("native", "Native assertion", EvaluatorKind.Script, true,
            Script: new(script.ScriptId, script.Version, script.ContentHash))], 0, await Revision()), human);
        var draft = await defs.CreateAsync(human.PrincipalId, tenant, "Native evaluation subject", await Revision(), initiator: human);
        draft = await defs.UpdateAsync(human.PrincipalId, tenant, draft.DraftId, draft.Candidate with { EvaluationBindings = [new(suite.SuiteId, suite.Version, EvaluationHook.AfterRun, EvaluationPolicy.RequirePassForVerifiedOutcome), new(suite.SuiteId, suite.Version, EvaluationHook.OnFailure, EvaluationPolicy.RequirePassForVerifiedOutcome)] }, await Revision(), draft.Revision, initiator: human);
        await defs.PublishAsync(human.PrincipalId, tenant, draft.DraftId, await Revision(), draft.Revision, initiator: human);
        var agent = await f.Agents.CreateFromPublicationAsync(human.PrincipalId, tenant, Guid.Parse(draft.DefinitionId), 1, await Revision(), initiator: human);
        await GrantModelFixtureSpaceRead(f, tenant, human.PrincipalId, agent.PrincipalId);
        var state = (await f.Iam.ReadTenantAsync(tenant))!;
        var role = new AccessRole(f.Ids.NewId(), tenant, "native-evaluator", "Native evaluator", false, false,
            ["evaluations.read", "evaluations.run", "scripts.read", "scripts.execute", "agents.read", "sessions.read", "agent-runs.read", "sandbox.read", "sandbox.execute"]);
        Assert.True(await f.Iam.TrySaveTenantAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 }, Roles = state.Roles.Append(role).ToArray(),
            Assignments = state.Assignments.Append(new(f.Ids.NewId(), tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), human.PrincipalId, f.Clock.GetUtcNow())).ToArray() },
            state.Tenant.Revision, f.Audit(tenant, human.PrincipalId, agent.Instance.InstanceId)));
        var session = await services.GetRequiredService<TenantSessionService>().CreateAsync(tenant, agent.Instance.InstanceId, human, SessionMode.Text, await Revision(), agent.Instance.Revision);
        var receipt = await services.GetRequiredService<TenantRunService>().SubmitTextAsync(tenant, session.Owner.SessionId, human, f.Ids.NewId(), "Native evidence subject", await Revision(), 1);
        var runs = (ITenantRunStore)f.Iam; var runtime = (ITenantRuntimeStore)f.Iam;
        var run = (await runs.FindTenantRunAsync(tenant, receipt.RunId))!;
        run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var sourceGeneration = run.Claim!.Generation;
        run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Fail(run.Revision, f.Clock.GetUtcNow(), run.Claim!.Generation, "native-fixture", "Factual source failure", false, null), f.Ids.NewId());
        var original = AgentWorkspacePhysicalPaths.WorkingDirectory(root, agent.Instance.InstanceId, run.SessionId);
        Directory.CreateDirectory(original); await File.WriteAllTextAsync(Path.Combine(original, "source-secret"), "original workspace sentinel");
        var request = new EvaluationRequest(EvaluationSubjectKind.AgentRun, run.AgentRunId, run.Revision, suite.SuiteId, suite.Version, "native-manual", await Revision());
        var attempt = await evals.RunAsync(human.PrincipalId, tenant, request, human);
        evaluationOutput.WriteLine("Native admission: " + string.Join(";", attempt.Findings.Select(f => f.ReasonCode)));
        Assert.Equal(EvaluationStatus.Pending, attempt.Status);
        var native = Assert.Single(await evals.SandboxEvidenceAsync(human.PrincipalId, tenant, attempt.AttemptId, human));
        var approvalService = services.GetRequiredService<TenantApprovalService>();
        var approval = await approvalService.ReadAsync(tenant, native.ApprovalRequestId, human);
        Assert.Equal(run.Revision, (await runs.FindTenantRunAsync(tenant, run.AgentRunId))!.Revision);
        await approvalService.DecideAsync(tenant, approval.RequestId, human, true, approval.Operation.ActionHash, await Revision(), approval.Revision);
        void ChangeUnrelatedTenantState(int sequence)
        {
            var revision = f.Iam.ReadTenantAsync(tenant).AsTask().GetAwaiter().GetResult()!.Tenant.Revision;
            _ = f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Unrelated settlement " + sequence, ["agents.read"], revision).AsTask().GetAwaiter().GetResult();
        }
        var validations = 0; var commandCount = 0;
        adapter.EvaluationCheckpoint = phase => {
            if (phase == "commandStarted") commandCount++;
            if (phase == "settlementValidated" && ++validations <= 2) ChangeUnrelatedTenantState(validations);
        };
        await approvalService.DispatchPendingAsync();
        await evals.ObserveTerminalsAsync(8, CancellationToken.None);
        adapter.EvaluationCheckpoint = null;
        Assert.Equal(3, validations); Assert.Equal(1, commandCount);
        evaluationOutput.WriteLine("Native settlement: two unrelated tenant commits; three validations; one command");
        var observed = await evals.ReadAsync(human.PrincipalId, tenant, attempt.AttemptId, human);
        var evidence = Assert.Single(await evals.SandboxEvidenceAsync(human.PrincipalId, tenant, attempt.AttemptId, human));
        evaluationOutput.WriteLine($"Native outcome={observed.Outcome}; reason={evidence.ReasonCode}; phase={evidence.Phase}; exit={evidence.ExitCode}; cleanupPending={evidence.CleanupPending}; execution={evidence.ExecutionId}");
        Assert.Equal(EvaluationOutcome.Passed, observed.Outcome); Assert.True(evidence.TerminationConfirmed); Assert.False(evidence.CleanupPending);
        Assert.Equal(script.ContentHash, evidence.Script.ContentHash);
        Assert.Equal(attempt.AttemptId, (await evals.RunAsync(human.PrincipalId, tenant, request, human)).AttemptId);
        Assert.Equal(run.Revision, (await runs.FindTenantRunAsync(tenant, run.AgentRunId))!.Revision);
        Assert.Equal("original workspace sentinel", await File.ReadAllTextAsync(Path.Combine(original, "source-secret")));
        Assert.False(File.Exists(Path.Combine(original, "check.txt"))); Assert.Empty(http.Requests);
        var store = (ITenantEvaluationStore)f.Iam;
        var execution = (await store.FindSandboxEvaluationAsync(tenant, native.ExecutionId))!;
        await Assert.ThrowsAsync<AccessDeniedException>(() => services.GetRequiredService<IEvaluationSandboxExecutor>().ExecuteEvaluationAsync(tenant, native.ExecutionId, execution.DispatchClaim!.Value).AsTask());
        var failing = await evals.SaveScriptAsync(human.PrincipalId, tenant, script.ScriptId, new("Native assertion", ScriptKind.Sandbox, EvaluationPredicate.OutputPresent, null,
            "exit 7", [EvaluationHook.Manual], 1, await Revision(), SandboxProfileId: profile.Id), human);
        var failedSuite = await evals.SaveSuiteAsync(human.PrincipalId, tenant, suite.SuiteId, new("Native check", [new("native", "Native assertion", EvaluatorKind.Script, true,
            Script: new(failing.ScriptId, failing.Version, failing.ContentHash))], 1, await Revision()), human);
        // Both supported automatic boundaries keep trusted provenance and their own exact Review.
        var automatic = (await store.ListAttemptsAsync(tenant, EvaluationSubjectKind.AgentRun, run.AgentRunId, 0, 20)).Items.Where(a => a.Hook is EvaluationHook.AfterRun or EvaluationHook.OnFailure).ToArray();
        Assert.Equal(2, automatic.Length);
        foreach (var auto in automatic)
        {
            Assert.NotNull(auto.LifecycleOrigin);
            var autoEvidence = Assert.Single(await evals.SandboxEvidenceAsync(human.PrincipalId, tenant, auto.AttemptId, human));
            var review = await approvalService.ReadAsync(tenant, autoEvidence.ApprovalRequestId, human);
            await approvalService.DecideAsync(tenant, review.RequestId, human, true, review.Operation.ActionHash, await Revision(), review.Revision);
        }
        await evals.ObserveTerminalsAsync(8, CancellationToken.None);
        Assert.Equal("Verified", (await evals.SummaryAsync(human.PrincipalId, tenant, run, human, CancellationToken.None)).Status);
        Assert.All(automatic, a => Assert.Equal(EvaluationOutcome.Passed, store.FindAttemptAsync(tenant, a.AttemptId).Result!.Outcome));
        // A failed assertion is a factual native Failed, separate from its already Failed source Run.
        var failed = await evals.RunAsync(human.PrincipalId, tenant, request with { SuiteVersion = failedSuite.Version, DedupKey = "native-failing", ExpectedRevision = await Revision() }, human);
        var failureEvidence = Assert.Single(await evals.SandboxEvidenceAsync(human.PrincipalId, tenant, failed.AttemptId, human));
        var failReview = await approvalService.ReadAsync(tenant, failureEvidence.ApprovalRequestId, human);
        await approvalService.DecideAsync(tenant, failReview.RequestId, human, true, failReview.Operation.ActionHash, await Revision(), failReview.Revision);
        await evals.ObserveTerminalsAsync(8, CancellationToken.None);
        var failedObservedEvidence = Assert.Single(await evals.SandboxEvidenceAsync(human.PrincipalId, tenant, failed.AttemptId, human));
        evaluationOutput.WriteLine($"Failing native result: {failedObservedEvidence.ReasonCode}; exit={failedObservedEvidence.ExitCode}; cleanupPending={failedObservedEvidence.CleanupPending}");
        Assert.Equal(EvaluationOutcome.Failed, (await evals.ReadAsync(human.PrincipalId, tenant, failed.AttemptId, human)).Outcome);
        Assert.Equal(7, Assert.Single(await evals.SandboxEvidenceAsync(human.PrincipalId, tenant, failed.AttemptId, human)).ExitCode);
        var missing = await evals.SaveScriptAsync(human.PrincipalId, tenant, script.ScriptId, new("Requires historical output", ScriptKind.Sandbox, EvaluationPredicate.OutputPresent, null,
            "exit 0", [EvaluationHook.Manual], 2, await Revision(), SandboxProfileId: profile.Id, SandboxInput: SandboxEvaluationInput.RunOutput), human);
        var missingSuite = await evals.SaveSuiteAsync(human.PrincipalId, tenant, suite.SuiteId, new("Requires output", [new("native", "Native assertion", EvaluatorKind.Script, true,
            Script: new(missing.ScriptId, missing.Version, missing.ContentHash))], 2, await Revision()), human);
        var absent = await evals.RunAsync(human.PrincipalId, tenant, request with { SuiteVersion = missingSuite.Version, DedupKey = "native-missing", ExpectedRevision = await Revision() }, human);
        Assert.Equal(EvaluationOutcome.Inconclusive, absent.Outcome);
        Assert.Equal("sandbox_evaluation_historical_input_unavailable", Assert.Single(absent.Findings).ReasonCode);
        Assert.Empty(await evals.SandboxEvidenceAsync(human.PrincipalId, tenant, absent.AttemptId, human));
        Assert.Equal(AgentRunStatus.Failed, (await runs.FindTenantRunAsync(tenant, run.AgentRunId))!.Status);
        Assert.Equal(run.Revision, (await runs.FindTenantRunAsync(tenant, run.AgentRunId))!.Revision);
        Assert.Equal("original workspace sentinel", await File.ReadAllTextAsync(Path.Combine(original, "source-secret")));
        // Neither public invocation nor completed source claims grant independent provider dispatch.
        await Assert.ThrowsAsync<AccessDeniedException>(() => evals.RunAsync(human.PrincipalId, f.Ids.NewId(), request, human).AsTask());
        var version = 3;
        async Task<(EvaluationAttempt Attempt, EvaluationSandboxEvidence Evidence)> Prepare(string key, string body, int timeout = 10)
        {
            var next = await evals.SaveScriptAsync(human.PrincipalId, tenant, script.ScriptId, new("Native reliability", ScriptKind.Sandbox, EvaluationPredicate.OutputPresent, null,
                body, [EvaluationHook.Manual], version++, await Revision(), SandboxProfileId: profile.Id, SandboxTimeoutSeconds: timeout), human);
            var nextSuite = await evals.SaveSuiteAsync(human.PrincipalId, tenant, suite.SuiteId, new("Native reliability", [new("native", "Native assertion", EvaluatorKind.Script, true,
                Script: new(next.ScriptId, next.Version, next.ContentHash))], next.Version - 1, await Revision()), human);
            var nextAttempt = await evals.RunAsync(human.PrincipalId, tenant, request with { SuiteVersion = nextSuite.Version, DedupKey = key, ExpectedRevision = await Revision() }, human);
            return (nextAttempt, Assert.Single(await evals.SandboxEvidenceAsync(human.PrincipalId, tenant, nextAttempt.AttemptId, human)));
        }
        async Task Approve(EvaluationSandboxEvidence item)
        {
            var review = await approvalService.ReadAsync(tenant, item.ApprovalRequestId, human);
            await approvalService.DecideAsync(tenant, review.RequestId, human, true, review.Operation.ActionHash, await Revision(), review.Revision);
        }
        var timeoutCase = await Prepare("native-timeout", "sleep 15; exit 0", 1);
        await Assert.ThrowsAsync<AccessDeniedException>(() => adapter.ExecuteEvaluationAsync(tenant, timeoutCase.Evidence.ExecutionId, sourceGeneration).AsTask());
        await Approve(timeoutCase.Evidence);
        await evals.ObserveTerminalsAsync(8, CancellationToken.None);
        var timed = (await store.FindSandboxEvaluationAsync(tenant, timeoutCase.Evidence.ExecutionId))!;
        Assert.Equal(EvaluationOutcome.Inconclusive, timed.Outcome); Assert.True(timed.TerminationConfirmed); Assert.False(timed.CleanupPending);
        evaluationOutput.WriteLine($"Timeout: {timed.ReasonCode}; termination={timed.TerminationConfirmed}");

        var cancelCase = await Prepare("native-cancel", "sleep 15; exit 0", 20); await Approve(cancelCase.Evidence);
        var commandStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.EvaluationCheckpoint = phase => { if (phase == "commandStarted") commandStarted.TrySetResult(); };
        var cancelling = evals.ObserveTerminalsAsync(8, CancellationToken.None).AsTask();
        await commandStarted.Task.WaitAsync(TimeSpan.FromSeconds(50));
        var active = (await store.FindSandboxEvaluationAsync(tenant, cancelCase.Evidence.ExecutionId))!;
        await evals.CancelSandboxAsync(human.PrincipalId, tenant, cancelCase.Attempt.AttemptId, new(active.ExecutionId, active.Revision, await Revision()), human);
        await cancelling.WaitAsync(TimeSpan.FromSeconds(40)); adapter.EvaluationCheckpoint = null;
        var cancelled = (await store.FindSandboxEvaluationAsync(tenant, active.ExecutionId))!;
        Assert.Equal(EvaluationOutcome.Inconclusive, cancelled.Outcome); Assert.Equal("sandbox_evaluation_cancelled", cancelled.ReasonCode); Assert.True(cancelled.TerminationConfirmed);
        evaluationOutput.WriteLine($"Cancellation: {cancelled.ReasonCode}; termination={cancelled.TerminationConfirmed}");

        // Crash after the real command completed, before any result/cleanup commit.
        var crashCase = await Prepare("native-interrupted", "printf once > /working/once; exit 0"); await Approve(crashCase.Evidence);
        var dispatchedCommands = 0;
        adapter.EvaluationCheckpoint = phase => { if (phase == "commandStarted") dispatchedCommands++; if (phase == "commandCompleted") throw new OpenSandboxService.FinalizationCrash(); };
        await Assert.ThrowsAsync<OpenSandboxService.FinalizationCrash>(() => evals.ObserveTerminalsAsync(8, CancellationToken.None).AsTask());
        var interrupted = (await store.FindSandboxEvaluationAsync(tenant, crashCase.Evidence.ExecutionId))!;
        Assert.Equal(EvaluationSandboxPhase.Dispatching, interrupted.Phase); Assert.True(interrupted.CleanupPending); Assert.Null(interrupted.Outcome);
        adapter.EvaluationCheckpoint = null; clock.Advance(TimeSpan.FromSeconds(121));
        // A fresh service owner reads durable state and may retire only, never dispatch again.
        await using (var restarted = RuntimeServices(f, http, new(), "Synthetic"))
        {
            var reopenedOptions = restarted.GetRequiredService<OpenSandboxOptions>();
            reopenedOptions.Enabled = true; reopenedOptions.AllowLocalImageIds = true; reopenedOptions.Endpoint = options.Endpoint; reopenedOptions.ApiKey = options.ApiKey;
            reopenedOptions.CanonicalWorkspaceRoot = root; reopenedOptions.WorkspaceDockerHostRoot = root;
            restarted.GetRequiredService<OpenSandboxService>().EvaluationCheckpoint = _ => throw new InvalidOperationException("Recovery replayed a command");
            await restarted.GetRequiredService<TenantEvaluationService>().ObserveTerminalsAsync(8, CancellationToken.None);
        }
        var recovered = (await store.FindSandboxEvaluationAsync(tenant, interrupted.ExecutionId))!;
        Assert.Equal(EvaluationSandboxPhase.Settled, recovered.Phase); Assert.Equal(EvaluationOutcome.Inconclusive, recovered.Outcome);
        Assert.Equal("sandbox_evaluation_interrupted", recovered.ReasonCode); Assert.True(recovered.TerminationConfirmed); Assert.False(recovered.CleanupPending);
        Assert.Equal(interrupted.DispatchClaim, recovered.DispatchClaim); Assert.Equal(1, dispatchedCommands); Assert.Null(recovered.InputJson);
        evaluationOutput.WriteLine($"Restart recovery: {recovered.ReasonCode}; same claim={recovered.DispatchClaim == interrupted.DispatchClaim}; command count={dispatchedCommands}");

        var parallelCase = await Prepare("native-parallel-a", "set -eu; test ! -e /working/isolated; printf isolated > /working/isolated; sleep 1; test -f /working/isolated");
        var parallelAttempt = await evals.RunAsync(human.PrincipalId, tenant, request with { SuiteVersion = parallelCase.Attempt.SuiteVersion, DedupKey = "native-parallel-b", ExpectedRevision = await Revision() }, human);
        var parallelEvidence = Assert.Single(await evals.SandboxEvidenceAsync(human.PrincipalId, tenant, parallelAttempt.AttemptId, human));
        await Approve(parallelCase.Evidence); await Approve(parallelEvidence);
        var claims = new List<(Guid Id, Guid Claim)>();
        foreach (var item in new[] { parallelCase.Evidence, parallelEvidence })
        {
            var pending = (await store.FindSandboxEvaluationAsync(tenant, item.ExecutionId))!;
            var current = await services.GetRequiredService<EvaluationSandboxAuthority>().RequireAsync(pending, CancellationToken.None, approved: true);
            var claimed = pending with { Phase = EvaluationSandboxPhase.Reserved, Revision = pending.Revision + 1, DispatchClaim = f.Ids.NewId(), DispatchExpiresAt = clock.GetUtcNow().AddMinutes(2) };
            Assert.True(await store.TrySaveSandboxEvaluationAsync(claimed, pending.Revision, expectedSecurityRevision: current.Tenant.Revision));
            claims.Add((claimed.ExecutionId, claimed.DispatchClaim.Value));
        }
        await Task.WhenAll(claims.Select(c => adapter.ExecuteEvaluationAsync(tenant, c.Id, c.Claim).AsTask()));
        await evals.ObserveTerminalsAsync(8, CancellationToken.None);
        var parallelResults = await Task.WhenAll(claims.Select(c => store.FindSandboxEvaluationAsync(tenant, c.Id).AsTask()));
        Assert.All(parallelResults, item => { Assert.Equal(EvaluationOutcome.Passed, item!.Outcome); Assert.True(item.TerminationConfirmed); });
        Assert.Equal(2, parallelResults.Select(item => item!.ProviderId).Distinct().Count());
        evaluationOutput.WriteLine("Concurrent native attempts: both Passed; distinct provider identities; confirmed cleanup");

        var contendedCase = await Prepare("native-settlement-contended", "exit 0"); await Approve(contendedCase.Evidence);
        validations = 0; commandCount = 0;
        adapter.EvaluationCheckpoint = phase => {
            if (phase == "commandStarted") commandCount++;
            if (phase == "settlementValidated") ChangeUnrelatedTenantState(10 + ++validations);
        };
        await evals.ObserveTerminalsAsync(8, CancellationToken.None); adapter.EvaluationCheckpoint = null;
        var contended = (await store.FindSandboxEvaluationAsync(tenant, contendedCase.Evidence.ExecutionId))!;
        Assert.Equal(3, validations); Assert.Equal(1, commandCount); Assert.Equal(EvaluationOutcome.Inconclusive, contended.Outcome);
        Assert.Equal("sandbox_evaluation_settlement_conflict", contended.ReasonCode); Assert.Equal(0, contended.ExitCode); Assert.True(contended.TerminationConfirmed);
        evaluationOutput.WriteLine("Native settlement exhaustion: Inconclusive; one command; confirmed termination");

        // A current executor permission is independent from the completed source Run's old authority.
        var beforeDispatchCase = await Prepare("native-before-dispatch-revoked", "exit 0"); await Approve(beforeDispatchCase.Evidence);
        state = (await f.Iam.ReadTenantAsync(tenant))!;
        var nativeAssignment = state.Assignments.Single(a => a.RoleId == role.RoleId && a.PrincipalId == agent.PrincipalId);
        await f.Access.RevokeAssignmentAsync(human.PrincipalId, tenant, nativeAssignment.AssignmentId, await Revision());
        await evals.ObserveTerminalsAsync(8, CancellationToken.None);
        var beforeDispatch = (await store.FindSandboxEvaluationAsync(tenant, beforeDispatchCase.Evidence.ExecutionId))!;
        Assert.Equal(EvaluationSandboxPhase.Settled, beforeDispatch.Phase); Assert.Equal(EvaluationOutcome.Inconclusive, beforeDispatch.Outcome); Assert.Null(beforeDispatch.ProviderId); Assert.Null(beforeDispatch.DispatchClaim); Assert.Null(beforeDispatch.InputJson);
        evaluationOutput.WriteLine("Revoked before dispatch: Inconclusive; no provider create or dispatch claim");
        var denied = await evals.RunAsync(human.PrincipalId, tenant, request with { DedupKey = "native-denied", ExpectedRevision = await Revision() }, human);
        Assert.Equal(EvaluationOutcome.Inconclusive, denied.Outcome); Assert.Empty(await evals.SandboxEvidenceAsync(human.PrincipalId, tenant, denied.AttemptId, human));
        state = (await f.Iam.ReadTenantAsync(tenant))!;
        Assert.True(await f.Iam.TrySaveTenantAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 }, Assignments = state.Assignments.Append(nativeAssignment).ToArray() }, state.Tenant.Revision, f.Audit(tenant, human.PrincipalId, agent.Instance.InstanceId)));
        var revokedCase = await Prepare("native-settlement-revoked", "exit 0"); await Approve(revokedCase.Evidence);
        adapter.EvaluationCheckpoint = phase => { if (phase == "settlementValidated") f.Identity.SignOutAsync(human.AuthenticationSessionId).AsTask().GetAwaiter().GetResult(); };
        await evals.ObserveTerminalsAsync(8, CancellationToken.None); adapter.EvaluationCheckpoint = null;
        var revoked = (await store.FindSandboxEvaluationAsync(tenant, revokedCase.Evidence.ExecutionId))!;
        Assert.Equal(EvaluationOutcome.Inconclusive, revoked.Outcome); Assert.True(revoked.TerminationConfirmed); Assert.Null(revoked.InputJson);
        var unavailable = (await store.FindAttemptAsync(tenant, revokedCase.Attempt.AttemptId))!;
        Assert.Equal(EvaluationStatus.Indeterminate, unavailable.Status); Assert.Equal(EvaluationOutcome.Inconclusive, unavailable.Outcome);
        Assert.Equal(run.Revision, (await runs.FindTenantRunAsync(tenant, run.AgentRunId))!.Revision);
        Assert.Equal("original workspace sentinel", await File.ReadAllTextAsync(Path.Combine(original, "source-secret")));
        evaluationOutput.WriteLine($"Settlement logout: native={revoked.Outcome}; attempt={unavailable.Status}; termination={revoked.TerminationConfirmed}");
        Directory.Delete(root, true);

    }
    private sealed class NativeEvaluationClock : TimeProvider
    {
        private long offset;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref offset));
        internal void Advance(TimeSpan by) => Interlocked.Add(ref offset, by.Ticks);
    }
}
