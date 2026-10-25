namespace AgentCore.Contracts.Http;

public sealed record SessionContextSwitchRequest(Guid TransitionId, Guid SpaceId, Guid? EnvironmentId,
    long ExpectedRevision, long ExpectedSessionRevision, long ExpectedSelectionRevision);
