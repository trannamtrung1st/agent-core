using System.Text.Json;
using AgentCore.Application.Models;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Models;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;
using AgentCore.Domain.Workflows;

namespace AgentCore.Application.Workflows;

public sealed record WorkflowStart(int Version, JsonElement Inputs, string RequestKey, long ExpectedSecurityRevision,
    Guid? CoordinatorInstanceId = null, Guid? SourceSessionId = null, DateTimeOffset? DeadlineAt = null);

/// <summary>Durable source admission and bounded recovery. Elsa alone selects the next graph node.</summary>
public sealed partial class TenantWorkflowExecutionService(IIamStore security, ITenantWorkflowCatalogStore catalog,
    ITenantWorkflowExecutionStore processes, ITenantWorkflowTaskStore workflowTasks, ITenantTaskStore tasks,
    ITenantSessionStore sessions, ITenantRunStore runs, ITenantSpaceStore spaces,
    TenantWorkflowCatalogService catalogs, TenantRunConfigurationResolver configurations, ITenantModelCatalogReader models,
    IFormalWorkflowRuntime runtime, IdentityService identity, IamAuthorizer iam, IIdGenerator ids, TimeProvider time, AutoModelRouter? routing = null,
    AgentCore.Application.Evaluations.TenantEvaluationService? evaluations = null, WorkflowEvidenceAuthority? evidence = null)
{
    private WorkflowEvidenceAuthority Evidence => evidence ?? new(processes, (ITenantEvaluationStore)security, (ITenantSessionGoalStore)security,
        runs, sessions, tasks, (ITenantAgentStore)security, iam, time);
    private readonly SemaphoreSlim recovery = new(1, 1);
    private DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds());
    public async ValueTask<WorkflowExecution> StartHumanAsync(Guid tenant, Guid workflow, HumanSessionReference human, WorkflowStart request, CancellationToken ct = default)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct);
        _ = WorkflowCatalogRules.RequestKey(request.RequestKey);
        var scope = "human:" + human.PrincipalId.ToString("D");
        var previous = await processes.FindWorkflowStartRequestAsync(tenant, scope, request.RequestKey, ct);
        if (previous is not null && (previous.WorkflowId != workflow || previous.Version != request.Version)) throw new SecurityConflictException("workflow_request_conflict");
        var version = previous is null ? await catalogs.EligibleVersionAsync(tenant, workflow, request.Version, human, ct)
            : await catalogs.VersionAsync(tenant, workflow, request.Version, human, ct);
        var graph = version.Graph.Read();
        await TaskAuthority.RequireAsync(state, human.PrincipalId, "workflows.start", WorkflowCatalogAuthority.Scope(tenant, workflow), iam, ct);
        var inputs = WorkflowExecutionRules.Inputs(graph, request.Inputs); var coordinator = request.CoordinatorInstanceId;
        if (graph.Nodes.Any(n => n.Kind == WorkflowNodeKind.AgentTask))
        {
            coordinator ??= graph.CoordinatorInstanceIds.Count == 1 ? graph.CoordinatorInstanceIds[0] : null;
            if (coordinator is null || !graph.CoordinatorInstanceIds.Contains(coordinator.Value)) throw new ArgumentException("Choose an eligible coordinator.");
        }
        else if (coordinator is not null) throw new ArgumentException("This workflow does not need an Agent coordinator.");
        var fingerprint = WorkflowExecutionRules.Hash(JsonSerializer.Serialize(new { workflow, request.Version, version.Graph.Hash, inputs, coordinator, request.SourceSessionId, request.DeadlineAt }));
        _ = WorkflowCatalogRules.RequestKey(request.RequestKey);
        if (previous is not null)
        {
            if (previous.Fingerprint != fingerprint) throw new SecurityConflictException("workflow_request_conflict");
            await RequireReceiptSource(state, human.PrincipalId, previous, ct);
            if ((await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct)).Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
            WorkflowTelemetry.Record(WorkflowEvent.Replayed, previous);
            return previous;
        }
        if (request.ExpectedSecurityRevision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
        if (!await runtime.IsReadyAsync(ct)) throw new AccessDeniedException("workflow_runtime_unavailable");
        var id = ids.NewId(); var now = Now;
        var value = new WorkflowExecution(tenant, id, workflow, version.Version, version.Graph.Hash,
            new(WorkflowInvocationKind.HumanManual, id, human.PrincipalId, human.PrincipalId, human.AccountId, request.SourceSessionId), coordinator,
            inputs, WorkflowExecutionRules.Hash(inputs), request.RequestKey, fingerprint, now, request.DeadlineAt ?? now.AddDays(30), 1);
        WorkflowExecutionRules.Validate(value, version);
        foreach (var node in graph.Nodes.Where(n => n.Kind == WorkflowNodeKind.AgentTask)) _ = WorkflowTaskRules.Objective(value, node);
        var accepted = await processes.StartHumanWorkflowAsync(new(human, value, request.ExpectedSecurityRevision, ids.NewId()), ct);
        WorkflowTelemetry.Record(WorkflowEvent.Accepted, accepted); return accepted;
    }
    public async ValueTask<WorkflowExecution?> RecoverHumanStartAsync(Guid tenant, HumanSessionReference human, string key, CancellationToken ct = default)
    {
        _ = WorkflowCatalogRules.RequestKey(key); var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct);
        var value = await processes.FindWorkflowStartRequestAsync(tenant, "human:" + human.PrincipalId.ToString("D"), key, ct);
        if (value is not null)
        {
            await TaskAuthority.RequireAsync(state, human.PrincipalId, "workflows.read", WorkflowCatalogAuthority.Scope(tenant, value.WorkflowId), iam, ct);
            await RequireReceiptSource(state, human.PrincipalId, value, ct);
        }
        if ((await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct)).Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
        return value;
    }
    private async ValueTask RequireReceiptSource(TenantSecurityState state, Guid actor, WorkflowExecution value, CancellationToken ct)
    {
        if (value.Source.SessionId is not { } source) return;
        if (await sessions.FindTenantSessionAsync(value.TenantId, source, ct) is null) throw new AccessDeniedException("resource_unavailable");
        await TaskAuthority.RequireAsync(state, actor, "sessions.read", TenantSessionRules.Scope(value.TenantId, source), iam, ct);
    }
    public async ValueTask ReconcileAsync(int limit, CancellationToken ct)
    {
        if (!await recovery.WaitAsync(0, ct)) return;
        var started = System.Diagnostics.Stopwatch.GetTimestamp(); var count = 0;
        try { var page = await processes.ListWorkflowRecoveryAsync(Math.Clamp(limit, 1, 16), ct); count = page.Count;
            foreach (var value in page) { WorkflowTelemetry.Pending(value, Now); await ReconcileOneAsync(value, ct); } }
        finally { recovery.Release(); WorkflowTelemetry.Recovery(count, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds); }
    }
    public async ValueTask ReconcileOneAsync(WorkflowExecution value, CancellationToken ct)
    {
        if (WorkflowExecutionRules.Terminal(value.Status)) return;
        using var trace = RuntimeTelemetry.Activity.StartActivity("workflow.reconcile", System.Diagnostics.ActivityKind.Internal);
        trace?.SetTag("tenant.id", value.TenantId); trace?.SetTag("workflow.id", value.WorkflowId);
        trace?.SetTag("execution.id", value.ExecutionId); trace?.SetTag("workflow.version", value.Version);
        trace?.SetTag("workflow.status", value.Status.ToString());
        if (trace is not null) trace.SetTag("diagnostic.id", trace.TraceId.ToHexString());
        try
        {
            if (value.SourceClosure is { } closure) { await Stop(value, closure.ReasonCode, ct); return; }
            if (value.CancellationRequested || value.DeadlineAt <= Now) { await Stop(value, value.CancellationRequested ? "workflow_cancelled" : "workflow_expired", ct); return; }
            var state = await security.ReadTenantAsync(value.TenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable");
            if (await WorkflowSourceClosureRules.ReasonAsync(value, state, security, sessions, runs, Now, ct) is { } closureReason)
            {
                var closed = value with { Revision = value.Revision + 1, SourceClosure = new(closureReason, Now) };
                if (await processes.WriteWorkflowExecutionAsync(new(closed, value.Revision, ids.NewId()), ct)) await Stop(closed, closureReason, ct);
                return;
            }
            var version = await catalog.FindWorkflowVersionAsync(value.TenantId, value.WorkflowId, value.Version, ct) ?? throw new AccessDeniedException("workflow_version_unavailable");
            var existingSteps = await processes.ReadWorkflowStepsAsync(value.TenantId, value.ExecutionId, ct);
            if (existingSteps.Any(s => s.Kind == WorkflowNodeKind.HumanDecision && s.Status == WorkflowOperationStatus.Reserved
                && s.CreatedAt.AddHours(version.Graph.Read().Nodes.Single(n => n.Id == s.NodeId).Decision!.TimeoutHours) <= Now))
            { await Stop(value, "workflow_decision_expired", ct); return; }
            await WorkflowExecutionAuthority.RequireSourceAsync(state, value, version, security, sessions, iam, Now, ct);
            if (await runtime.ReadAsync(value.TenantId, value.ExecutionId, ct) is null)
            {
                if (security is not ITenantWorkflowInvocationStore invocations) throw new AccessDeniedException("workflow_source_unavailable");
                await invocations.RequireWorkflowParticipantsAsync(value, ct);
            }
            var observed = await runtime.StartAsync(version, value.ExecutionId, ct);
            if (observed.Faulted) { await Project(value, WorkflowExecutionStatus.NeedsAttention, "workflow_runtime_faulted", ct); return; }
            await RecoverConsumed(value, version, observed, ct);
            if (observed.Finished || observed.Cancelled) { await Terminal(value, version, state, observed, ct); return; }
            var bookmark = observed.Bookmarks.SingleOrDefault() ?? throw new InvalidOperationException("Workflow has no actionable bookmark.");
            var node = version.Graph.Read().Nodes.Single(n => n.Id == bookmark.NodeId);
            var operations = await processes.ReadWorkflowStepsAsync(value.TenantId, value.ExecutionId, ct);
            var operation = operations.SingleOrDefault(s => s.NodeId == node.Id);
            if (operation is null)
                operation = await processes.ReserveWorkflowStepAsync(new(value.TenantId, value.ExecutionId,
                    new(value.TenantId, value.ExecutionId, node.Id, 1, ids.NewId(), bookmark.BookmarkId, value.GraphHash, node.Kind, 1,
                        WorkflowOperationStatus.Reserved, Now, node.Kind == WorkflowNodeKind.AgentTask ? ids.NewId() : null,
                        node.Kind == WorkflowNodeKind.AgentTask ? ids.NewId() : null), value.Revision, ids.NewId()), ct);
            WorkflowTelemetry.Operation(operation, Now);
            if (operation.BookmarkId != bookmark.BookmarkId || operation.GraphHash != version.Graph.Hash) throw new InvalidOperationException("Workflow bookmark correlation changed.");
            if (operation.Status >= WorkflowOperationStatus.Settled)
            {
                state = await security.ReadTenantAsync(value.TenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable");
                await WorkflowExecutionAuthority.RequireSourceAsync(state, value, version, security, sessions, iam, Now, ct);
                await runtime.ResumeAsync(value.TenantId, value.ExecutionId, operation.BookmarkId, operation.NodeId, operation.Outcome!, ct);
                if (operation.Status == WorkflowOperationStatus.Settled)
                    await processes.WriteWorkflowStepAsync(new(operation with { Revision = operation.Revision + 1, Status = WorkflowOperationStatus.SignalConsumed }, operation.Revision, ids.NewId()), ct);
                await Project(value, WorkflowExecutionStatus.Running, null, ct); return;
            }
            if (node.Kind == WorkflowNodeKind.AgentTask) { await AgentStep(value, version, node, operation, ct); return; }
            if (node.Kind == WorkflowNodeKind.Verify) { await VerifyStep(state, value, version, node, operation, operations, ct); return; }
            if (node.Kind == WorkflowNodeKind.Branch)
            {
                var outcome = WorkflowProofRules.BranchOutcome(node, operations);
                await Settle(operation with { Outcome = outcome, SelectedEdgeId = version.Graph.Read().Edges.Single(e => e.From == node.Id && e.Outcome == outcome).Id }, ct); return;
            }
            if (node.Kind == WorkflowNodeKind.EndSuccess)
            {
                var proof = await Evidence.FinalProofAsync(state, value, version, operations, ct);
                if (proof.CanSucceed) await Settle(operation with { Outcome = "complete", ProofAccepted = true }, ct);
                else await Project(value, WorkflowExecutionStatus.WaitingForVerification, proof.ReasonCode, ct);
                return;
            }
            if (node.Kind == WorkflowNodeKind.HumanDecision && operation.CreatedAt.AddHours(node.Decision!.TimeoutHours) <= Now)
            { await Stop(value, "workflow_decision_expired", ct); return; }
            await Project(value, WorkflowExecutionStatus.WaitingForHuman, null, ct);
        }
        catch (SecurityConflictException) { WorkflowTelemetry.Conflict(); } // A committed winner is read back on the next bounded pass.
        catch (AccessDeniedException error) { await Project(value, WorkflowExecutionStatus.Blocked, error.ReasonCode, ct); }
        catch (AgentCore.Application.Sessions.AgentCoreException) { await Project(value, WorkflowExecutionStatus.Blocked, "workflow_worker_unavailable", ct); }
        catch (InvalidOperationException) { await Project(value, WorkflowExecutionStatus.NeedsAttention, "workflow_runtime_unavailable", ct); }
        catch (FormalWorkflowRuntimeUnavailableException) { await Project(value, WorkflowExecutionStatus.NeedsAttention, "workflow_runtime_unavailable", ct); }
    }
    private async ValueTask AgentStep(WorkflowExecution execution, WorkflowVersion version, WorkflowNode node, WorkflowStepOperation operation, CancellationToken ct)
    {
        if (operation.Status == WorkflowOperationStatus.Reserved)
        {
            var state = await security.ReadTenantAsync(execution.TenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable");
            var binding = node.Task!; var now = Now; var context = await spaces.FindSpaceAsync(execution.TenantId, binding.SpaceId, ct) ?? throw new AccessDeniedException("space_unavailable");
            var resolved = await configurations.ResolveInSpaceAsync(execution.TenantId, binding.TargetInstanceId,
                new(execution.TenantId, binding.SpaceId, binding.EnvironmentId, binding.EnvironmentId is null ? 0 : context.EnvironmentSelectionEpoch, 1, now), ct);
            var coordinator = (await configurations.CurrentPolicyAsync(execution.TenantId, execution.CoordinatorInstanceId!.Value, ct)).Agent;
            var catalog = await models.ReadAsync(state, ct); var worker = resolved.Agent;
            var preference = worker.Instance.UnattendedModel ?? ModelPreference.Inherit; var defaults = resolved.Configuration.Configuration.Definition.ModelDefaults;
            var selected = routing is null ? SessionModelBinder.BindPreference(catalog, preference, defaults)
                : await routing.ResolveAsync(catalog, preference, defaults, false, true, "delegatedTask", RoutingTaskSignals.Project(null, "delegatedTask"), ct);
            var model = new AgentRunModelPin(selected.OfferingId, selected.ConnectionId, selected.ExternalModelId, selected.ReasoningEffort, selected.Binding, selected.Decision);
            var allocated = WorkflowTaskRules.Allocate(execution, version, node.Id, resolved.Configuration.Configuration.Definition, worker.Instance.ExecutionBudgets,
                await processes.ReadWorkflowTasksAsync(execution.TenantId, execution.ExecutionId, ct));
            var activation = ids.NewId(); var session = ids.NewSessionId(); var deadline = execution.DeadlineAt < now.Add(TaskLimits.MaxLifetime) ? execution.DeadlineAt : now.Add(TaskLimits.MaxLifetime);
            var source = new TaskSource(execution.TenantId, null, coordinator.Instance.InstanceId, coordinator.PrincipalId, TaskActorKind.WorkflowContinuation,
                null, null, null, execution.Source.OriginalHumanPrincipalId, activation, null, 1,
                new(execution.WorkflowId, execution.ExecutionId, execution.Version, execution.GraphHash, node.Id, 1, operation.OperationId, execution.Source.SessionId, execution.Source.Causation));
            var request = new TaskRequest(WorkflowTaskRules.Objective(execution, node), node.Name, [binding.TargetInstanceId], false, binding.SpaceId, binding.EnvironmentId,
                [], false, deadline, "workflow:" + operation.OperationId.ToString("D"));
            var assignment = new TaskAssignment(worker.Instance.InstanceId, worker.PrincipalId, binding.SpaceId, binding.EnvironmentId, worker.Instance.Revision,
                TaskLimits.Hash(resolved.Configuration.Configuration), "explicit", model, resolved.Configuration.Configuration.SpaceContext!.EnvironmentSelectionEpoch, worker.Instance.Persona.Name, allocated);
            var task = new DelegatedTask(operation.TaskId!.Value, source, request, assignment, DelegatedTaskState.Queued, 1, now, now, deadline, session, operation.WorkerRunId);
            var graph = TaskAdmissionFactory.Worker(task, worker, resolved.Configuration, model, session, ids.NewId(), activation, operation.WorkerRunId!.Value, ids.NewId(), now);
            var admitted = await workflowTasks.AdmitWorkflowTaskAsync(new(execution.TenantId, execution.ExecutionId, operation.OperationId, operation.Revision, state.Tenant.Revision, task, graph.Session, graph.Run, ids.NewId()), ct);
            WorkflowTelemetry.Record(admitted.Replayed ? WorkflowEvent.TaskReplayed : WorkflowEvent.TaskAdmitted, execution);
            await Project(execution, WorkflowExecutionStatus.WaitingForAgent, null, ct); return;
        }
        var saved = await tasks.FindTaskAsync(execution.TenantId, operation.TaskId!.Value, ct) ?? throw new AccessDeniedException("workflow_task_source_unavailable");
        if (saved.State == DelegatedTaskState.NeedsAttention)
        {
            var worker = await WorkflowWorkerRun(saved, ct);
            await Project(execution, WorkflowExecutionStatus.NeedsAttention, worker is null || worker.RequiresEffectReconciliation
                ? "workflow_unknown_effect" : "workflow_worker_needs_attention", ct); return;
        }
        if (TaskLimits.Terminal(saved.State))
        {
            var next = operation with { Revision = operation.Revision + 1, Status = WorkflowOperationStatus.Settled,
                WorkerRunId = saved.TerminalRunId, Outcome = saved.State == DelegatedTaskState.Succeeded ? "succeeded" : "failed", ProofAccepted = false, SettledAt = Now };
            await processes.WriteWorkflowStepAsync(new(next, operation.Revision, ids.NewId()), ct); return;
        }
        var run = await WorkflowWorkerRun(saved, ct);
        await Project(execution, run?.Status == AgentRunStatus.WaitingForApproval ? WorkflowExecutionStatus.WaitingForOperationApproval : WorkflowExecutionStatus.WaitingForAgent, null, ct);
    }
    private async ValueTask<AgentRun?> WorkflowWorkerRun(DelegatedTask task, CancellationToken ct)
    {
        if (task.InitialWorkerRunId is not { } runId) return null;
        var chain = new Dictionary<Guid, AgentRun>();
        for (var hop = 0; hop < 64; hop++)
        {
            var item = await runs.FindTenantRunAsync(task.Source.TenantId, runId, ct);
            if (item is null || !chain.TryAdd(item.AgentRunId, item)) break;
            if (item.Result?.ContinuationRunId is not { } next) break;
            runId = next;
        }
        return TaskSettlement.FinalRun(task, id => chain.GetValueOrDefault(id));
    }
    private async ValueTask Project(WorkflowExecution before, WorkflowExecutionStatus status, string? reason, CancellationToken ct)
    {
        if (before.Status == status && before.ReasonCode == reason) return;
        var next = before with { Revision = before.Revision + 1, Status = status, ReasonCode = reason, TerminalAt = WorkflowExecutionRules.Terminal(status) ? Now : null };
        if (await processes.WriteWorkflowExecutionAsync(new(next, before.Revision, ids.NewId()), ct)) WorkflowTelemetry.Record(WorkflowEvent.Projected, next);
    }
}
