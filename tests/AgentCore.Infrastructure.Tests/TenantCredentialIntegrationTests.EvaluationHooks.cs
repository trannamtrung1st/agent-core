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
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var login = await SandboxHuman(f, owner.OwnerPrincipalId); var tenant = owner.TenantId;
        var human = new TenantSessionInitiator(login.AccountId, login.AuthenticationSessionId, login.PrincipalId);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        using var http = new ProviderHttpFixture(); await using var services = RuntimeServices(f, http, new(), "Synthetic");
        var evals = services.GetRequiredService<TenantEvaluationService>(); var defs = services.GetRequiredService<TenantDefinitionService>();
        var checks = new List<EvaluationCheck> { new("run-state", "Saved Run condition", EvaluatorKind.Script, true,
            before ? advisory ? EvaluationPredicate.InputPresent : EvaluationPredicate.OutputPresent : EvaluationPredicate.RunCompleted) };
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
            Assert.Equal(before ? "NotConfigured" : "NotEvaluated", (await evals.SummaryAsync(human.PrincipalId, tenant, terminal, human, deadline.Token)).Status);
            Assert.Equal(terminal.Revision, (await runs.FindTenantRunAsync(tenant, run.AgentRunId))!.Revision); Assert.Empty(http.Requests); return;
        }
        Assert.Equal(advisory ? EvaluationOutcome.Inconclusive : before ? EvaluationOutcome.Failed : EvaluationOutcome.Passed, Assert.Single(attempts).Outcome);
        if (before && !advisory) Assert.Null(terminal.Result);
        else { Assert.Equal("Hello from synthetic.", terminal.Result!.Text); Assert.Equal(before ? "NotConfigured" : "Verified", (await evals.SummaryAsync(human.PrincipalId, tenant, terminal, human, deadline.Token)).Status); }
        lock (durations) foreach (var measurement in durations.Where(d => d.Hook is "BeforeRun" or "AfterRun")) evaluationOutput.WriteLine($"Evaluation latency: store={(sqlite ? "SQLite" : "InMemory")}; hook={measurement.Hook}; ms={measurement.Milliseconds:F3}");
        Assert.Equal(terminal.Revision, (await runs.FindTenantRunAsync(tenant, run.AgentRunId))!.Revision); Assert.Empty(http.Requests);
    }
}
