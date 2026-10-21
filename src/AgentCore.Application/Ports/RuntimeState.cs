using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Ports;

// Observations never authorize resource access and never contain resource handles or content.
public sealed record DurableSessionState(int Open, int Paused, int Terminal, DateTimeOffset? LastActivityAtUtc);
public sealed record DurableRunState(int Queued, int Executing, int WaitingApproval, int WaitingSignal, int Retrying, int Terminal);
public sealed record HostedSessionState(int Runtimes, int Attached);
public sealed record ResourceStateObservation(string Kind, string Availability, string Sharing,
    DateTimeOffset ObservedAtUtc, int? LiveContexts = null, int? OpenPages = null,
    int? ActiveGenerations = null, int? ActiveCommands = null, int? CleanupPending = null,
    string? Code = null);

public interface IBrowserStateReader
{
    ValueTask<ResourceStateObservation> ObserveAsync(Guid trustedInstanceId, CancellationToken ct = default);
    ValueTask<ResourceStateObservation> ObserveSessionAsync(Guid trustedInstanceId, Guid trustedSessionId, CancellationToken ct = default) => throw new NotSupportedException();
}
