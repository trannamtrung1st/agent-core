using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Tasks;

namespace AgentCore.Application.Tasks;

/// <summary>Conservative durable reservations. Unknown usage never returns credits; a restart cannot reset a root.</summary>
public static class TaskBudgetRules
{
    private static (int Steps, int Seconds) Charge(DelegatedTask task, IReadOnlyList<DelegatedTask> all, Func<Guid, AgentRun?> runs, HashSet<Guid> seen)
    {
        var reserved = task.Assignment.Budget ?? throw new AccessDeniedException("task_budget_unavailable");
        if (!seen.Add(task.TaskId)) throw new AccessDeniedException("task_budget_invalid");
        if (TaskLimits.Terminal(task.State) && task.InitialWorkerRunId is null) return (0, 0);
        if (!TaskLimits.Terminal(task.State)) return (reserved.MaxSteps + reserved.ReportMaxSteps, reserved.DurationSeconds + reserved.ReportDurationSeconds);
        // A blocked task never acquired an execution lease, so its entire reservation is unspent.
        if (task.WorkerSessionId is null) return (0, 0);
        var final = TaskSettlement.FinalRun(task, runs);
        if (!final.IsTerminal || final.RequiresEffectReconciliation) return (reserved.MaxSteps + reserved.ReportMaxSteps, reserved.DurationSeconds + reserved.ReportDurationSeconds);
        var steps = final.Checkpoint?.StepCount ?? (final.AttemptCount == 0 ? 0 : reserved.MaxSteps);
        var seconds = final.Checkpoint?.ActiveExecutionMs is { } active ? (active + 999) / 1000 : final.AttemptCount == 0 ? 0 : reserved.DurationSeconds;
        foreach (var child in all.Where(t => t.Source.ParentTaskId == task.TaskId))
        {
            var charge = Charge(child, all, runs, seen); steps = checked(steps + charge.Steps); seconds = checked(seconds + charge.Seconds);
        }
        // Report credit stays reserved conservatively. Its own approval successors
        // use that same small profile; completing a worker cannot reset root credit.
        return (Math.Min(reserved.MaxSteps, steps) + reserved.ReportMaxSteps, Math.Min(reserved.DurationSeconds, seconds) + reserved.ReportDurationSeconds);
    }
    public static TaskExecutionAllowance Allowance(AgentRun source, IReadOnlyList<DelegatedTask> all, Func<Guid, AgentRun?> runs, TaskExecutionAllowance? workflowCharge = null)
    {
        var profile = source.Admission.ExecutionBudget?.Profile ?? throw new AccessDeniedException("task_budget_unavailable");
        var steps = profile.MaxSteps - (workflowCharge?.MaxSteps ?? 0); var seconds = profile.DurationSeconds - (workflowCharge?.DurationSeconds ?? 0);
        foreach (var child in all.Where(t => t.Source.SessionId == source.SessionId && t.Source.RootActivationId == source.Admission.Activation.RootActivationId
            && t.Source.ActorKind == TaskActorKind.Agent))
        {
            var charged = Charge(child, all, runs, []); steps -= charged.Steps; seconds -= charged.Seconds;
        }
        return new(Math.Max(0, steps), Math.Max(0, seconds));
    }
    public static TaskBudgetReservation Allocate(AgentRun? source, ExecutionBudgetProfile worker, IReadOnlyList<DelegatedTask> all, Func<Guid, AgentRun?> runs, DateTimeOffset at, Guid? logicalSourceRunId = null, bool reportOnCompletion = false, TaskExecutionAllowance? workflowCharge = null)
    {
        worker.Validate();
        var reportSteps = reportOnCompletion ? 8 : 0; var reportSeconds = reportOnCompletion ? 60 : 0;
        if (source is null)
        {
            var human = new TaskBudgetReservation(worker.MaxSteps - reportSteps, worker.DurationSeconds - reportSeconds,
                worker.MaxSteps, worker.DurationSeconds, reportSteps, reportSeconds);
            if (human.MaxSteps < 8 || human.DurationSeconds < 60) throw new AccessDeniedException("task_root_budget_exhausted");
            human.Validate(); return human;
        }
        var allowance = Allowance(source, all, runs, workflowCharge); var own = source.Admission.ExecutionBudget!.Profile;
        var parentTask = source.Admission.DelegatedTask is { } work ? all.SingleOrDefault(t => t.TaskId == work.TaskId) : null;
        if (source.Admission.DelegatedTask is not null && parentTask is null) throw new AccessDeniedException("task_budget_unavailable");
        var rootSteps = parentTask?.Assignment.Budget?.RootMaxSteps ?? own.MaxSteps;
        var rootSeconds = parentTask?.Assignment.Budget?.RootDurationSeconds ?? own.DurationSeconds;
        var siblings = all.Count(t => t.Source.RunId == (logicalSourceRunId ?? source.AgentRunId));
        var slots = TaskLimits.MaxChildrenPerRun - siblings;
        if (slots <= 0) throw new AccessDeniedException("task_capacity_exceeded");
        var selfSteps = Math.Max(8, checked((source.Checkpoint?.StepCount ?? 0) + 1));
        var active = source.Checkpoint?.ActiveExecutionMs ?? 0;
        if (source.Checkpoint?.BudgetRecordedAtUtc is { } recorded && source.Claim is not null)
            active += (int)Math.Clamp((at - recorded).TotalMilliseconds, 0, source.Checkpoint.RemainingOverallBudgetMs);
        var selfSeconds = Math.Max(60, (active + 999) / 1000 + 1);
        var freeSteps = allowance.MaxSteps - selfSteps; var freeSeconds = allowance.DurationSeconds - selfSeconds;
        var stepPackage = freeSteps / slots; var secondPackage = freeSeconds / slots;
        if (stepPackage < 8 + reportSteps || secondPackage < 60 + reportSeconds) { stepPackage = freeSteps; secondPackage = freeSeconds; }
        var steps = Math.Min(worker.MaxSteps, stepPackage - reportSteps);
        var seconds = Math.Min(worker.DurationSeconds, secondPackage - reportSeconds);
        if (steps < 8 || seconds < 60) throw new AccessDeniedException("task_root_budget_exhausted");
        var reservation = new TaskBudgetReservation(steps, seconds, rootSteps, rootSeconds, reportSteps, reportSeconds); reservation.Validate(); return reservation;
    }
    public static EffectiveExecutionBudget Worker(AgentDefinition definition, ExecutionBudgetPolicy? instance, TaskBudgetReservation reservation)
    {
        reservation.Validate(); var own = ExecutionBudgetResolver.Resolve(definition, instance, false);
        if (reservation.MaxSteps > own.Profile.MaxSteps || reservation.DurationSeconds > own.Profile.DurationSeconds) throw new AccessDeniedException("task_worker_budget_changed");
        var profile = own.Profile with { MaxSteps = reservation.MaxSteps, DurationSeconds = reservation.DurationSeconds,
            PerToolSeconds = Math.Min(own.Profile.PerToolSeconds, reservation.DurationSeconds), Preset = ExecutionBudgetPreset.Custom };
        profile.Validate(); return own with { Profile = profile };
    }
    public static EffectiveExecutionBudget Report(AgentDefinition definition, ExecutionBudgetPolicy? instance, TaskBudgetReservation reservation)
    {
        reservation.Validate();
        if (reservation.ReportMaxSteps == 0) throw new AccessDeniedException("task_report_budget_unavailable");
        return Worker(definition, instance, new(reservation.ReportMaxSteps, reservation.ReportDurationSeconds,
            reservation.RootMaxSteps, reservation.RootDurationSeconds));
    }
}
