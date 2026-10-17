namespace AgentCore.Contracts.Http;

public sealed record TenantAuditEntryDto(Guid AuditId, Guid? TenantId, Guid ActorPrincipalId, string Action,
    string Target, string Decision, string ReasonCode, DateTimeOffset At, ExecutionAuthorizationAuditDto? Execution);

public sealed record ExecutionAuthorizationAuditDto(ExecutionAuditContextDto Context, long RunRevision,
    long SecurityRevision, Guid? ClaimGeneration, string? Permission, ExecutionAuditResourceDto Resource,
    string? ApprovalKind, string Purpose, Guid? ApprovalRequestId, Guid? OperationId, Guid? GrantId);

public sealed record ExecutionAuditContextDto(Guid TenantId, Guid AgentPrincipalId, Guid? OriginalInitiatorPrincipalId,
    Guid SessionId, Guid RunId, Guid ActivationId, Guid RootActivationId, Guid? ParentRunId,
    Guid? ParentActivationId, string Origin);

public sealed record ExecutionAuditResourceDto(string Kind, Guid? TenantId, string? ResourceType, string? ResourceId);
