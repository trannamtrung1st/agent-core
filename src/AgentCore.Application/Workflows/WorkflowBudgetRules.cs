using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Workflows;

namespace AgentCore.Application.Workflows;

public static class WorkflowBudgetRules
{
    public static TaskExecutionAllowance Charge(AgentRun source, IEnumerable<WorkflowExecution> executions)
    {
        // A process owns independent worker roots but cannot refund or reset the invoking Session/root's credit.
        var accepted = executions.Where(e => e.TenantId == source.Owner.TenantId && e.Source.Kind == WorkflowInvocationKind.ClaimedAgent
            && e.Source.SessionId == source.SessionId && e.Source.RootActivationId == source.Admission.Activation.RootActivationId);
        return new(accepted.Sum(e => e.SourceBudget?.MaxSteps ?? 0), accepted.Sum(e => e.SourceBudget?.DurationSeconds ?? 0));
    }
    public static void RequireReservation(WorkflowExecution execution, AgentRun source, TaskExecutionAllowance available, DateTimeOffset at)
    {
        if (execution.SourceBudget is not { } budget) return;
        var ownSteps = Math.Max(8, (source.Checkpoint?.StepCount ?? 0) + 1);
        var active = source.Checkpoint?.ActiveExecutionMs ?? 0;
        if (source.Checkpoint?.BudgetRecordedAtUtc is { } recorded && source.Claim is not null)
            active += (int)Math.Clamp((at - recorded).TotalMilliseconds, 0, source.Checkpoint.RemainingOverallBudgetMs);
        var ownSeconds = Math.Max(60, (active + 999) / 1000 + 1);
        if (budget.MaxSteps > available.MaxSteps - ownSteps || budget.DurationSeconds > available.DurationSeconds - ownSeconds)
            throw new AccessDeniedException("workflow_source_budget_exhausted");
    }
}
