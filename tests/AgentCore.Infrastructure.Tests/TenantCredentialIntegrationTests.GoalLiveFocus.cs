using AgentCore.Application.Goals;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Execution;
using AgentCore.Application.Events;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using Microsoft.Extensions.DependencyInjection;
namespace AgentCore.Infrastructure.Tests;
public sealed partial class TenantCredentialIntegrationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Goals_live_mailbox_focus_survives_Run_writes_without_retargeting_running_work(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Live Goal focus", "live-goal-focus");
        var login = await SandboxHuman(f, owner.OwnerPrincipalId); var human = new TenantSessionInitiator(login.AccountId, login.AuthenticationSessionId, login.PrincipalId); var tenant = owner.TenantId;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        using var http = new ProviderHttpFixture(); var probe = new GlobalModelProbe(); await using var services = RuntimeServices(f, http, probe, "Synthetic");
        var agent = await f.Agents.CreateAsync(human.PrincipalId, tenant, "examiner", 1, await Revision()); await GrantModelFixtureSpaceRead(f, tenant, human.PrincipalId, agent.PrincipalId);
        var role = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Goal executor", ["agents.read", "agents.chat", "sessions.read", "agent-runs.read", "goals.read", "goals.manage"], await Revision());
        await f.Access.AssignRoleAsync(human.PrincipalId, tenant, agent.PrincipalId, role.RoleId, AgentCore.Domain.Security.AccessScope.ForTenant(tenant), await Revision());
        var session = await f.Sessions.CreateAsync(tenant, agent.Instance.InstanceId, human, SessionMode.Text, await Revision(), agent.Instance.Revision); var sid = session.Owner.SessionId; var goals = GoalService(f); var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var one = (await goals.MutateAsync(tenant, sid, first, human, new(GoalCommandKind.Create, "one", await Revision(), 0, 0, HumanGoal("First outcome"), Confirm: true))).Goal;
        var two = (await goals.MutateAsync(tenant, sid, second, human, new(GoalCommandKind.Create, "two", await Revision(), 0, 0, HumanGoal("Second outcome"), Confirm: true))).Goal;
        var view = await goals.ListAsync(tenant, sid, human); await goals.FocusAsync(tenant, sid, first, human, Guid.NewGuid(), view.SecurityRevision, view.SessionRevision, one.Revision, default);
        view = await goals.ListAsync(tenant, sid, human); var input = await goals.ContinueAsync(tenant, sid, first, human, Guid.NewGuid(), view.SecurityRevision, view.SessionRevision, one.Revision, one.PlanRevision, default);
        var runs = (ITenantRunStore)f.Iam; var store = (ITenantRuntimeStore)f.Iam; var run = (await runs.FindTenantRunAsync(tenant, input.RunId))!;
        run = await store.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var frame = new TenantRuntimeFrame(run.Owner, sid, run.AgentRunId, run.Claim!.Generation); var output = new CapturingSessionOutput(); var loaded = (await store.LoadTenantRuntimeAsync(frame))!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using (var runtime = services.GetRequiredService<SessionRuntimeFactory>().CreateTenant(loaded, output, frame, store, runs,
            services.GetRequiredService<TenantRunExecutionAuthority>(), services.GetRequiredService<TenantToolAuthorization>()))
        {
            Assert.True(await runtime.DispatchAgentRunAsync(run.AgentRunId, true, deadline.Token));
            var result = await runtime.FocusGoalAsync(new(session.Owner, human, second, Guid.NewGuid(), await Revision(), loaded.Revision, two.Revision), deadline.Token);
            Assert.NotNull(result); Assert.Equal(second, result.GoalId);
            await output.WaitForAsync(x => x.Payload is ResponseCompletedOutput or ErrorOutput, deadline.Token); await runtime.WaitUntilIdleAsync(deadline.Token);
        }
        var saved = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(tenant, sid))!;
        Assert.Equal(second, saved.Snapshot.FocusedGoalId); Assert.Equal(2, saved.Snapshot.GoalFocusRevision);
        run = (await runs.FindTenantRunAsync(tenant, run.AgentRunId))!; Assert.Equal(AgentRunStatus.Completed, run.Status); Assert.Equal(first, run.Admission.GoalWork!.GoalId);
        var ordinary = await f.RunService().SubmitTextAsync(tenant, sid, human, Guid.NewGuid(), "New unbound human intent", await Revision(), saved.Snapshot.Revision);
        Assert.Null((await runs.FindTenantRunAsync(tenant, ordinary.RunId))!.Admission.GoalWork); Assert.Empty(http.Requests);
    }
}
