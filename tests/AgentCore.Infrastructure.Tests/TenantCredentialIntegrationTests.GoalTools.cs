using System.Text.Json;
using AgentCore.Application.Goals;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Goals;
using AgentCore.Domain.Security;
using Microsoft.Extensions.DependencyInjection;
namespace AgentCore.Infrastructure.Tests;
public sealed partial class TenantCredentialIntegrationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Goals_real_claimed_model_tools_propose_report_replay_and_deny_forged_authority(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Model Goals", "model-goals");
        var login = await SandboxHuman(f, owner.OwnerPrincipalId); var human = new TenantSessionInitiator(login.AccountId, login.AuthenticationSessionId, login.PrincipalId); var tenant = owner.TenantId;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        using var http = new ProviderHttpFixture(); await using var services = RuntimeServices(f, http, new(), "Synthetic");
        var definitions = services.GetRequiredService<TenantDefinitionService>();
        var draft = await definitions.CreateAsync(human.PrincipalId, tenant, "Goal author", await Revision(), initiator: human);
        draft = await definitions.UpdateAsync(human.PrincipalId, tenant, draft.DraftId, draft.Candidate with { ExecutionPolicy = new(Capabilities: new("Selected", GoalToolContract.Names)) }, await Revision(), draft.Revision, initiator: human);
        await definitions.PublishAsync(human.PrincipalId, tenant, draft.DraftId, await Revision(), draft.Revision, initiator: human);
        var agent = await f.Agents.CreateFromPublicationAsync(human.PrincipalId, tenant, Guid.Parse(draft.DefinitionId), 1, await Revision(), initiator: human);
        await GrantModelFixtureSpaceRead(f, tenant, human.PrincipalId, agent.PrincipalId);
        var role = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Goal model executor", ["agents.read", "agents.chat", "sessions.read", "agent-runs.read", "goals.read", "goals.manage"], await Revision());
        await f.Access.AssignRoleAsync(human.PrincipalId, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision());
        var session = await f.Sessions.CreateAsync(tenant, agent.Instance.InstanceId, human, SessionMode.Text, await Revision(), agent.Instance.Revision); var sid = session.Owner.SessionId; var goals = GoalService(f); var id = Guid.NewGuid(); var plan = HumanGoal(); var cp = plan.Checkpoints[0].CheckpointId;
        var goal = (await goals.MutateAsync(tenant, sid, id, human, new(GoalCommandKind.Create, "create", await Revision(), 0, 0, plan, Confirm: true))).Goal;
        var view = await goals.ListAsync(tenant, sid, human); await goals.FocusAsync(tenant, sid, id, human, Guid.NewGuid(), view.SecurityRevision, view.SessionRevision, goal.Revision, default);
        view = await goals.ListAsync(tenant, sid, human); var input = await goals.ContinueAsync(tenant, sid, id, human, Guid.NewGuid(), view.SecurityRevision, view.SessionRevision, goal.Revision, goal.PlanRevision, default);
        var runs = (ITenantRunStore)f.Iam; var runtime = (ITenantRuntimeStore)f.Iam; var run = (await runs.FindTenantRunAsync(tenant, input.RunId))!;
        run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var frame = new TenantRuntimeFrame(run.Owner, sid, run.AgentRunId, run.Claim!.Generation); var tools = services.GetRequiredService<GoalToolService>();
        Assert.Equal(GoalToolContract.Names.Order(), (await tools.OfferedAsync(run, default)).Order());
        var read = await tools.ExecuteAsync(frame, new("read", "goals.read", JsonSerializer.Serialize(new { goalId = id })), 20000, default); Assert.Contains(id.ToString("D"), read);
        goal = await goals.ReadAsync(tenant, sid, id, human);
        var progress = new ModelToolCall("progress", "goals.progress", JsonSerializer.Serialize(new { goalId = id, expectedGoalRevision = goal.Revision, expectedPlanRevision = goal.PlanRevision, checkpointId = cp, note = "Actual claimed model reports a blocker", action = "blocked" }));
        await tools.ExecuteAsync(frame, progress, 20000, default); await tools.ExecuteAsync(frame, progress, 20000, default);
        goal = await goals.ReadAsync(tenant, sid, id, human); Assert.Equal(GoalCheckpointStatus.Blocked, goal.Checkpoints[0].Status); Assert.Equal(run.AgentRunId, goal.Checkpoints[0].SourceRunId);
        var emptyPage = JsonDocument.Parse(await tools.ExecuteAsync(frame, new("page", "goals.read", "{\"page\":99}"), 20000, default));
        Assert.Empty(emptyPage.RootElement.GetProperty("items").EnumerateArray()); Assert.False(emptyPage.RootElement.GetProperty("hasMore").GetBoolean());
        var proposedId = Guid.NewGuid(); var proposal = new ModelToolCall("plan", "goals.plan", JsonSerializer.Serialize(new { goalId = proposedId, expectedGoalRevision = 0, expectedPlanRevision = 0, plan }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        await tools.ExecuteAsync(frame, proposal, 20000, default); Assert.Equal(GoalStatus.Proposed, (await goals.ReadAsync(tenant, sid, proposedId, human)).Status);
        await Assert.ThrowsAsync<AccessDeniedException>(async () => await tools.ExecuteAsync(frame with { Generation = f.Ids.NewId() }, progress, 20000, default));
        await Assert.ThrowsAsync<ArgumentException>(async () => await tools.ExecuteAsync(frame, new("accept", "goals.verify", JsonSerializer.Serialize(new { goalId = id, expectedGoalRevision = goal.Revision, expectedPlanRevision = goal.PlanRevision, checkpointId = cp, evidence = new { kind = 2, sourceId = Guid.NewGuid(), runId = run.AgentRunId } })), 20000, default));
        await Assert.ThrowsAsync<AccessDeniedException>(async () => await ((ITenantSessionGoalStore)f.Iam).MutateGoalAsync(new(tenant, sid, id, new(Agent: frame, Tool: "goals.progress"), new(GoalCommandKind.Acceptance, "forged-accept", await Revision(), goal.Revision, goal.PlanRevision, CheckpointId: cp, Note: "Forged", Action: "accept"), Guid.NewGuid(), f.Clock.GetUtcNow())));
        var oldGoal = goal;
        var replacement = await definitions.ForkAsync(human.PrincipalId, tenant, Guid.Parse(draft.DefinitionId), 1, await Revision(), initiator: human);
        replacement = await definitions.UpdateAsync(human.PrincipalId, tenant, replacement.DraftId, replacement.Candidate with { ExecutionPolicy = new(Capabilities: new("Selected", [])) }, await Revision(), replacement.Revision, initiator: human);
        await definitions.PublishAsync(human.PrincipalId, tenant, replacement.DraftId, await Revision(), replacement.Revision, initiator: human);
        await services.GetRequiredService<TenantAgentConfigurationService>().AdoptVersionAsync(human.PrincipalId, tenant, agent.Instance.InstanceId, 2, await Revision(), agent.Instance.Revision, initiator: human);
        Assert.Empty(await tools.OfferedAsync(run, default));
        await Assert.ThrowsAsync<AccessDeniedException>(async () => await tools.ExecuteAsync(frame, progress, 20000, default));
        Assert.Equal(oldGoal.Revision, (await goals.ReadAsync(tenant, sid, id, human)).Revision); Assert.Empty(http.Requests);
    }
}
