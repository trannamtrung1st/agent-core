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
using AgentCore.Domain.Triggers;
using AgentCore.Application.Events;
using AgentCore.Infrastructure.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory]
    [InlineData(false, "manual")] [InlineData(true, "manual")] [InlineData(false, "scheduled")] [InlineData(true, "scheduled")]
    [InlineData(false, "webhook")] [InlineData(true, "webhook")] [InlineData(false, "core")] [InlineData(true, "core")]
    [InlineData(false, "webhook-batch")] [InlineData(true, "webhook-batch")] [InlineData(false, "core-batch")] [InlineData(true, "core-batch")]
    public async Task Workflows_automation_occurrence_owns_typed_start_and_real_verified_worker(bool sqlite, string origin)
    {
        var scheduled = origin == "scheduled"; var eventSource = origin is not ("manual" or "scheduled"); var core = origin.StartsWith("core", StringComparison.Ordinal); var batch = origin.EndsWith("-batch", StringComparison.Ordinal);
        var clock = new NativeEvaluationClock();
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true);
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
                TriggerPolicy = (await f.BuiltIns.GetAsync("general-assistant", 35))!.TriggerPolicy,
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
            [], [WorkflowInvocationKind.AutomationOccurrence], [source.Instance.InstanceId], MaxTotalTaskSteps: 16, MaxTotalTaskSeconds: 120);
        var catalog = Workflows(f, engine); var workflow = Guid.NewGuid();
        await catalog.SaveAsync(tenant, workflow, human, new("Agent-selected process", "", graph, false, 0, await Revision(), "draft"));
        await catalog.PublishAsync(tenant, workflow, human, new(1, await Revision(), "publish"));
        var invocationRole = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Explicit Automation workflow",
            ["automations.run", "webhooks.read", "core-events.read", "workflows.read", "workflows.start", "workflows.monitor", "tasks.read", "tasks.delegate", "evaluations.read", "evaluations.run"], await Revision());
        await f.Access.AssignRoleAsync(human.PrincipalId, tenant, source.PrincipalId, invocationRole.RoleId, AccessScope.ForTenant(tenant), await Revision());
        var version = (await ((ITenantWorkflowCatalogStore)f.Iam).FindWorkflowVersionAsync(tenant, workflow, 1))!;
        var automationService = services.GetRequiredService<TenantAutomationService>();
        var webhooks = services.GetRequiredService<TenantWebhookService>();
        var webhook = eventSource && !core ? await webhooks.CreateAsync(tenant, human, "Workflow source", "workflow.start", await Revision()) : null;
        var dispatch = batch ? new EventDispatch(EventDispatchMode.CoalesceLatest, 60) : new EventDispatch();
        var automation = await automationService.SaveAsync(tenant, source.Instance.InstanceId, null, human,
            new(true, "Pinned process", "Start the saved process", eventSource ? null : new AgentCore.Domain.Triggers.OneShotSchedule(clock.GetUtcNow().AddMinutes(1), "UTC"),
                RequiresTools: false, Events: eventSource ? [new(null, core ? new CoreEventTrigger("instance.config_changed", "true", dispatch) : new EventTrigger(webhook!.View.Source.ResourceId, "true", dispatch))] : null, StartWorkflow: new(workflow, 1, version.Graph.Hash, "{}")), await Revision(), source.Instance.Revision, 0);
        TenantAutomationAdmissionResult accepted;
        if (eventSource)
        {
            if (core)
            {
                source = await f.AgentConfiguration().UpdatePersonaAsync(human.PrincipalId, tenant, source.Instance.InstanceId, source.Instance.Persona with { Name = "Genuine workflow event" }, await Revision(), source.Instance.Revision, source.Instance.PersonaRevision, initiator: human);
                if (batch) source = await f.AgentConfiguration().UpdatePersonaAsync(human.PrincipalId, tenant, source.Instance.InstanceId, source.Instance.Persona with { Name = "Second genuine event" }, await Revision(), source.Instance.Revision, source.Instance.PersonaRevision, initiator: human);
            }
            else
            {
                var sent = await webhooks.AdmitAsync(tenant, webhook!.View.Source.ResourceId, webhook.Token, System.Text.Encoding.UTF8.GetBytes("{\"eventId\":\"workflow-event\",\"data\":{\"bounded\":true}}"));
                Assert.Equal(ExternalEventIngressKind.Admitted, sent.Kind);
                var duplicateReceipt = await webhooks.AdmitAsync(tenant, webhook.View.Source.ResourceId, webhook.Token, System.Text.Encoding.UTF8.GetBytes("{\"eventId\":\"workflow-event\",\"data\":{\"bounded\":true}}"));
                Assert.Equal(sent.EventId, duplicateReceipt.EventId);
                if (batch) await webhooks.AdmitAsync(tenant, webhook.View.Source.ResourceId, webhook.Token, System.Text.Encoding.UTF8.GetBytes("{\"eventId\":\"workflow-event-second\",\"data\":{\"bounded\":true}}"));
            }
            var processor = services.GetRequiredService<TenantEventProcessor>(); var processed = await processor.RunOnceAsync();
            if (batch)
            {
                Assert.Equal(2, processed.Coalesced); Assert.Equal(0, (await ((ITenantWorkflowExecutionStore)f.Iam).ListWorkflowExecutionsAsync(tenant, null, 0, 20)).Total);
                clock.Advance(TimeSpan.FromSeconds(61)); processed = await processor.RunOnceAsync();
            }
            var executions = await ((ITenantWorkflowExecutionStore)f.Iam).ListWorkflowExecutionsAsync(tenant, null, 0, 20);
            if (executions.Total == 0)
            {
                var activity = await ((ITenantEventStore)f.Iam).ListTenantEventActivityAsync(tenant, source.Instance.InstanceId, automation.AutomationId, new(null, null), 0, 20);
                foreach (var item in activity.Items)
                {
                    if (item.Bucket is { } pendingBucket) await automationService.AdmitEventAsync(pendingBucket);
                    else if (item.Delivery.Status == AgentCore.Domain.Events.EventMatchStatus.Matched) await automationService.AdmitEventAsync(item.Delivery);
                }
                Assert.Fail(System.Text.Json.JsonSerializer.Serialize(new { processed, activity }));
            }
            var eventExecution = Assert.Single(executions.Items);
            Assert.Equal(1, processed.Admitted); Assert.Equal(1, eventExecution.Source.Causation!.TriggerDepth); Assert.Equal(automation.AutomationId, Assert.Single(eventExecution.Source.Causation.VisitedAutomationIds));
            var occurrence = (await ((ITenantAutomationStore)f.Iam).FindTenantOccurrenceAsync(tenant, eventExecution.Source.SourceId))!;
            accepted = new(true, occurrence, null, null, WorkflowExecutionId: eventExecution.ExecutionId);
            Assert.Equal(0, (await processor.RunOnceAsync()).Admitted);
        }
        else if (scheduled)
        {
            clock.Advance(TimeSpan.FromMinutes(1)); Assert.Equal(1, await automationService.AdmitDueAsync());
            var execution = Assert.Single((await ((ITenantWorkflowExecutionStore)f.Iam).ListWorkflowExecutionsAsync(tenant, null, 0, 20)).Items);
            var occurrence = (await ((ITenantAutomationStore)f.Iam).FindTenantOccurrenceAsync(tenant, execution.Source.SourceId))!;
            accepted = new(true, occurrence, null, null, WorkflowExecutionId: execution.ExecutionId);
            Assert.Equal(0, await automationService.AdmitDueAsync());
        }
        else accepted = await automationService.RunAsync(tenant, automation.AutomationId, human, await Revision(), source.Instance.Revision, automation.Revision);
        Assert.True(accepted.Created); Assert.Null(accepted.SessionId); Assert.Null(accepted.RunId); Assert.Null(accepted.Occurrence.ModelPin);
        Assert.Equal(accepted.WorkflowExecutionId, accepted.Occurrence.AcceptedWorkflowExecutionId);
        var processes = (ITenantWorkflowExecutionStore)f.Iam;
        var executionBinding = (await processes.FindWorkflowExecutionAsync(tenant, accepted.WorkflowExecutionId!.Value))!;
        Assert.Equal(WorkflowInvocationKind.AutomationOccurrence, executionBinding.Source.Kind);
        Assert.Equal(automation.AutomationId, executionBinding.Source.AutomationId); Assert.Equal(accepted.Occurrence.OccurrenceId, executionBinding.Source.SourceId);
        Assert.Null(executionBinding.Source.SessionId); Assert.Null(executionBinding.Source.RunId); Assert.Null(executionBinding.Source.AccountId); Assert.Null(executionBinding.Source.RootActivationId);
        Assert.Equal(origin == "manual" ? human.PrincipalId : null, executionBinding.Source.OriginalHumanPrincipalId);
        Assert.Equal(source.PrincipalId, executionBinding.Source.PrincipalId);
        Assert.True(await ((ITenantAutomationStore)f.Iam).IsTenantAutomationBusyAsync(tenant, automation.AutomationId));
        Assert.Empty(await ((ITenantRuntimeStore)f.Iam).ListRunnableTenantRunsAsync(clock.GetUtcNow(), 20));
        var initial = TenantEventAdmissionRules.Initial(accepted.Occurrence);
        var next = (await ((ITenantAutomationStore)f.Iam).FindTenantAutomationAsync(tenant, automation.AutomationId))!;
        var duplicate = await ((ITenantWorkflowInvocationStore)f.Iam).AdmitAutomationWorkflowAsync(new(automation, next, initial, executionBinding, source.Instance.Revision, null),
            await Revision(), origin == "manual" ? human : null, Guid.NewGuid());
        Assert.False(duplicate!.Created); Assert.Equal(executionBinding.ExecutionId, duplicate.WorkflowExecutionId);
        var bridge = services.GetRequiredService<TenantWorkflowExecutionService>();
        for (var pass = 0; pass < 12; pass++)
        {
            await bridge.ReconcileAsync(4, default);
            foreach (var task in (await processes.ReadWorkflowTasksAsync(tenant, executionBinding.ExecutionId)).Where(t => t.State == DelegatedTaskState.Queued))
            {
                await ExecuteCooperation(f, services, tenant, task.InitialWorkerRunId!.Value, probe);
                await ((ITenantTaskStore)f.Iam).ReconcileTaskAsync(tenant, task.TaskId, task.Revision, f.Ids.NewId());
            }
        }
        var saved = (await processes.FindWorkflowExecutionAsync(tenant, executionBinding.ExecutionId))!;
        Assert.True(saved.Status == WorkflowExecutionStatus.Completed, $"Stopped at {saved.Status}: {saved.ReasonCode}");
        Assert.Single(await processes.ReadWorkflowTasksAsync(tenant, saved.ExecutionId)); Assert.Empty(http.Requests);
        Assert.False(await ((ITenantAutomationStore)f.Iam).IsTenantAutomationBusyAsync(tenant, automation.AutomationId));
    }
}
