using System.Text.Json;
using System.Runtime.CompilerServices;
using AgentCore.Application.Ports;
using AgentCore.Application.Execution;
using AgentCore.Infrastructure.Security;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Application.Workflows;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Evaluations;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;
using AgentCore.Domain.Workflows;
using AgentCore.Infrastructure.Providers.Synthetic;
using AgentCore.Infrastructure.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public Task Workflows_worker_protected_operation_requires_actual_Run_approval_and_rejection_never_passes(bool sqlite) => WorkflowProtectedOperation(sqlite, false);
    [Theory] [InlineData(false)] [InlineData(true)]
    public Task Workflows_unknown_worker_effect_survives_cancellation_without_replay_or_retirement(bool sqlite) => WorkflowProtectedOperation(sqlite, true);
    private async Task WorkflowProtectedOperation(bool sqlite, bool unknown)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Protected workflow", "protected-workflow");
        var auth = await SandboxHuman(f, owner.OwnerPrincipalId);
        var human = new TenantSessionInitiator(auth.AccountId, auth.AuthenticationSessionId, auth.PrincipalId); var tenant = owner.TenantId;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        await using var vendor = WorkflowRuntimeProvider(f, sqlite);
        if (sqlite) await vendor.MigrateFormalWorkflowRuntimeAsync(); await vendor.PopulateFormalWorkflowActivitiesAsync();
        var engine = vendor.GetRequiredService<IFormalWorkflowRuntime>();
        using var http = new ProviderHttpFixture(); var probe = new GlobalModelProbe();
        await using var services = RuntimeServices(f, http, probe, "Synthetic", formalWorkflowRuntime: engine, syntheticModel: new WorkflowProtectedModel());
        var definitions = services.GetRequiredService<TenantDefinitionService>();
        async Task<TenantAgent> Agent(string name, string[] capabilities)
        {
            var draft = await definitions.CreateAsync(human.PrincipalId, tenant, name, await Revision(), initiator: human);
            draft = await definitions.UpdateAsync(human.PrincipalId, tenant, draft.DraftId, draft.Candidate with {
                ExecutionPolicy = new(Capabilities: new("Selected", capabilities)) }, await Revision(), draft.Revision, initiator: human);
            await definitions.PublishAsync(human.PrincipalId, tenant, draft.DraftId, await Revision(), draft.Revision, initiator: human);
            return await f.Agents.CreateFromPublicationAsync(human.PrincipalId, tenant, Guid.Parse(draft.DefinitionId), 1, await Revision(), initiator: human);
        }
        var coordinator = await Agent("Process coordinator", ["tasks.delegate"]);
        var worker = await Agent("Protected worker", [ToolCatalog.WorkspaceWrite]);
        worker = await f.AgentConfiguration().UpdateCooperationAsync(human.PrincipalId, tenant, worker.Instance.InstanceId, new(AcceptTasks: true), await Revision(), worker.Instance.Revision, initiator: human);
        var role = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Worker explicit authority", ["sessions.create", "sessions.read", "agent-runs.create", "agent-runs.read", "agents.read", "spaces.read", "workspaces.read", "workspaces.write"], await Revision());
        await f.Access.AssignRoleAsync(human.PrincipalId, tenant, worker.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision());
        role = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Process explicit authority", ["tasks.delegate", "agents.read", "sessions.read", "agent-runs.read", "spaces.read", "evaluations.read", "evaluations.run"], await Revision());
        await f.Access.AssignRoleAsync(human.PrincipalId, tenant, coordinator.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision());
        await services.GetRequiredService<TenantOperationPolicyService>().SaveAsync(tenant, human, "workspaces.write", new(OperationApprovalMode.Always, false), await Revision(), 0);
        var evaluations = services.GetRequiredService<AgentCore.Application.Evaluations.TenantEvaluationService>();
        var suite = await evaluations.SaveSuiteAsync(human.PrincipalId, tenant, Guid.NewGuid(), new("Independent proof", [new("done", "Completed worker", EvaluatorKind.Script, true, EvaluationPredicate.RunCompleted)], 0, await Revision()), human);
        var space = (await ((ITenantSpaceStore)f.Iam).FindPrivateSpaceAsync(tenant, worker.Instance.InstanceId))!;
        var graph = new WorkflowGraph(1, [new("start", "Start", WorkflowNodeKind.Start),
            new("task", "Protected work", WorkflowNodeKind.AgentTask, WorkflowRequirement.Global, Task: new(worker.Instance.InstanceId, "Attempt the exact protected fixture operation", space.SpaceId)),
            new("verify", "Independent proof", WorkflowNodeKind.Verify, WorkflowRequirement.Global, Verification: new(WorkflowVerificationKind.Evaluation, "task", suite.SuiteId, suite.Version, suite.ContentHash)),
            new("review", "Process-only sign-off", WorkflowNodeKind.HumanDecision, WorkflowRequirement.Global, Decision: new(human.PrincipalId, "Accept recorded process outcome?")),
            new("done", "Completed", WorkflowNodeKind.EndSuccess), new("failed", "Failed", WorkflowNodeKind.EndFailure)],
            [new("a", "start", "task"), new("b", "task", "verify", "succeeded"), new("c", "task", "failed", "failed"), new("d", "verify", "review", "passed"), new("e", "verify", "failed", "failed"),
                new("f", "review", "done", "approve"), new("g", "review", "failed", "reject")], [], [WorkflowInvocationKind.HumanManual], [coordinator.Instance.InstanceId]);
        var catalog = Workflows(f, engine); var id = Guid.NewGuid();
        await catalog.SaveAsync(tenant, id, human, new("Protected workflow", "", graph, false, 0, await Revision(), "draft"));
        await catalog.PublishAsync(tenant, id, human, new(1, await Revision(), "publish"));
        var bridge = new TenantWorkflowExecutionService(f.Iam, (ITenantWorkflowCatalogStore)f.Iam, (ITenantWorkflowExecutionStore)f.Iam,
            (ITenantWorkflowTaskStore)f.Iam, (ITenantTaskStore)f.Iam, (ITenantSessionStore)f.Iam, (ITenantRunStore)f.Iam, (ITenantSpaceStore)f.Iam,
            catalog, f.RunConfiguration(), new SyntheticTenantModelCatalogReader(f.Iam), engine, f.Identity, f.Authorizer, f.Ids, f.Clock, evaluations: evaluations);
        using var input = JsonDocument.Parse("{}");
        var execution = await bridge.StartHumanAsync(tenant, id, human, new(1, input.RootElement, "start", await Revision(), CoordinatorInstanceId: coordinator.Instance.InstanceId));
        var processes = (ITenantWorkflowExecutionStore)f.Iam;
        for (var pass = 0; pass < 4; pass++) await bridge.ReconcileAsync(4, default);
        var task = Assert.Single(await processes.ReadWorkflowTasksAsync(tenant, execution.ExecutionId));
        if (unknown)
        {
            // Fault injection after persisted dispatch intent, before a confirmed effect result. No external call is made.
            var runtime = (ITenantRuntimeStore)f.Iam; var runs = (ITenantRunStore)f.Iam;
            var interrupted = (await runs.FindTenantRunAsync(tenant, task.InitialWorkerRunId!.Value))!;
            async Task Apply(AgentRunCommand command) => interrupted = await runtime.ApplyTenantRuntimeCommandAsync(interrupted.Owner, interrupted.AgentRunId, command, Guid.NewGuid());
            await Apply(new AgentRunCommand.Claim(interrupted.Revision, f.Clock.GetUtcNow(), Guid.NewGuid(), f.Clock.GetUtcNow().AddMinutes(5)));
            var checkpoint = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [new("uncertain-workflow-call", ToolCatalog.WorkspaceWrite, "{\"path\":\"/working/uncertain.txt\",\"content\":\"retain this uncertain source\"}")])]), 1, 32, 1000);
            await Apply(new AgentRunCommand.Checkpoint(interrupted.Revision, f.Clock.GetUtcNow(), interrupted.Claim!.Generation, checkpoint, "retain this uncertain source"));
            await Apply(new AgentRunCommand.MarkSideEffect(interrupted.Revision, f.Clock.GetUtcNow(), interrupted.Claim!.Generation, AgentRunSideEffectDisposition.Prepared, "uncertain-workflow-call", new string('a', 64)));
            await Apply(new AgentRunCommand.MarkSideEffect(interrupted.Revision, f.Clock.GetUtcNow(), interrupted.Claim!.Generation, AgentRunSideEffectDisposition.InFlight, "uncertain-workflow-call", new string('a', 64)));
            await Apply(new AgentRunCommand.RequestCancellation(interrupted.Revision, f.Clock.GetUtcNow(), null));
            await Apply(new AgentRunCommand.CommitCancellation(interrupted.Revision, f.Clock.GetUtcNow(), interrupted.Claim!.Generation, null));
            Assert.True(interrupted.RequiresEffectReconciliation);
            await ((ITenantTaskStore)f.Iam).ReconcileTaskAsync(tenant, task.TaskId, task.Revision, Guid.NewGuid());
            await bridge.ReconcileAsync(4, default);
            var attention = (await processes.FindWorkflowExecutionAsync(tenant, execution.ExecutionId))!;
            Assert.Equal(WorkflowExecutionStatus.NeedsAttention, attention.Status); Assert.Equal("workflow_unknown_effect", attention.ReasonCode);
            await bridge.CancelHumanAsync(new(tenant, execution.ExecutionId, human, attention.Revision, await Revision(), "cancel-uncertain", Guid.NewGuid()));
            for (var pass = 0; pass < 3; pass++) await bridge.ReconcileAsync(4, default);
            var retained = (await processes.FindWorkflowExecutionAsync(tenant, execution.ExecutionId))!;
            Assert.True(retained.CancellationRequested); Assert.Equal(WorkflowExecutionStatus.NeedsAttention, retained.Status);
            Assert.Empty((await engine.ReadAsync(tenant, execution.ExecutionId))!.Bookmarks);
            Assert.Single(await processes.ReadWorkflowTasksAsync(tenant, execution.ExecutionId));
            Assert.True((await runs.FindTenantRunAsync(tenant, interrupted.AgentRunId))!.RequiresEffectReconciliation);
            Assert.Null((await ((ITenantTaskStore)f.Iam).FindTaskAsync(tenant, task.TaskId))!.ContentRetiredAt);
            if (sqlite) Assert.Equal(retained, await ((ITenantWorkflowExecutionStore)new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock)).FindWorkflowExecutionAsync(tenant, execution.ExecutionId));
            return;
        }
        var run = await ExecuteCooperation(f, services, tenant, task.InitialWorkerRunId!.Value, probe, AgentRunStatus.WaitingForApproval);
        await bridge.ReconcileAsync(4, default);
        Assert.Equal(WorkflowExecutionStatus.WaitingForOperationApproval, (await processes.FindWorkflowExecutionAsync(tenant, execution.ExecutionId))!.Status);
        var request = (await ((ITenantApprovalStore)f.Iam).FindApprovalAsync(tenant, run.ApprovalRequestId!.Value))!;
        Assert.Equal(run.AgentRunId, request.RunId); Assert.Equal(run.SessionId, request.SessionId);
        Assert.Equal(worker.PrincipalId, request.ExecutorPrincipalId); Assert.Equal(human.PrincipalId, request.OriginalInitiatorPrincipalId);
        var step = (await processes.ReadWorkflowStepsAsync(tenant, execution.ExecutionId)).Single(s => s.NodeId == "task");
        Assert.False(step.ProofAccepted); Assert.False((await bridge.ReadHumanAsync(tenant, execution.ExecutionId, human)).Steps.Single(s => s.NodeId == "review").CanDecide);
        var approvals = services.GetRequiredService<TenantApprovalService>();
        await approvals.DecideAsync(tenant, request.RequestId, human, false, request.Operation.ActionHash, await Revision(), request.Revision);
        await approvals.DispatchPendingAsync();
        var original = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(tenant, run.AgentRunId))!;
        if (original.Result?.ContinuationRunId is { } successor) await ExecuteCooperation(f, services, tenant, successor, probe);
        task = (await ((ITenantTaskStore)f.Iam).FindTaskAsync(tenant, task.TaskId))!;
        await ((ITenantTaskStore)f.Iam).ReconcileTaskAsync(tenant, task.TaskId, task.Revision, f.Ids.NewId());
        for (var pass = 0; pass < 6; pass++) await bridge.ReconcileAsync(4, default);
        var denied = (await processes.FindWorkflowExecutionAsync(tenant, execution.ExecutionId))!;
        Assert.NotEqual(WorkflowExecutionStatus.Completed, denied.Status);
        if (denied.Status == WorkflowExecutionStatus.NeedsAttention) Assert.Equal("workflow_worker_needs_attention", denied.ReasonCode);
        if (!WorkflowExecutionRules.Terminal(denied.Status))
        {
            await bridge.CancelHumanAsync(new(tenant, execution.ExecutionId, human, denied.Revision, await Revision(), "cancel-incomplete-worker", Guid.NewGuid()));
            for (var pass = 0; pass < 3; pass++) await bridge.ReconcileAsync(4, default);
            Assert.Equal(WorkflowExecutionStatus.Cancelled, (await processes.FindWorkflowExecutionAsync(tenant, execution.ExecutionId))!.Status);
        }
        Assert.False((await bridge.ReadHumanAsync(tenant, execution.ExecutionId, human)).Steps.Single(s => s.NodeId == "task").Proven);
        Assert.Empty(Directory.GetFiles(f.Root, "workflow-rejected-effect.txt", SearchOption.AllDirectories)); Assert.Empty(http.Requests);
    }
    private sealed class WorkflowProtectedModel : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new ScriptedLanguageModel().Capabilities;
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield(); cancellationToken.ThrowIfCancellationRequested();
            var returned = request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization);
            var args = returned ? JsonSerializer.Serialize(new { outcome = "NeedsAttention", attentionRequired = true, summary = "The protected operation was rejected; no effect was performed." })
                : JsonSerializer.Serialize(new { operation = "mutate_workspace", mutation = new { tool = ToolCatalog.WorkspaceWrite, arguments = new { path = "/working/workflow-rejected-effect.txt", content = "Must not be written after rejection" } } });
            yield return new ModelToolCallEvent(new(returned ? "finish-protected-worker" : "protected-worker-review", returned ? ToolCatalog.WorkComplete : ToolCatalog.RequestAuthorization, args));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }
}
