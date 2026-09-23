using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Triggers;

namespace AgentCore.Api.Realtime;

public sealed partial class SessionHost
{
    public IReadOnlyList<LiveOccurrenceTarget> ListCompatible(TriggerOwner owner, TriggerSourceKind sourceKind)
    {
        var matches = new List<LiveOccurrenceTarget>();
        foreach (var live in _live.Values)
        {
            var snapshot = live.Runtime.Snapshot;
            if (snapshot.Status != SessionStatus.Attached
                || snapshot.ArchivedAt is not null
                || SessionLifecycle.IsTerminal(snapshot.LifecycleStatus)
                || snapshot.AgentInstanceId != owner.AgentInstanceId
                || snapshot.ProfileId != owner.ProfileId
                || !OccurrenceCompatibility.Allows(snapshot.Definition, sourceKind))
            {
                continue;
            }

            matches.Add(new LiveOccurrenceTarget(snapshot.SessionId));
        }

        return matches;
    }

    public Task<OccurrenceAccept> SubmitAsync(
        Guid sessionId,
        OccurrenceDelivery delivery,
        CancellationToken cancellationToken = default)
    {
        if (!_live.TryGetValue(sessionId, out var live))
        {
            return Task.FromResult(OccurrenceAccept.Unavailable);
        }

        return live.Runtime.SubmitOccurrenceAsync(delivery, cancellationToken);
    }

    public Task BeginAcceptedAsync(
        Guid sessionId,
        OccurrenceDelivery delivery,
        CancellationToken cancellationToken = default)
    {
        if (!_live.TryGetValue(sessionId, out var live))
        {
            return Task.CompletedTask;
        }

        return live.Runtime.BeginAcceptedOccurrenceAsync(delivery, cancellationToken);
    }

    public Task AbandonReservationAsync(
        Guid sessionId,
        Guid occurrenceId,
        CancellationToken cancellationToken = default)
    {
        if (!_live.TryGetValue(sessionId, out var live))
        {
            return Task.CompletedTask;
        }

        return live.Runtime.AbandonOccurrenceReservationAsync(occurrenceId, cancellationToken);
    }
}
