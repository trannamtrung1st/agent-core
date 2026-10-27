using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Execution;

public static partial class AgentRunWaitExecution
{
    public static bool IsReady(AgentRun run, DateTimeOffset now) =>
        run.Wait is { Mode: AgentRunWaitMode.Duration } wait && wait.DeadlineUtc <= now;

    public static AgentRun Apply(AgentRun run, AgentRunCommand command)
    {
        if (run.Revision != command.ExpectedRevision || command.AtUtc < run.UpdatedAtUtc || command.AtUtc.Offset != TimeSpan.Zero)
            throw new AgentRunTransitionException(AgentRunTransitionFailure.StaleRevision, "Wait transition is stale.");
        if (command is AgentRunCommand.SuspendWait suspend)
        {
            if (suspend.Wait.Mode != AgentRunWaitMode.Duration
                || !AgentRunToolCallCheckpoint.TryRead(suspend.Value, out var messages)
                || !AgentRunToolCallCheckpoint.PendingCalls(messages!).Any(c => c.Id == suspend.Wait.ToolCallId && c.Name == "execution.wait"))
                throw new ArgumentException("Duration wait requires its pending tool call checkpoint.");
            return command.Apply(run, Guid.NewGuid);
        }
        if (command is not AgentRunCommand.ResumeWait wake || run.Status != AgentRunStatus.WaitingForSignal || run.Wait is not { } wait
            || !IsReady(run, wake.AtUtc))
            throw new AgentRunTransitionException(AgentRunTransitionFailure.NotClaimable, "Wait condition is not ready.");
        var result = System.Text.Json.JsonSerializer.Serialize(new { reason = "elapsed", elapsedSeconds = Math.Max(0, (wake.AtUtc - wait.StartedAtUtc).TotalSeconds) });
        var checkpoint = AgentRunToolCallCheckpoint.AppendWaitResult(run.Checkpoint!, wait.ToolCallId, result);
        return run.ResumeFromSignal(wake.ExpectedRevision, wake.Generation, checkpoint, wake.AtUtc, wake.LeaseExpiresAt);
    }
}
