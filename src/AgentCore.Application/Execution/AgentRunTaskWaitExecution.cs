using AgentCore.Domain.Conversation;
using AgentCore.Domain.Tasks;

namespace AgentCore.Application.Execution;

public sealed record TaskWaitFact(Guid TaskId, bool Available, DelegatedTaskState? State, long Revision, long MessageSequence);
public static partial class AgentRunWaitExecution
{
    private static bool TaskReady(TaskWaitWitness witness, TaskWaitFact fact) => fact.Available
        && (fact.State is { } state && TaskLimits.Terminal(state) || fact.MessageSequence > witness.MessageSequence);
    public static AgentRunWait BindTasks(AgentRunWait wait, IReadOnlyList<TaskWaitFact> facts)
    {
        if (wait.Mode != AgentRunWaitMode.Tasks || wait.Tasks is null || wait.Tasks.Any(t => t.Revision != 0 || t.MessageSequence != 0)
            || facts.Count != wait.Tasks.Count || facts.Any(t => !t.Available)) throw new ArgumentException("Wait requires exact readable task witnesses.");
        return wait with { Tasks = Array.AsReadOnly(wait.Tasks.Select(t => {
            var fact = facts.Single(f => f.TaskId == t.TaskId); return new TaskWaitWitness(t.TaskId, fact.Revision, fact.MessageSequence); }).ToArray()) };
    }
    public static bool TasksReady(AgentRun run, DateTimeOffset at, IReadOnlyList<TaskWaitFact> facts)
    {
        if (run.Wait is not { Mode: AgentRunWaitMode.Tasks, Tasks: { } targets } wait) return false;
        if (wait.DeadlineUtc <= at || targets.Any(t => !facts.Any(f => f.TaskId == t.TaskId && f.Available))) return true;
        var ready = targets.Count(t => facts.Any(f => f.TaskId == t.TaskId && TaskReady(t, f)));
        return wait.Until == AgentRunWaitUntil.All ? ready == targets.Count : ready > 0;
    }
    public static AgentRun ApplyTasks(AgentRun run, AgentRunCommand command, IReadOnlyList<TaskWaitFact> facts)
    {
        if (run.Revision != command.ExpectedRevision || command.AtUtc < run.UpdatedAtUtc) throw new ArgumentException("Task wait revision is stale.");
        if (command is AgentRunCommand.SuspendWait suspend)
        {
            var bound = BindTasks(suspend.Wait, facts);
            return run.SuspendForSignal(suspend.ExpectedRevision, suspend.Generation, suspend.Value, bound, suspend.AtUtc);
        }
        if (command is not AgentRunCommand.ResumeWait wake || run.Status != AgentRunStatus.WaitingForSignal || run.Wait is not { Tasks: { } targets } wait
            || !TasksReady(run, wake.AtUtc, facts)) throw new AgentRunTransitionException(AgentRunTransitionFailure.NotClaimable, "Task wait condition is not ready.");
        var ready = targets.Where(t => facts.Any(f => f.TaskId == t.TaskId && TaskReady(t, f))).Select(t => t.TaskId).ToArray();
        var unavailable = targets.Where(t => !facts.Any(f => f.TaskId == t.TaskId && f.Available)).Select(t => t.TaskId).ToArray();
        var met = wait.Until == AgentRunWaitUntil.All ? ready.Length == targets.Count : ready.Length > 0;
        var text = System.Text.Json.JsonSerializer.Serialize(new { reason = unavailable.Length > 0 ? "unavailable" : met ? "condition_met" : "timeout",
            taskIds = ready, pendingTaskIds = targets.Select(t => t.TaskId).Except(ready).Except(unavailable), unavailableTaskIds = unavailable,
            statuses = targets.Select(t => { var fact = facts.SingleOrDefault(f => f.TaskId == t.TaskId); return new { taskId = t.TaskId,
                state = fact?.Available == true ? fact.State?.ToString() : "Unavailable", revision = fact?.Available == true ? fact.Revision : (long?)null,
                newMessage = fact?.Available == true && fact.MessageSequence > t.MessageSequence }; }), elapsedSeconds = Math.Max(0, (wake.AtUtc - wait.StartedAtUtc).TotalSeconds) });
        return run.ResumeFromSignal(wake.ExpectedRevision, wake.Generation, AgentRunToolCallCheckpoint.AppendWaitResult(run.Checkpoint!, wait.ToolCallId, text), wake.AtUtc, wake.LeaseExpiresAt);
    }
}
