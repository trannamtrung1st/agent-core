using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Testing;

public static class PausedSessionReopen
{
    public static async Task<SessionSnapshot> ReopenAsync(
        IMemoryStore store,
        SessionSnapshot snapshot,
        TimeProvider time,
        CancellationToken cancellationToken = default)
    {
        if (snapshot.Status != SessionStatus.Paused)
        {
            return snapshot;
        }

        var now = time.GetUtcNow();
        var reopened = snapshot with
        {
            Status = SessionStatus.Created,
            PauseReason = null,
            RuntimeEpoch = snapshot.RuntimeEpoch + 1,
            LastUserActivityAt = now,
            UpdatedAt = now,
            Revision = snapshot.Revision + 1
        };
        await store.SaveAsync(reopened, snapshot.Revision, cancellationToken).ConfigureAwait(false);
        return (await store.LoadAsync(snapshot.SessionId, cancellationToken).ConfigureAwait(false))!;
    }
}
