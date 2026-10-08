using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Execution;

public static class AgentRunWaitExecution
{
    public static IReadOnlyList<Guid> ReadyTargets(AgentRun run, AgentRunWait wait, IReadOnlyList<BackgroundCompletionCandidate> children)
    {
        var owned = children.Where(c => c.Run.Owner == run.Owner && c.Session.Origin.OriginatingSessionId == run.SessionId
            && c.Session.Origin.InitialBackgroundAgentRunId == c.Run.AgentRunId).ToArray();
        if (wait.BackgroundSessionIds.Any(id => id == run.SessionId || !owned.Any(c => c.Session.SessionId == id)))
            throw new ArgumentException("Wait targets must be initial background children owned by this parent Session.");
        return wait.BackgroundSessionIds.Where(id => owned.Any(c => c.Session.SessionId == id && c.Run.IsTerminal)).ToArray();
    }

    public static bool IsReady(AgentRun run, DateTimeOffset now, IReadOnlyList<BackgroundCompletionCandidate> children)
    {
        if (run.Wait is not { } wait) return false;
        if (wait.DeadlineUtc <= now) return true;
        if (wait.Mode == AgentRunWaitMode.Duration) return false;
        var ready = ReadyTargets(run, wait, children);
        return wait.Until == AgentRunWaitUntil.All ? ready.Count == wait.BackgroundSessionIds.Count : ready.Count > 0;
    }

    public static AgentRun Apply(AgentRun run, AgentRunCommand command, IReadOnlyList<BackgroundCompletionCandidate> children)
    {
        if (run.Revision != command.ExpectedRevision || command.AtUtc < run.UpdatedAtUtc || command.AtUtc.Offset != TimeSpan.Zero)
            throw new AgentRunTransitionException(AgentRunTransitionFailure.StaleRevision, "Wait transition is stale.");
        if (command is AgentRunCommand.SuspendWait suspend)
        {
            if (suspend.Wait.Mode == AgentRunWaitMode.Background) ReadyTargets(run, suspend.Wait, children);
            if (!AgentRunToolCallCheckpoint.TryRead(suspend.Value, out var messages)
                || !AgentRunToolCallCheckpoint.PendingCalls(messages!).Any(c => c.Id == suspend.Wait.ToolCallId && c.Name == "execution.wait"))
                throw new ArgumentException("Wait requires its pending tool call checkpoint.");
            return command.Apply(run, Guid.NewGuid);
        }
        if (command is not AgentRunCommand.ResumeWait wake || run.Status != AgentRunStatus.WaitingForSignal || run.Wait is not { } wait
            || !IsReady(run, wake.AtUtc, children))
            throw new AgentRunTransitionException(AgentRunTransitionFailure.NotClaimable, "Wait condition is not ready.");
        var ready = wait.Mode == AgentRunWaitMode.Background ? ReadyTargets(run, wait, children) : [];
        var met = wait.Mode == AgentRunWaitMode.Background && (wait.Until == AgentRunWaitUntil.All ? ready.Count == wait.BackgroundSessionIds.Count : ready.Count > 0);
        var result = System.Text.Json.JsonSerializer.Serialize(new { reason = wait.Mode == AgentRunWaitMode.Duration ? "elapsed" : met ? "condition_met" : "timeout",
            backgroundSessionIds = ready, pendingBackgroundSessionIds = wait.BackgroundSessionIds.Except(ready).ToArray(),
            statuses = wait.BackgroundSessionIds.Select(id => new { backgroundSessionId = id, status = children.Single(c => c.Session.SessionId == id && c.Session.Origin.InitialBackgroundAgentRunId == c.Run.AgentRunId).Run.Status.ToString() }), elapsedSeconds = Math.Max(0, (wake.AtUtc - wait.StartedAtUtc).TotalSeconds) });
        var checkpoint = AgentRunToolCallCheckpoint.AppendWaitResult(run.Checkpoint!, wait.ToolCallId, result);
        return run.ResumeFromSignal(wake.ExpectedRevision, wake.Generation, checkpoint, wake.AtUtc, wake.LeaseExpiresAt);
    }
}
