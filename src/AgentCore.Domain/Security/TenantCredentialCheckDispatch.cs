namespace AgentCore.Domain.Security;

// An at-most-once dispatch fence. Provider values and response bodies never enter this record.
public sealed record TenantCredentialCheckDispatch(Guid OperationId, Guid TenantId, Guid RequestId, Guid RunId,
    Guid SessionId, Guid RootActivationId, Guid ExecutorPrincipalId, Guid InstanceId, Guid CredentialId,
    Guid Generation, string ActionHash, long StartedRunRevision, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt = null);
