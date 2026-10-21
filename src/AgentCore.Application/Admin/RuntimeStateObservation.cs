using AgentCore.Application.Ports;

namespace AgentCore.Application.Admin;

public sealed record RuntimeStateIssue(string Kind, string Code);
public sealed record RuntimeStateObservation(Guid AgentInstanceId, DateTimeOffset ObservedAtUtc,
    DateTimeOffset FreshThroughUtc, string Availability, DurableSessionState? Sessions,
    HostedSessionState? HostedSessions, DurableRunState? Runs,
    IReadOnlyList<ResourceStateObservation> Resources, IReadOnlyList<RuntimeStateIssue> Issues, string Scope = "adminInstance");
