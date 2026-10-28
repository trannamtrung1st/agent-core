using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Evaluations;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;
using AgentCore.Domain.Workflows;

namespace AgentCore.Application.Workflows;

public sealed partial class TenantWorkflowExecutionService
{
    private async ValueTask VerifyStep(TenantSecurityState state, WorkflowExecution execution, WorkflowVersion version,
        WorkflowNode node, WorkflowStepOperation operation, IReadOnlyList<WorkflowStepOperation> steps, CancellationToken ct)
    {
        if (node.Verification!.Kind == WorkflowVerificationKind.Evaluation && operation.EvaluationAttemptId is null)
        {
            if (evaluations is null) throw new AccessDeniedException("workflow_evaluation_unavailable");
            var source = steps.Single(s => s.NodeId == node.Verification.TaskNodeId);
            var task = await tasks.FindTaskAsync(execution.TenantId, source.TaskId!.Value, ct) ?? throw new AccessDeniedException("workflow_task_evidence_unavailable");
            var worker = task.TerminalRunId is { } id ? await runs.FindTenantRunAsync(execution.TenantId, id, ct) : null;
            if (task.State != DelegatedTaskState.Succeeded || worker is null || !worker.IsTerminal) throw new AccessDeniedException("workflow_task_evidence_unavailable");
            var actor = await Evidence.EvaluatorAsync(execution, ct);
            var attempt = await evaluations.RunAsync(actor, execution.TenantId,
                new(EvaluationSubjectKind.AgentRun, worker.AgentRunId, worker.Revision, node.Verification.SuiteId!.Value,
                    node.Verification.SuiteVersion!.Value, WorkflowEvidenceAuthority.EvaluationKey(operation), state.Tenant.Revision), null, ct);
            await processes.WriteWorkflowStepAsync(new(operation with { Revision = operation.Revision + 1,
                Status = WorkflowOperationStatus.Admitted, EvaluationAttemptId = attempt.AttemptId }, operation.Revision, ids.NewId()), ct);
            return;
        }
        var result = await Evidence.VerificationAsync(state, execution, version, node, operation, steps, ct);
        if (result.Status is WorkflowEvidenceStatus.Passed or WorkflowEvidenceStatus.Failed)
            await Settle(operation with { Outcome = result.Status == WorkflowEvidenceStatus.Passed ? "passed" : "failed", ProofAccepted = result.Status == WorkflowEvidenceStatus.Passed }, ct);
        else await Project(execution, result.Status == WorkflowEvidenceStatus.Inconclusive ? WorkflowExecutionStatus.NeedsAttention
            : result.ReasonCode == "evaluation_operation_approval_pending" ? WorkflowExecutionStatus.WaitingForOperationApproval : WorkflowExecutionStatus.WaitingForVerification, result.ReasonCode, ct);
    }
    private ValueTask<bool> Settle(WorkflowStepOperation operation, CancellationToken ct) => processes.WriteWorkflowStepAsync(
        new(operation with { Revision = operation.Revision + 1, Status = WorkflowOperationStatus.Settled, SettledAt = Now }, operation.Revision, ids.NewId()), ct);

