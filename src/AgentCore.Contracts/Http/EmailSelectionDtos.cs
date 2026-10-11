namespace AgentCore.Contracts.Http;

public sealed record DecideEmailAccountRequest(Guid RequestId, Guid? AccountId, string? Reference, long ExpectedInstanceRevision);
