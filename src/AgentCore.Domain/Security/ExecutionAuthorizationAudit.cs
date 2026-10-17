using AgentCore.Domain.Conversation;

namespace AgentCore.Domain.Security;

public enum ExecutionAuthorizationPurpose { Inspection, ApprovalPreparation, ApprovalDecision, EffectEligibility, ToolBoundary, EffectResult }

// References and observed revisions only. This evidence never authorizes a later operation.
public sealed record ExecutionAuthorizationAudit(TrustedExecutionContext Context, long RunRevision,
    long SecurityRevision, Guid? ClaimGeneration, string? Permission, AccessScope Resource,
    ApprovalKind? ApprovalKind, ExecutionAuthorizationPurpose Purpose, Guid? ApprovalRequestId = null, Guid? OperationId = null, Guid? GrantId = null);
