namespace AgentCore.Domain.Workflows;

/// <summary>A qualified human's process-only receipt. It is never an IAM Review or ExceptionalGrant.</summary>
public sealed record WorkflowProcessDecision(Guid TenantId, Guid ExecutionId, string NodeId, Guid OperationId,
    string GraphHash, Guid ReviewerPrincipalId, Guid AccountId, bool Approved, string? Rationale,
    string RequestKey, string Fingerprint, DateTimeOffset At,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ContentRetiredAt = null);
