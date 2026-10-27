using AgentCore.Domain.Conversation;
using AgentCore.Domain.Tasks;

namespace AgentCore.Application.Tasks;

public static class TaskSettlement
{
    public static string? StopReason(DelegatedTask task, AgentRun run, bool sourceAvailable, bool targetAvailable, DateTimeOffset at)
    {
        if (run.IsTerminal) return run.Status == AgentRunStatus.Cancelled && run.CancellationRequestedAtUtc >= task.DeadlineAt
            ? "task_deadline_exceeded" : task.Reason is "task_deadline_exceeded" or "task_source_unavailable" or "task_executor_unavailable" ? task.Reason : null;
        return task.DeadlineAt <= at ? "task_deadline_exceeded" : !sourceAvailable ? "task_source_unavailable" : !targetAvailable ? "task_executor_unavailable" : null;
    }
    public static AgentRun FinalRun(DelegatedTask task, Func<Guid, AgentRun?> read)
    {
        var run = read(task.InitialWorkerRunId ?? throw new InvalidOperationException("Task has no worker."))
            ?? throw new InvalidOperationException("Task worker is missing.");
        var visited = new HashSet<Guid>();
        for (var i = 0; i < 64; i++)
        {
            if (!visited.Add(run.AgentRunId) || run.Owner.TenantId != task.Source.TenantId || run.AgentInstanceId != task.Assignment.InstanceId
                || run.Owner.ExecutorPrincipalId != task.Assignment.PrincipalId || run.SessionId != task.WorkerSessionId
                || run.Admission.DelegatedTask?.TaskId != task.TaskId || run.Admission.DelegatedTask.AssignmentHash != TaskLimits.Hash(task.Assignment))
                throw new InvalidOperationException("Task worker lineage contradicts saved assignment.");
            if (run.Result?.ContinuationRunId is not { } next) return run;
            var child = read(next) ?? throw new InvalidOperationException("Task successor is missing.");
            if (child.Admission.ExecutionContext?.ParentRunId != run.AgentRunId || child.Admission.Activation.Kind != ActivationKind.ApprovalContinuation
                || child.Admission.ApprovalResume?.SourceRunId != run.AgentRunId) throw new InvalidOperationException("Task successor has invalid provenance.");
            run = child;
        }
        throw new InvalidOperationException("Task successor chain exceeds its bound.");
    }
    public static DelegatedTask Observe(DelegatedTask task, AgentRun run, DateTimeOffset at, string? stopReason = null)
    {
        if (TaskLimits.Terminal(task.State)) return task;
        var stoppedFor = stopReason ?? (task.Reason is "task_deadline_exceeded" or "task_source_unavailable" or "task_executor_unavailable" ? task.Reason : null);
        var state = run.IsTerminal && run.RequiresEffectReconciliation ? DelegatedTaskState.NeedsAttention : run.Status switch {
            AgentRunStatus.Completed when run.Result?.AttentionRequired == true => DelegatedTaskState.NeedsAttention,
            AgentRunStatus.Completed => DelegatedTaskState.Succeeded,
            AgentRunStatus.Failed => DelegatedTaskState.Failed,
            AgentRunStatus.Cancelled when stoppedFor == "task_deadline_exceeded" => DelegatedTaskState.Failed,
            AgentRunStatus.Cancelled => DelegatedTaskState.Cancelled,
            AgentRunStatus.Running => DelegatedTaskState.Running,
            AgentRunStatus.WaitingForApproval or AgentRunStatus.WaitingForSignal => DelegatedTaskState.Waiting,
            AgentRunStatus.Queued or AgentRunStatus.WaitingToRetry => DelegatedTaskState.Queued,
            _ => throw new InvalidOperationException("Unsupported worker Run status.") };
        if (state == task.State && task.CancellationRequested == run.CancellationRequested && (stoppedFor is null || task.Reason == stoppedFor)) return task;
        var terminal = TaskLimits.Terminal(state);
        var result = terminal && run.Result?.Text is { Length: > 0 } text ? TaskLimits.Preview(text, TaskLimits.ResultCharacters) : null;
        return task.Advance(state, at, terminalRunId: terminal ? run.AgentRunId : null, result: state == DelegatedTaskState.Succeeded ? result ?? "Work completed without a textual result." : result,
            reason: state == DelegatedTaskState.NeedsAttention ? "worker_effect_or_outcome_needs_attention" : stoppedFor ?? (state == DelegatedTaskState.Failed ? run.Failure?.Code ?? "worker_failed"
                : state == DelegatedTaskState.Cancelled ? "worker_cancelled" : null), cancellationRequested: task.CancellationRequested || run.CancellationRequested);
    }
}
