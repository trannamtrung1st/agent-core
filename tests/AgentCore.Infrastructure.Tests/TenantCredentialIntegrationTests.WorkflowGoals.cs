using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Workflows;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Goals;
using AgentCore.Domain.Workflows;
using AgentCore.Infrastructure.Providers.Synthetic;
using AgentCore.Infrastructure.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Workflows_Goal_checkpoint_and_final_acceptance_remain_with_the_existing_Goal_owner(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Goal process", "workflow-goal");
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var human = new HumanSessionReference(owner.AccountId, login.SessionId, owner.OwnerPrincipalId);
        var initiator = new TenantSessionInitiator(human.AccountId, human.AuthenticationSessionId, human.PrincipalId); var tenant = owner.TenantId;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var agent = await f.Agents.CreateAsync(human.PrincipalId, tenant, "examiner", 1, await Revision());
        var session = await f.Sessions.CreateAsync(tenant, agent.Instance.InstanceId, initiator, SessionMode.Text, await Revision(), agent.Instance.Revision);
        var sid = session.Owner.SessionId; var goalId = Guid.NewGuid(); var plan = HumanGoal(final: true); var goals = GoalService(f);
        var goal = (await goals.MutateAsync(tenant, sid, goalId, initiator, new(GoalCommandKind.Create, "create", await Revision(), 0, 0, plan, Confirm: true))).Goal;
        var graph = new WorkflowGraph(1, [new("start", "Start", WorkflowNodeKind.Start),
            new("verify", "Observe verified Goal checkpoint", WorkflowNodeKind.Verify, WorkflowRequirement.Global,
                Verification: new(WorkflowVerificationKind.GoalCheckpoint, SessionId: sid, GoalId: goalId, CheckpointId: plan.Checkpoints[0].CheckpointId, PlanRevision: goal.PlanRevision)),
            new("done", "Workflow completed", WorkflowNodeKind.EndSuccess), new("failed", "Verification failed", WorkflowNodeKind.EndFailure)],
            [new("a", "start", "verify"), new("b", "verify", "done", "passed"), new("c", "verify", "failed", "failed")], [], [WorkflowInvocationKind.HumanManual], [],
            SuccessGoal: new(sid, goalId, goal.PlanRevision));
        await using var provider = WorkflowRuntimeProvider(f, sqlite);
        if (sqlite) await provider.MigrateFormalWorkflowRuntimeAsync(); await provider.PopulateFormalWorkflowActivitiesAsync();
        var engine = provider.GetRequiredService<IFormalWorkflowRuntime>(); var catalog = Workflows(f, engine); var id = Guid.NewGuid();
        await catalog.SaveAsync(tenant, id, human, new("Goal verification process", "", graph, false, 0, await Revision(), "draft"));
        await catalog.PublishAsync(tenant, id, human, new(1, await Revision(), "publish"));
        var bridge = new TenantWorkflowExecutionService(f.Iam, (ITenantWorkflowCatalogStore)f.Iam, (ITenantWorkflowExecutionStore)f.Iam,
            (ITenantWorkflowTaskStore)f.Iam, (ITenantTaskStore)f.Iam, (ITenantSessionStore)f.Iam, (ITenantRunStore)f.Iam, (ITenantSpaceStore)f.Iam,
            catalog, f.RunConfiguration(), new SyntheticTenantModelCatalogReader(f.Iam), engine, f.Identity, f.Authorizer, f.Ids, f.Clock);
        using var inputs = JsonDocument.Parse("{}");
        var execution = await bridge.StartHumanAsync(tenant, id, human, new(1, inputs.RootElement, "start", await Revision(), SourceSessionId: sid));
        for (var i = 0; i < 4; i++) await bridge.ReconcileAsync(4, default);
        Assert.Equal(WorkflowExecutionStatus.WaitingForVerification.ToString(), (await bridge.ReadHumanAsync(tenant, execution.ExecutionId, human)).Status);
        Assert.Equal(GoalRules.Hash(goal), GoalRules.Hash(await goals.ReadAsync(tenant, sid, goalId, initiator)));
        goal = (await goals.MutateAsync(tenant, sid, goalId, initiator, new(GoalCommandKind.Acceptance, "checkpoint", await Revision(), goal.Revision, goal.PlanRevision,
            CheckpointId: plan.Checkpoints[0].CheckpointId, Note: "Reviewed the checkpoint", Action: "accept"))).Goal;
        Assert.Equal(GoalStatus.Active, goal.Status);
        for (var i = 0; i < 5; i++) await bridge.ReconcileAsync(4, default);
        var waiting = await bridge.ReadHumanAsync(tenant, execution.ExecutionId, human);
        Assert.Equal("WaitingForVerification", waiting.Status); Assert.Equal("workflow_goal_verification_pending", waiting.ReasonCode);
        Assert.Equal(GoalRules.Hash(goal), GoalRules.Hash(await goals.ReadAsync(tenant, sid, goalId, initiator)));
        goal = (await goals.MutateAsync(tenant, sid, goalId, initiator, new(GoalCommandKind.Acceptance, "overall", await Revision(), goal.Revision, goal.PlanRevision,
            Note: "Accepted all final success criteria", Action: "accept"))).Goal;
        Assert.Equal(GoalStatus.Completed, goal.Status);
        for (var i = 0; i < 5; i++) await bridge.ReconcileAsync(4, default);
        Assert.Equal("Completed", (await bridge.ReadHumanAsync(tenant, execution.ExecutionId, human)).Status);
        var current = await f.Sessions.ReadAsync(tenant, sid, initiator);
        await f.Sessions.ChangeAsync(tenant, initiator, new(sid, current.Revision, TenantSessionAction.Delete), await Revision());
        await Assert.ThrowsAsync<AccessDeniedException>(() => bridge.ReadHumanAsync(tenant, execution.ExecutionId, human).AsTask());
        Assert.Equal(0, (await bridge.ListHumanAsync(tenant, human, 0, 20)).Total);
    }
}
