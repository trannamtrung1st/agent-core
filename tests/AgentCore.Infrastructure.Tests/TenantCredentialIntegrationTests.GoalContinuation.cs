using AgentCore.Application.Goals;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Execution;
using AgentCore.Application.Events;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Goals;
using AgentCore.Domain.Security;
using Microsoft.Extensions.DependencyInjection;
namespace AgentCore.Infrastructure.Tests;
public sealed partial class TenantCredentialIntegrationTests
{
    private static async Task<AgentRun> ExecuteGoalRun(Fixture f, ServiceProvider services, Guid tenant, Guid runId, GlobalModelProbe? probe = null)
    {
        var runs = (ITenantRunStore)f.Iam; var storage = (ITenantRuntimeStore)f.Iam; var run = (await runs.FindTenantRunAsync(tenant, runId))!;
        run = await storage.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        Assert.True(await services.GetRequiredService<TenantRunExecutionAuthority>().CurrentDefinitionAsync(run) is not null, string.Join("\n", probe?.Diagnostics.ToArray() ?? []));
        var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation); var output = new CapturingSessionOutput();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using (var runtime = services.GetRequiredService<SessionRuntimeFactory>().CreateTenant((await storage.LoadTenantRuntimeAsync(frame))!, output, frame, storage, runs,
            services.GetRequiredService<TenantRunExecutionAuthority>(), services.GetRequiredService<TenantToolAuthorization>()))
        {
            Assert.True(await runtime.DispatchAgentRunAsync(run.AgentRunId, true, deadline.Token));
            try { await output.WaitForAsync(x => x.Payload is ResponseCompletedOutput or ErrorOutput, deadline.Token); await runtime.WaitUntilIdleAsync(deadline.Token); }
            catch (OperationCanceledException) { throw new Xunit.Sdk.XunitException("Goal Runtime timed out. " + string.Join("\n", probe?.Diagnostics.ToArray() ?? [])); }
        }
        run = (await runs.FindTenantRunAsync(tenant, runId))!; Assert.True(run.Status == AgentRunStatus.Completed, run.Failure?.Code + ":" + run.Failure?.Summary + "\n" + string.Join("\n", probe?.Diagnostics.ToArray() ?? [])); return run;
    }
    [Theory] [InlineData(false, "continue")] [InlineData(true, "continue")]
    [InlineData(false, "pause")] [InlineData(true, "pause")]
    [InlineData(false, "user-input")] [InlineData(true, "user-input")]
    [InlineData(false, "off")] [InlineData(true, "off")]
    [InlineData(false, "queued-pause")] [InlineData(true, "queued-pause")]
    [InlineData(false, "queued-input")] [InlineData(true, "queued-input")]
    public async Task Goals_real_auto_successor_uses_incumbent_Runtime_and_stops_for_human_boundaries(bool sqlite, string scenario)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Goals", "goals");
        var login = await SandboxHuman(f, owner.OwnerPrincipalId); var human = new TenantSessionInitiator(login.AccountId, login.AuthenticationSessionId, login.PrincipalId); var tenant = owner.TenantId;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        using var http = new ProviderHttpFixture(); var probe = new GlobalModelProbe(); await using var services = RuntimeServices(f, http, probe, "Synthetic");
        var agent = await f.Agents.CreateAsync(human.PrincipalId, tenant, "examiner", 1, await Revision()); await GrantModelFixtureSpaceRead(f, tenant, human.PrincipalId, agent.PrincipalId);
        var role = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Goal execution", ["agents.read", "agents.chat", "sessions.read", "agent-runs.read", "agent-runs.create", "goals.read", "goals.manage"], await Revision());
        await f.Access.AssignRoleAsync(human.PrincipalId, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision());
        var session = await f.Sessions.CreateAsync(tenant, agent.Instance.InstanceId, human, SessionMode.Text, await Revision(), agent.Instance.Revision); var sid = session.Owner.SessionId;
        var first = Guid.NewGuid(); var second = Guid.NewGuid(); var id = Guid.NewGuid(); var service = GoalService(f);
        var plan = HumanGoal() with { Checkpoints = [new(first, "First reviewed outcome", "Human accepts first result", true, [], [new(GoalVerificationKind.HumanAcceptance)]),
            new(second, "Second reviewed outcome", "Human accepts second result", true, [first], [new(GoalVerificationKind.HumanAcceptance)])],
            AutoContinuation = scenario == "off" ? new() : new(true, 1, f.Clock.GetUtcNow().AddMinutes(20)) };
        var goal = (await service.MutateAsync(tenant, sid, id, human, new(GoalCommandKind.Create, "create", await Revision(), 0, 0, plan, Confirm: true))).Goal;
        var view = await service.ListAsync(tenant, sid, human); await service.FocusAsync(tenant, sid, id, human, Guid.NewGuid(), view.SecurityRevision, view.SessionRevision, goal.Revision, default);
        view = await service.ListAsync(tenant, sid, human); var request = Guid.NewGuid();
        var receipt = await service.ContinueAsync(tenant, sid, id, human, request, view.SecurityRevision, view.SessionRevision, goal.Revision, goal.PlanRevision, default);
        var source = await ExecuteGoalRun(f, services, tenant, receipt.RunId, probe);
        var replay = await service.ContinueAsync(tenant, sid, id, human, request, view.SecurityRevision, view.SessionRevision, goal.Revision, goal.PlanRevision, default);
        Assert.False(replay.Created); Assert.Equal(receipt.RunId, replay.RunId);
        goal = await service.ReadAsync(tenant, sid, id, human);
        goal = (await service.MutateAsync(tenant, sid, id, human, new(GoalCommandKind.Acceptance, "accept-first", await Revision(), goal.Revision, goal.PlanRevision, CheckpointId: first, Note: "Inspected the first result", Action: "accept"))).Goal;
        if (scenario == "pause") goal = (await service.MutateAsync(tenant, sid, id, human, new(GoalCommandKind.Control, "pause", await Revision(), goal.Revision, goal.PlanRevision, Action: "pause"))).Goal;
        if (scenario == "user-input")
        {
            var saved = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(tenant, sid))!;
            var ordinary = await f.RunService().SubmitTextAsync(tenant, sid, human, Guid.NewGuid(), "A new ordinary request", await Revision(), saved.Snapshot.Revision);
            Assert.Null((await ((ITenantRunStore)f.Iam).FindTenantRunAsync(tenant, ordinary.RunId))!.Admission.GoalWork);
        }
        var continuation = services.GetRequiredService<GoalContinuationService>();
        var count = await continuation.ObserveAsync(8, default);
        if (scenario is "pause" or "user-input" or "off") { Assert.Equal(0, count); Assert.Equal(0, (await service.ReadAsync(tenant, sid, id, human)).AutoRunsUsed); return; }
        Assert.Equal(1, count); Assert.Equal(0, await continuation.ObserveAsync(8, default));
        var children = (await ((ITenantRunStore)f.Iam).ListTenantSessionRunsAsync(tenant, sid, null, 0, 100)).Items.Where(r => r.Admission.Activation.Kind == ActivationKind.GoalContinuation).ToArray();
        var child = Assert.Single(children); Assert.Equal(source.AgentRunId, child.Admission.ExecutionContext!.ParentRunId); Assert.Equal(source.Admission.Activation.RootActivationId, child.Admission.Activation.RootActivationId);
        Assert.Equal(second, child.Admission.GoalWork!.CheckpointId); Assert.Empty(child.Admission.Activation.SourceEntryIds);
        if (scenario.StartsWith("queued-"))
        {
            goal = await service.ReadAsync(tenant, sid, id, human);
            if (scenario == "queued-pause") await service.MutateAsync(tenant, sid, id, human, new(GoalCommandKind.Control, "queued-pause", await Revision(), goal.Revision, goal.PlanRevision, Action: "pause"));
            else
            {
                var current = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(tenant, sid))!;
                await f.RunService().SubmitTextAsync(tenant, sid, human, Guid.NewGuid(), "New human intent before claim", await Revision(), current.Snapshot.Revision);
            }
            var cancelled = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(child.Owner, child.AgentRunId,
                new AgentRunCommand.Claim(child.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
            Assert.Equal(AgentRunStatus.Cancelled, cancelled.Status); Assert.Null(cancelled.Claim);
            Assert.Equal(1, (await service.ReadAsync(tenant, sid, id, human)).AutoRunsUsed); Assert.Empty(http.Requests); return;
        }
        if (sqlite)
        {
            var reopened = new AgentCore.Infrastructure.Security.SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock);
            Assert.Equal(1, (await ((ITenantSessionGoalStore)reopened).FindGoalAsync(tenant, sid, id))!.AutoRunsUsed);
            Assert.Empty(await ((ITenantGoalContinuationStore)reopened).ListGoalContinuationCandidatesAsync(8, default));
            Assert.Single((await ((ITenantRunStore)reopened).ListTenantSessionRunsAsync(tenant, sid, null, 0, 100)).Items, r => r.Admission.Activation.Kind == ActivationKind.GoalContinuation);
        }
        child = await ExecuteGoalRun(f, services, tenant, child.AgentRunId, probe);
        goal = await service.ReadAsync(tenant, sid, id, human); Assert.Equal(1, goal.AutoRunsUsed);
        goal = (await service.MutateAsync(tenant, sid, id, human, new(GoalCommandKind.Acceptance, "accept-second", await Revision(), goal.Revision, goal.PlanRevision, CheckpointId: second, Note: "Inspected second result", Action: "accept"))).Goal;
        Assert.Equal(GoalStatus.Completed, goal.Status); Assert.Equal(0, await continuation.ObserveAsync(8, default)); Assert.Empty(http.Requests);
    }
}
