using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Api.Realtime;

public sealed partial class SessionHost : IHostedSessionStateReader
{
    public HostedSessionState ObserveSession(AgentRunOwner owner, Guid sessionId)
    {
        lock (_gate)
        {
            if (!_live.TryGetValue(sessionId, out var live) || live.Runtime.Snapshot.AgentInstanceId != owner.AgentInstanceId
                || live.Runtime.Snapshot.ProfileId != owner.ProfileId || live.Runtime.Snapshot.DurablyDeletedAt is not null
                || live.Runtime.Snapshot.ArchivedAt is not null) return new(0, 0);
            return new(1, live.ConnectionId is null ? 0 : 1);
        }
    }

    public HostedSessionState Observe(AgentRunOwner owner)
    {
        lock (_gate)
        {
            var hosted = _live.Values.Where(l => l.Runtime.Snapshot.AgentInstanceId == owner.AgentInstanceId
                && l.Runtime.Snapshot.ProfileId == owner.ProfileId && l.Runtime.Snapshot.DurablyDeletedAt is null
                && l.Runtime.Snapshot.ArchivedAt is null).ToArray();
            return new(hosted.Length, hosted.Count(l => l.ConnectionId is not null));
        }
    }
}
