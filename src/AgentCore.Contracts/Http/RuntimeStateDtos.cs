namespace AgentCore.Contracts.Http;

public sealed record RuntimeStateResponse(Guid AgentInstanceId, DateTimeOffset ObservedAtUtc,
    DateTimeOffset FreshThroughUtc, string Availability, SessionStateResponse? Sessions,
    HostedSessionStateResponse? HostedSessions, RunStateResponse? Runs,
    IReadOnlyList<ResourceStateResponse> Resources, IReadOnlyList<RuntimeStateIssueResponse> Issues);
public sealed record SessionStateResponse(int Open, int Paused, int Terminal, DateTimeOffset? LastActivityAtUtc);
public sealed record HostedSessionStateResponse(int Runtimes, int Attached);
public sealed record RunStateResponse(int Queued, int Executing, int WaitingApproval, int WaitingSignal, int Retrying, int Terminal);
public sealed record ResourceStateResponse(string Kind, string Availability, string Sharing,
    DateTimeOffset ObservedAtUtc, int? LiveContexts, int? OpenPages,
    int? ActiveGenerations, int? ActiveCommands, int? CleanupPending, string? Code);
public sealed record RuntimeStateIssueResponse(string Kind, string Code);