    private async ValueTask RecoverConsumed(WorkflowExecution execution, WorkflowVersion version, FormalWorkflowState observed, CancellationToken ct)
    {
        // A crash may happen after Elsa commits advancement but before Core records signal consumption.
        // Only the exact successor (or the same terminal success node) proves this saved signal was consumed.
        var graph = version.Graph.Read();
        foreach (var step in (await processes.ReadWorkflowStepsAsync(execution.TenantId, execution.ExecutionId, ct)).Where(s => s.Status == WorkflowOperationStatus.Settled))
        {
            var target = step.Kind == WorkflowNodeKind.EndSuccess ? step.NodeId : graph.Edges.Single(e => e.From == step.NodeId && e.Outcome == step.Outcome).To;
            if (observed.Bookmarks.Any(b => b.NodeId == target) || observed.Finished && observed.TerminalNodeId == target)
                await processes.WriteWorkflowStepAsync(new(step with { Revision = step.Revision + 1, Status = WorkflowOperationStatus.SignalConsumed }, step.Revision, ids.NewId()), ct);
        }
    }
    private async ValueTask Terminal(WorkflowExecution execution, WorkflowVersion version, TenantSecurityState state, FormalWorkflowState observed, CancellationToken ct)
    {
        if (observed.Cancelled) { await Project(execution, WorkflowExecutionStatus.NeedsAttention, "workflow_runtime_cancelled", ct); return; }
        var node = version.Graph.Read().Nodes.SingleOrDefault(n => n.Id == observed.TerminalNodeId);
        var steps = await processes.ReadWorkflowStepsAsync(execution.TenantId, execution.ExecutionId, ct);
        if (node?.Kind == WorkflowNodeKind.EndFailure) { await Project(execution, WorkflowExecutionStatus.Failed, "workflow_route_failed", ct); return; }
        var proof = await Evidence.FinalProofAsync(state, execution, version, steps, ct);
        if (node?.Kind == WorkflowNodeKind.EndSuccess && proof.CanSucceed && steps.Any(s => s.NodeId == node.Id && s.Status == WorkflowOperationStatus.SignalConsumed && s.ProofAccepted))
            await Project(execution, WorkflowExecutionStatus.Completed, null, ct);
        else await Project(execution, WorkflowExecutionStatus.NeedsAttention, proof.ReasonCode ?? "workflow_terminal_proof_pending", ct);
    }
    private async ValueTask Stop(WorkflowExecution execution, string reason, CancellationToken ct)
    {
        await runtime.CancelAsync(execution.TenantId, execution.ExecutionId, ct);
        foreach (var task in await processes.ReadWorkflowTasksAsync(execution.TenantId, execution.ExecutionId, ct))
            if (!TaskLimits.Terminal(task.State)) await tasks.ReconcileTaskAsync(execution.TenantId, task.TaskId, task.Revision, ids.NewId(), ct, reason);
        var saved = await processes.ReadWorkflowTasksAsync(execution.TenantId, execution.ExecutionId, ct);
        var unknown = false;
        foreach (var task in saved.Where(t => t.State == DelegatedTaskState.NeedsAttention))
            if (await WorkflowWorkerRun(task, ct) is not { RequiresEffectReconciliation: false }) unknown = true;
        if (unknown) await Project(execution, WorkflowExecutionStatus.NeedsAttention, "workflow_unknown_effect", ct);
        else if (saved.Any(t => !TaskLimits.Terminal(t.State))) await Project(execution, WorkflowExecutionStatus.Blocked, "workflow_stop_pending", ct);
        else await Project(execution, execution.CancellationRequested ? WorkflowExecutionStatus.Cancelled : WorkflowExecutionStatus.Expired, reason, ct);
    }
    public async ValueTask<WorkflowProcessDecision> DecideHumanAsync(WorkflowDecisionCommand command, CancellationToken ct = default)
    {
        await TenantHumanSessionAuthority.RequireAsync(security, identity, command.TenantId, command.Human, ct);
        var result = await processes.DecideWorkflowAsync(command, ct);
        if (await processes.FindWorkflowExecutionAsync(command.TenantId, command.ExecutionId, ct) is { } execution)
            AgentCore.Application.Observability.WorkflowTelemetry.Record(AgentCore.Application.Observability.WorkflowEvent.DecisionRecorded, execution);
        return result;
    }
    public async ValueTask<WorkflowExecution> CancelHumanAsync(WorkflowCancelCommand command, CancellationToken ct = default)
    {
        await TenantHumanSessionAuthority.RequireAsync(security, identity, command.TenantId, command.Human, ct);
        var saved = await processes.CancelWorkflowAsync(command, ct);
        AgentCore.Application.Observability.WorkflowTelemetry.Record(AgentCore.Application.Observability.WorkflowEvent.CancellationRequested, saved);
        return saved;
    }
}
