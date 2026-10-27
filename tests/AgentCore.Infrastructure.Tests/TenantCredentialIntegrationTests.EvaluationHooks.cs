using System.Diagnostics.Metrics;
using Xunit.Abstractions;
using AgentCore.Application.Execution;
using AgentCore.Application.Events;
using AgentCore.Application.Evaluations;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Evaluations;
using AgentCore.Domain.Security;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Infrastructure.Tests;
public sealed partial class TenantCredentialIntegrationTests
{
    private readonly ITestOutputHelper evaluationOutput;
    public TenantCredentialIntegrationTests(ITestOutputHelper output) => evaluationOutput = output;
    [Theory]
    [InlineData(false, true, false, false, false)] [InlineData(true, true, false, false, false)] [InlineData(false, false, false, false, false)] [InlineData(true, false, false, false, false)]
    [InlineData(false, false, true, false, false)] [InlineData(true, false, true, false, false)]
    [InlineData(false, true, false, true, false)] [InlineData(true, true, false, true, false)]
    [InlineData(false, true, false, false, true)] [InlineData(true, true, false, false, true)]
    [InlineData(false, false, false, false, true)] [InlineData(true, false, false, false, true)]
    public async Task Unified_actual_Runtime_blocks_first_generation_or_records_separate_terminal_verification(bool sqlite, bool before, bool governance, bool advisory, bool noEvaluationAuthority)
    {
        var durations = new List<(string Hook, double Milliseconds)>();
        using var listener = new MeterListener { InstrumentPublished = (instrument, receiver) => { if (instrument.Name == "evaluations.duration_ms") receiver.EnableMeasurementEvents(instrument); } };
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) => { var hook = tags.ToArray().FirstOrDefault(t => t.Key == "hook").Value?.ToString() ?? "unknown"; lock (durations) durations.Add((hook, value)); });
        listener.Start();
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var login = await SandboxHuman(f, owner.OwnerPrincipalId); var tenant = owner.TenantId;
        var human = new TenantSessionInitiator(login.AccountId, login.AuthenticationSessionId, login.PrincipalId);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        using var http = new ProviderHttpFixture(); await using var services = RuntimeServices(f, http, new(), "Synthetic");
        var evals = services.GetRequiredService<TenantEvaluationService>(); var defs = services.GetRequiredService<TenantDefinitionService>();
        var checks = new List<EvaluationCheck> { new("run-state", "Saved Run condition", EvaluatorKind.Script, true,
            before ? advisory ? EvaluationPredicate.InputPresent : EvaluationPredicate.OutputPresent : EvaluationPredicate.RunCompleted) };
        if (before && !advisory && !noEvaluationAuthority)
            checks[0] = new("run-state", "Required model precondition", EvaluatorKind.Agent, true, Rubric: "Check whether the saved input is eligible.");
        if (advisory) checks.Add(new("advisory-review", "Optional human assessment", EvaluatorKind.Manual, false));
        var suite = await evals.SaveSuiteAsync(human.PrincipalId, tenant, f.Ids.NewId(), new("Runtime checks", checks, 0, await Revision()), human);
        var draft = await defs.CreateAsync(human.PrincipalId, tenant, "Evaluated agent", await Revision(), initiator: human);
        if (governance) await evals.SaveGovernanceAsync(human.PrincipalId, tenant, [new(suite.SuiteId, 1, EvaluationHook.AfterRun, EvaluationPolicy.RequirePassForVerifiedOutcome)], await Revision(), 0, human);
        var edited = await defs.UpdateAsync(human.PrincipalId, tenant, draft.DraftId, draft.Candidate with { EvaluationBindings = governance ? [] : [new(suite.SuiteId, suite.Version, before ? EvaluationHook.BeforeRun : EvaluationHook.AfterRun,
            before ? noEvaluationAuthority ? EvaluationPolicy.RecordOnly : EvaluationPolicy.RequirePassBeforeRun : EvaluationPolicy.RequirePassForVerifiedOutcome)] }, await Revision(), 1, initiator: human);
        var publication = await defs.PublishAsync(human.PrincipalId, tenant, draft.DraftId, await Revision(), edited.Revision, initiator: human);
        var agent = await f.Agents.CreateFromPublicationAsync(human.PrincipalId, tenant, Guid.Parse(publication.DefinitionId), publication.Version, await Revision(), initiator: human);
        await GrantModelFixtureSpaceRead(f, tenant, human.PrincipalId, agent.PrincipalId);
        var state = (await f.Iam.ReadTenantAsync(tenant))!;
        var role = new AccessRole(f.Ids.NewId(), tenant, "evaluation-runtime", "Evaluation runtime", false, false, noEvaluationAuthority ? ["agents.read", "sessions.read", "agent-runs.read"] : ["evaluations.read", "evaluations.run", "agents.read", "sessions.read", "agent-runs.read"]);
        var assignment = new RoleAssignment(f.Ids.NewId(), tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), human.PrincipalId, f.Clock.GetUtcNow());
        Assert.True(await f.Iam.TrySaveTenantAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 }, Roles = state.Roles.Append(role).ToArray(), Assignments = state.Assignments.Append(assignment).ToArray() }, state.Tenant.Revision, f.Audit(tenant, human.PrincipalId, agent.Instance.InstanceId)));
        var session = await services.GetRequiredService<TenantSessionService>().CreateAsync(tenant, agent.Instance.InstanceId, human, SessionMode.Text, await Revision(), agent.Instance.Revision);
        var receipt = await services.GetRequiredService<TenantRunService>().SubmitTextAsync(tenant, session.Owner.SessionId, human, f.Ids.NewId(), "Hello", await Revision(), 1);
        var runs = (ITenantRunStore)f.Iam; var storage = (ITenantRuntimeStore)f.Iam; var run = (await runs.FindTenantRunAsync(tenant, receipt.RunId))!;
        Assert.Contains(run.Admission.Configuration!.Definition.EvaluationBindings!, b => b.SuiteId == suite.SuiteId && b.Version == suite.Version);
        run = await storage.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation); var output = new CapturingSessionOutput();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var runtime = services.GetRequiredService<SessionRuntimeFactory>().CreateTenant((await storage.LoadTenantRuntimeAsync(frame))!, output, frame, storage, runs,
            services.GetRequiredService<TenantRunExecutionAuthority>(), services.GetRequiredService<TenantToolAuthorization>());
        Assert.True(await runtime.DispatchAgentRunAsync(run.AgentRunId, true, deadline.Token));
        await output.WaitForAsync(x => x.Payload is ResponseCompletedOutput or ErrorOutput, deadline.Token); await runtime.WaitUntilIdleAsync(deadline.Token);
        var terminal = (await runs.FindTenantRunAsync(tenant, run.AgentRunId))!;
        Assert.Equal(before && !advisory && !noEvaluationAuthority ? AgentRunStatus.Failed : AgentRunStatus.Completed, terminal.Status);
        await evals.ObserveTerminalsAsync(8, deadline.Token);
        var attempts = (await ((ITenantEvaluationStore)f.Iam).ListAttemptsAsync(tenant, EvaluationSubjectKind.AgentRun, run.AgentRunId, 0, 100)).Items;
        if (noEvaluationAuthority)
        {
            Assert.Empty(attempts); Assert.Equal("Hello from synthetic.", terminal.Result!.Text);
            await Assert.ThrowsAsync<ArgumentException>(async () => await evals.RunAsync(human.PrincipalId, tenant,
                new(EvaluationSubjectKind.AgentRun, terminal.AgentRunId, terminal.Revision, suite.SuiteId, 1, "forged-hook", await Revision(), EvaluationHook.AfterRun), human).AsTask());
            var manual = await evals.RunAsync(human.PrincipalId, tenant,
                new(EvaluationSubjectKind.AgentRun, terminal.AgentRunId, terminal.Revision, suite.SuiteId, 1, "manual-only", await Revision()), human);
            Assert.Null(manual.LifecycleOrigin);
            Assert.Equal(manual.AttemptId, (await evals.RecoverAsync(human.PrincipalId, tenant, "manual-only", human))!.AttemptId);
            if (!before)
            {
                var deferredSummary = await evals.SummaryAsync(human.PrincipalId, tenant, terminal, human, deadline.Token);
                Assert.Equal("automatic_not_performed", deferredSummary.ObservationReason);
                var retry = deferredSummary.RetryAfter; Assert.NotNull(retry);
                await evals.ObserveTerminalsAsync(8, deadline.Token);
                Assert.Equal(retry, (await evals.SummaryAsync(human.PrincipalId, tenant, terminal, human, deadline.Token)).RetryAfter);
                for (var observation = 0; observation < 4; observation++)
                {
                    clock.Advance(TimeSpan.FromMinutes(6));
                    await evals.ObserveTerminalsAsync(8, deadline.Token);
                }
                Assert.Null((await evals.SummaryAsync(human.PrincipalId, tenant, terminal, human, deadline.Token)).RetryAfter);
                Assert.Equal(1, (await evals.AttemptsAsync(human.PrincipalId, tenant, EvaluationSubjectKind.AgentRun, terminal.AgentRunId, 0, 20, human)).Total);
                // Reconsider denied observations after an actual executor grant, without replaying manual work.
                var readerRole = (await f.Iam.ReadTenantAsync(tenant))!.Roles.Single(r => r.Key == "evaluation-runtime");
                var nextState = (await f.Iam.ReadTenantAsync(tenant))!;
                Assert.True(await f.Iam.TrySaveTenantAsync(nextState with { Tenant = nextState.Tenant with { Revision = nextState.Tenant.Revision + 1 },
                    Roles = nextState.Roles.Select(r => r.RoleId == readerRole.RoleId ? r with { Permissions = r.Permissions.Concat(["evaluations.read", "evaluations.run"]).ToArray() } : r).ToArray() }, nextState.Tenant.Revision, f.Audit(tenant, human.PrincipalId, agent.Instance.InstanceId)));
                clock.Advance(TimeSpan.FromSeconds(31));
                await evals.ObserveTerminalsAsync(8, deadline.Token);
                Assert.Equal("Verified", (await evals.SummaryAsync(human.PrincipalId, tenant, terminal, human, deadline.Token)).Status);
                return;
            }
            Assert.Equal(before ? "NotConfigured" : "NotEvaluated", (await evals.SummaryAsync(human.PrincipalId, tenant, terminal, human, deadline.Token)).Status);
            Assert.Equal(terminal.Revision, (await runs.FindTenantRunAsync(tenant, run.AgentRunId))!.Revision); Assert.Empty(http.Requests); return;
        }
        Assert.Equal(advisory || before ? EvaluationOutcome.Inconclusive : EvaluationOutcome.Passed, Assert.Single(attempts).Outcome);
        if (before && !advisory) Assert.Null(terminal.Result);
        else { Assert.Equal("Hello from synthetic.", terminal.Result!.Text); Assert.Equal(before ? "NotConfigured" : "Verified", (await evals.SummaryAsync(human.PrincipalId, tenant, terminal, human, deadline.Token)).Status); }
        lock (durations) foreach (var measurement in durations.Where(d => d.Hook is "BeforeRun" or "AfterRun")) evaluationOutput.WriteLine($"Evaluation latency: store={(sqlite ? "SQLite" : "InMemory")}; hook={measurement.Hook}; ms={measurement.Milliseconds:F3}");
        Assert.Equal(terminal.Revision, (await runs.FindTenantRunAsync(tenant, run.AgentRunId))!.Revision); Assert.Empty(http.Requests);
    }
    [Theory]
    [InlineData(false, "completed")] [InlineData(true, "completed")]
    [InlineData(false, "failed")] [InlineData(true, "failed")]
    [InlineData(false, "cancelled")] [InlineData(true, "cancelled")]
    public async Task Unified_failure_hook_summary_follows_actual_terminal_state(bool sqlite, string outcome)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var login = await SandboxHuman(f, owner.OwnerPrincipalId); var tenant = owner.TenantId;
        var human = new TenantSessionInitiator(login.AccountId, login.AuthenticationSessionId, login.PrincipalId);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        using var http = new ProviderHttpFixture(); await using var services = RuntimeServices(f, http, new(), "Synthetic");
        var evals = services.GetRequiredService<TenantEvaluationService>(); var defs = services.GetRequiredService<TenantDefinitionService>();
        var suite = await evals.SaveSuiteAsync(human.PrincipalId, tenant, f.Ids.NewId(), new("Failure observations", [new("completed", "Completed", EvaluatorKind.Script, true, EvaluationPredicate.RunCompleted)], 0, await Revision()), human);
        var draft = await defs.CreateAsync(human.PrincipalId, tenant, "Failure observer", await Revision(), initiator: human);
        draft = await defs.UpdateAsync(human.PrincipalId, tenant, draft.DraftId, draft.Candidate with { EvaluationBindings = [new(suite.SuiteId, 1, EvaluationHook.OnFailure)] }, await Revision(), draft.Revision, initiator: human);
        await defs.PublishAsync(human.PrincipalId, tenant, draft.DraftId, await Revision(), draft.Revision, initiator: human);
        var agent = await f.Agents.CreateFromPublicationAsync(human.PrincipalId, tenant, Guid.Parse(draft.DefinitionId), 1, await Revision(), initiator: human);
        await GrantModelFixtureSpaceRead(f, tenant, human.PrincipalId, agent.PrincipalId);
        var state = (await f.Iam.ReadTenantAsync(tenant))!;
        var role = new AccessRole(f.Ids.NewId(), tenant, "observer", "Observer", false, false, ["evaluations.read", "evaluations.run", "agents.read", "sessions.read", "agent-runs.read"]);
        var assignment = new RoleAssignment(f.Ids.NewId(), tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), human.PrincipalId, f.Clock.GetUtcNow());
        Assert.True(await f.Iam.TrySaveTenantAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 }, Roles = state.Roles.Append(role).ToArray(), Assignments = state.Assignments.Append(assignment).ToArray() }, state.Tenant.Revision, f.Audit(tenant, human.PrincipalId, agent.Instance.InstanceId)));
        var session = await services.GetRequiredService<TenantSessionService>().CreateAsync(tenant, agent.Instance.InstanceId, human, SessionMode.Text, await Revision(), agent.Instance.Revision);
        var receipt = await services.GetRequiredService<TenantRunService>().SubmitTextAsync(tenant, session.Owner.SessionId, human, f.Ids.NewId(), "Input", await Revision(), 1);
        var runs = (ITenantRunStore)f.Iam; var storage = (ITenantRuntimeStore)f.Iam; var run = (await runs.FindTenantRunAsync(tenant, receipt.RunId))!;
        run = await storage.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var generation = run.Claim!.Generation;
        if (outcome == "completed")
        {
            var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId, generation); var output = new CapturingSessionOutput();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var runtime = services.GetRequiredService<SessionRuntimeFactory>().CreateTenant((await storage.LoadTenantRuntimeAsync(frame))!, output, frame, storage, runs,
                services.GetRequiredService<TenantRunExecutionAuthority>(), services.GetRequiredService<TenantToolAuthorization>());
            Assert.True(await runtime.DispatchAgentRunAsync(run.AgentRunId, true, deadline.Token));
            await output.WaitForAsync(x => x.Payload is ResponseCompletedOutput, deadline.Token); await runtime.WaitUntilIdleAsync(deadline.Token);
            run = (await runs.FindTenantRunAsync(tenant, run.AgentRunId))!;
        }
        else if (outcome == "failed") run = await storage.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Fail(run.Revision, f.Clock.GetUtcNow(), generation, "fixture-failure", "Factual failure", false, null), f.Ids.NewId());
        else
        {
            run = await storage.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.RequestCancellation(run.Revision, f.Clock.GetUtcNow(), "Fixture cancellation"), f.Ids.NewId());
            run = await storage.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.CommitCancellation(run.Revision, f.Clock.GetUtcNow(), generation, "Fixture cancellation"), f.Ids.NewId());
        }
        await evals.ObserveTerminalsAsync(8, CancellationToken.None);
        var summary = await evals.SummaryAsync(human.PrincipalId, tenant, run, human, CancellationToken.None);
        Assert.Equal(outcome == "completed" ? "NotConfigured" : "Failed", summary.Status);
        Assert.Equal(outcome == "completed" ? 0 : 1, summary.RequiredChecks);
        if (outcome != "completed") Assert.NotNull((await ((ITenantEvaluationStore)f.Iam).FindAttemptAsync(tenant, Assert.Single(summary.AttemptIds)))!.LifecycleOrigin);
        Assert.Equal(run.Status, (await runs.FindTenantRunAsync(tenant, run.AgentRunId))!.Status); Assert.Empty(http.Requests);
        if (outcome == "completed")
        {
            var changed = await services.GetRequiredService<TenantAgentConfigurationService>().PatchSettingsAsync(human.PrincipalId, tenant, agent.Instance.InstanceId, "evaluationPolicy", await Revision(), agent.Instance.Revision,
                new Dictionary<string, System.Text.Json.JsonElement> { ["bindings"] = System.Text.Json.JsonSerializer.SerializeToElement(Array.Empty<string>()) }, [], initiator: human);
            var nextSession = await services.GetRequiredService<TenantSessionService>().CreateAsync(tenant, agent.Instance.InstanceId, human, SessionMode.Text, await Revision(), changed.Instance.Revision);
            var nextReceipt = await services.GetRequiredService<TenantRunService>().SubmitTextAsync(tenant, nextSession.Owner.SessionId, human, f.Ids.NewId(), "New input", await Revision(), 1);
            Assert.Empty((await runs.FindTenantRunAsync(tenant, nextReceipt.RunId))!.Admission.Configuration!.Definition.EvaluationBindings!);
            Assert.Single((await runs.FindTenantRunAsync(tenant, run.AgentRunId))!.Admission.Configuration!.Definition.EvaluationBindings!);
        }
    }

}
