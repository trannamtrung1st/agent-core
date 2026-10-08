using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Execution;

public static class AgentRunWaitExecution
{
    private static IReadOnlyList<BackgroundCompletionCandidate> OwnedChildren(AgentRun run, IReadOnlyList<BackgroundCompletionCandidate> children) =>
        children.Where(c => c.Run.Owner == run.Owner && c.Session.DurablyDeletedAt is null
            && c.Session.Origin.OriginatingSessionId == run.SessionId
            && c.Session.Origin.InitialBackgroundAgentRunId == c.Run.AgentRunId).ToArray();

    public static IReadOnlyList<Guid> ReadyTargets(AgentRun run, AgentRunWait wait, IReadOnlyList<BackgroundCompletionCandidate> children)
    {
        var owned = OwnedChildren(run, children);
        if (wait.BackgroundSessionIds.Any(id => id == run.SessionId || !owned.Any(c => c.Session.SessionId == id)))
            throw new ArgumentException("Wait targets must be initial background children owned by this parent Session.");
        return wait.BackgroundSessionIds.Where(id => owned.Any(c => c.Session.SessionId == id && c.Run.IsTerminal)).ToArray();
    }

    public static bool IsReady(AgentRun run, DateTimeOffset now, IReadOnlyList<BackgroundCompletionCandidate> children)
    {
        if (run.Wait is not { } wait) return false;
        if (wait.DeadlineUtc <= now) return true;
        if (wait.Mode == AgentRunWaitMode.Duration) return false;
        var owned = OwnedChildren(run, children);
        // Admission already validated targets. Lost targets must wake, not throw on every scan.
        if (wait.BackgroundSessionIds.Any(id => !owned.Any(c => c.Session.SessionId == id))) return true;
        var ready = wait.BackgroundSessionIds.Where(id => owned.Any(c => c.Session.SessionId == id && c.Run.IsTerminal)).ToArray();
        return wait.Until == AgentRunWaitUntil.All ? ready.Length == wait.BackgroundSessionIds.Count : ready.Length > 0;
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
        var owned = OwnedChildren(run, children);
        var ready = wait.BackgroundSessionIds.Where(id => owned.Any(c => c.Session.SessionId == id && c.Run.IsTerminal)).ToArray();
        var unavailable = wait.BackgroundSessionIds.Where(id => !owned.Any(c => c.Session.SessionId == id)).ToArray();
        var met = wait.Mode == AgentRunWaitMode.Background && (wait.Until == AgentRunWaitUntil.All ? ready.Length == wait.BackgroundSessionIds.Count : ready.Length > 0);
        var result = System.Text.Json.JsonSerializer.Serialize(new { reason = wait.Mode == AgentRunWaitMode.Duration ? "elapsed" : met ? "condition_met" : unavailable.Length > 0 ? "unavailable" : "timeout",
            backgroundSessionIds = ready, pendingBackgroundSessionIds = wait.BackgroundSessionIds.Except(ready).Except(unavailable).ToArray(),
            unavailableBackgroundSessionIds = unavailable,
            statuses = wait.BackgroundSessionIds.Select(id => new { backgroundSessionId = id, status = owned.SingleOrDefault(c => c.Session.SessionId == id)?.Run.Status.ToString() ?? "Unavailable" }), elapsedSeconds = Math.Max(0, (wake.AtUtc - wait.StartedAtUtc).TotalSeconds) });
        var checkpoint = AgentRunToolCallCheckpoint.AppendWaitResult(run.Checkpoint!, wait.ToolCallId, result);
        return run.ResumeFromSignal(wake.ExpectedRevision, wake.Generation, checkpoint, wake.AtUtc, wake.LeaseExpiresAt);
    }
}
