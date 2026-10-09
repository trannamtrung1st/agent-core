using AgentCore.Application.Execution;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private async ValueTask<string?> PreviousExecutionFactsAsync(AgentRun current, CancellationToken ct)
    {
        // Cursor is owner + Session scoped and starts strictly before this Run, including recovery.
        var page = await _agentRuns.ListPageAsync(current.Owner, current.SessionId, current.AgentRunId, 1, ct).ConfigureAwait(false);
        var previous = page.Items.FirstOrDefault();
        if (previous is null || !previous.IsTerminal) return null;
        var entry = _snapshot.Entries.LastOrDefault(e => e.ResponseId == previous.ResponseId);
        return RunExecutionFacts.Previous(previous, entry?.InterruptReason, entry?.Failure);
    }
}
