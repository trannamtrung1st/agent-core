using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Execution;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Application.Workflows;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Evaluations;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;
using AgentCore.Domain.Workflows;
using AgentCore.Infrastructure.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Workflows_actual_claimed_model_tools_bind_source_once_and_keep_original_root_budget(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Agent process", "workflow-agent");
        var human = await SandboxHuman(f, owner.OwnerPrincipalId); var tenant = owner.TenantId;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        await using var vendor = WorkflowRuntimeProvider(f, sqlite);
        if (sqlite) await vendor.MigrateFormalWorkflowRuntimeAsync(); await vendor.PopulateFormalWorkflowActivitiesAsync();
        var engine = vendor.GetRequiredService<IFormalWorkflowRuntime>();
        using var http = new ProviderHttpFixture(); var probe = new GlobalModelProbe();
        await using var services = RuntimeServices(f, http, probe, "Synthetic", formalWorkflowRuntime: engine);
        var definitions = services.GetRequiredService<TenantDefinitionService>();
        async Task<TenantAgent> Agent(string name, string[] capabilities)
        {
            var draft = await definitions.CreateAsync(human.PrincipalId, tenant, name, await Revision(), initiator: human);
            draft = await definitions.UpdateAsync(human.PrincipalId, tenant, draft.DraftId, draft.Candidate with {
                ExecutionBudgets = new(new(144, 900), new(144, 900), new(144, 900)),
                ExecutionPolicy = new(Capabilities: new("Selected", capabilities)) }, await Revision(), draft.Revision, initiator: human);
            await definitions.PublishAsync(human.PrincipalId, tenant, draft.DraftId, await Revision(), draft.Revision, initiator: human);
            return await f.Agents.CreateFromPublicationAsync(human.PrincipalId, tenant, Guid.Parse(draft.DefinitionId), 1, await Revision(), initiator: human);
        }
        var source = await Agent("Actual workflow invoker", WorkflowToolContract.Names.Append("tasks.delegate").ToArray());
        var worker = await Agent("Actual workflow worker", []);
        worker = await f.AgentConfiguration().UpdateCooperationAsync(human.PrincipalId, tenant, worker.Instance.InstanceId, new(AcceptTasks: true), await Revision(), worker.Instance.Revision, initiator: human);
        var baseRole = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Explicit Run authority", ["agents.read", "agents.chat", "sessions.create", "sessions.read", "agent-runs.create", "agent-runs.read", "spaces.read"], await Revision());
        foreach (var agent in new[] { source, worker })
        {
            await GrantModelFixtureSpaceRead(f, tenant, human.PrincipalId, agent.PrincipalId);
            await f.Access.AssignRoleAsync(human.PrincipalId, tenant, agent.PrincipalId, baseRole.RoleId, AccessScope.ForTenant(tenant), await Revision());
        }
        var evals = services.GetRequiredService<AgentCore.Application.Evaluations.TenantEvaluationService>();
        var suite = await evals.SaveSuiteAsync(human.PrincipalId, tenant, Guid.NewGuid(), new("Independent worker proof", [new("done", "Completed Run", EvaluatorKind.Script, true, EvaluationPredicate.RunCompleted)], 0, await Revision()), human);
        var space = (await ((ITenantSpaceStore)f.Iam).FindPrivateSpaceAsync(tenant, worker.Instance.InstanceId))!;
        var graph = new WorkflowGraph(1, [new("start", "Start", WorkflowNodeKind.Start),
            new("prepare", "Required worker", WorkflowNodeKind.AgentTask, WorkflowRequirement.Global, Task: new(worker.Instance.InstanceId, "synthetic-task-worker Prepare the exact bounded result", space.SpaceId)),
            new("verify", "Independent verification", WorkflowNodeKind.Verify, WorkflowRequirement.Global, Verification: new(WorkflowVerificationKind.Evaluation, "prepare", suite.SuiteId, suite.Version, suite.ContentHash)),
            new("done", "Workflow completed", WorkflowNodeKind.EndSuccess), new("failed", "Workflow failed", WorkflowNodeKind.EndFailure)],
            [new("a", "start", "prepare"), new("b", "prepare", "verify", "succeeded"), new("c", "prepare", "failed", "failed"), new("d", "verify", "done", "passed"), new("e", "verify", "failed", "failed")],
            [], [WorkflowInvocationKind.ClaimedAgent], [source.Instance.InstanceId], MaxTotalTaskSteps: 16, MaxTotalTaskSeconds: 120);
        var catalog = Workflows(f, engine); var workflow = Guid.NewGuid();
        await catalog.SaveAsync(tenant, workflow, human, new("Agent-selected process", "", graph, false, 0, await Revision(), "draft"));
        await catalog.PublishAsync(tenant, workflow, human, new(1, await Revision(), "publish"));
        var session = await f.Sessions.CreateAsync(tenant, source.Instance.InstanceId, new(human.AccountId, human.AuthenticationSessionId, human.PrincipalId), SessionMode.Text, await Revision(), source.Instance.Revision);
        var submitted = await services.GetRequiredService<TenantRunService>().SubmitTextAsync(tenant, session.Owner.SessionId,
            new(human.AccountId, human.AuthenticationSessionId, human.PrincipalId), f.Ids.NewId(), $"synthetic-workflow-start:{workflow:D}", await Revision(), 1);
        var tools = services.GetRequiredService<TenantWorkflowToolService>(); var processes = (ITenantWorkflowExecutionStore)f.Iam;
        var actual = await ExecuteCooperation(f, services, tenant, submitted.RunId, probe, beforeDispatch: async run => {
            Assert.Empty(await tools.OfferedAsync(run, default));
            var role = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Explicit workflow invocation", ["workflows.read", "workflows.start", "workflows.monitor", "tasks.read", "tasks.delegate", "evaluations.read", "evaluations.run"], await Revision());
            await f.Access.AssignRoleAsync(human.PrincipalId, tenant, source.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision());
            Assert.Equal(WorkflowToolContract.Names.Order(), (await tools.OfferedAsync(run, default)).Order());
            var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation);
            var call = new ModelToolCall("forged", "workflows.start", JsonSerializer.Serialize(new { workflowId = workflow, version = 1, inputs = new { }, requestKey = "forged" }));
            await Assert.ThrowsAsync<AccessDeniedException>(() => tools.ExecuteAsync(frame with { Generation = Guid.NewGuid() }, call, 4096, default).AsTask());
            await Assert.ThrowsAsync<ArgumentException>(() => tools.ExecuteAsync(frame, call, 10, default).AsTask());
            Assert.Equal(0, (await processes.ListWorkflowExecutionsAsync(tenant, null, 0, 20)).Total);
        });
        Assert.Contains("workflow request recorded", actual.Result!.Text);
        var execution = (await processes.FindWorkflowStartRequestAsync(tenant, "run:" + actual.AgentRunId.ToString("D"), "synthetic-workflow-once"))!;
        AgentRunToolCallCheckpoint.TryRead(actual.Checkpoint, out var messages);
        Assert.True(execution is not null, string.Join("\n", (messages ?? []).Where(m => m.Role == ModelRole.Tool).Select(m => m.Name + ": " + m.Text)));
        Assert.Equal(WorkflowInvocationKind.ClaimedAgent, execution.Source.Kind);
        Assert.Equal(actual.AgentRunId, execution.Source.RunId); Assert.Equal(actual.SessionId, execution.Source.SessionId);
        Assert.Equal(source.PrincipalId, execution.Source.PrincipalId); Assert.Equal(human.PrincipalId, execution.Source.OriginalHumanPrincipalId);
        Assert.Null(execution.Source.AccountId); Assert.Equal(actual.Admission.Activation.RootActivationId, execution.Source.RootActivationId);
        Assert.Equal(actual.AgentRunId, execution.Source.Causation!.RootAgentRunId);
        Assert.Equal(new WorkflowSourceBudget(16, 120), execution.SourceBudget);
        Assert.Equal(1, (await processes.ListWorkflowExecutionsAsync(tenant, null, 0, 20)).Total);
        var allowance = await ((ITenantTaskStore)f.Iam).ReadTaskExecutionAllowanceAsync(actual);
        Assert.Equal(128, allowance!.MaxSteps); Assert.Equal(780, allowance.DurationSeconds);
        var bridge = services.GetRequiredService<TenantWorkflowExecutionService>();
        for (var pass = 0; pass < 12; pass++)
        {
            await bridge.ReconcileAsync(4, default);
            foreach (var task in (await processes.ReadWorkflowTasksAsync(tenant, execution.ExecutionId)).Where(t => t.State == DelegatedTaskState.Queued))
            {
                await ExecuteCooperation(f, services, tenant, task.InitialWorkerRunId!.Value, probe);
                await ((ITenantTaskStore)f.Iam).ReconcileTaskAsync(tenant, task.TaskId, task.Revision, f.Ids.NewId());
            }
        }
        var saved = (await processes.FindWorkflowExecutionAsync(tenant, execution.ExecutionId))!;
        Assert.True(saved.Status == WorkflowExecutionStatus.Completed, $"Stopped at {saved.Status}: {saved.ReasonCode}");
        var workerTask = Assert.Single(await processes.ReadWorkflowTasksAsync(tenant, saved.ExecutionId));
        Assert.Equal(execution.Source.Causation, workerTask.Source.Workflow!.Causation); Assert.Null(workerTask.Source.RunId);
        var fact = Assert.Single((await ((ITenantEventStore)f.Iam).ListTenantCoreEventsAsync(tenant, worker.Instance.InstanceId, "run.completed", 0, 20)).Items).Event;
        Assert.Equal(actual.AgentRunId, fact.RootAgentRunId); Assert.Equal(0, fact.TriggerDepth); Assert.Empty(fact.VisitedAutomationIds!);
        Assert.Equal(allowance, await ((ITenantTaskStore)f.Iam).ReadTaskExecutionAllowanceAsync(actual));
        Assert.Empty(http.Requests);
    }
}
