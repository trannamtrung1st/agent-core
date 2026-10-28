using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Tasks;
using AgentCore.Domain.Workflows;

namespace AgentCore.Application.Workflows;

public static class WorkflowStepEvidenceRules
{
    public static void RequireWrite(WorkflowExecution execution, WorkflowVersion version, WorkflowStepOperation before,
        WorkflowStepOperation next, DelegatedTask? task, AgentRun? worker)
    {
        WorkflowExecutionAuthority.StepChange(execution, version, before, next);
        if (next.Status == WorkflowOperationStatus.SignalConsumed && before.Status == WorkflowOperationStatus.Settled) return;
        if (before.Kind != WorkflowNodeKind.AgentTask || before.Status != WorkflowOperationStatus.Admitted || next.Status != WorkflowOperationStatus.Settled
            || task is null || worker is null || task.TaskId != before.TaskId || task.Source.Workflow?.OperationId != before.OperationId
            || task.Source.Workflow.ExecutionId != execution.ExecutionId || task.Source.Workflow.GraphHash != execution.GraphHash
            || task.Source.Workflow.NodeId != before.NodeId || task.Assignment.InstanceId != worker.AgentInstanceId || task.WorkerSessionId != worker.SessionId
            || !worker.IsTerminal || worker.RequiresEffectReconciliation || task.State == DelegatedTaskState.NeedsAttention
            || task.TerminalRunId != worker.AgentRunId || !TaskLimits.Terminal(task.State) || next.WorkerRunId != worker.AgentRunId
            || next.Outcome != (task.State == DelegatedTaskState.Succeeded ? "succeeded" : "failed") || next.ProofAccepted
            || next.EvaluationAttemptId is not null || next.DecisionActorId is not null || next.SelectedEdgeId is not null)
            throw new AccessDeniedException("workflow_step_evidence_unavailable");
    }
}
