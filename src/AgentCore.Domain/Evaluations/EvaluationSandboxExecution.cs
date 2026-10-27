using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Domain.Evaluations;

public sealed record EvaluationHumanWitness(Guid AccountId, Guid AuthenticationSessionId, Guid PrincipalId);

public enum EvaluationSandboxPhase { AwaitingReview, Reserved, Dispatching, Settled }
public sealed record SandboxEvaluationReference(Guid AttemptId, Guid ExecutionId, string CheckId, ScriptReference Script, string InputHash);

// A separate, durable execution admission. Subject Run/Session IDs are read witnesses, never live claims.
// Inputs are removed after settlement. Recovery may retire a reserved environment, never dispatch it again.
public sealed record EvaluationSandboxExecution(Guid TenantId, Guid ExecutionId, Guid AttemptId, string CheckId,
    Guid ActorId, Guid ExecutorId, Guid SubjectRunId, long SubjectRevision, Guid SubjectSessionId, Guid InstanceId,
    ScriptReference Script, SandboxEvaluationInput InputKind, SandboxProfile Profile, string? InputJson, string InputHash,
    TenantApprovalRequest Approval, EvaluationHumanWitness? Human, string HostScope,
    EvaluationSandboxPhase Phase, long Revision, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    Guid? DispatchClaim = null, DateTimeOffset? DispatchExpiresAt = null, string? ProviderId = null,
    bool CleanupPending = false, int CleanupPasses = 0, DateTimeOffset? RetryCleanupAt = null,
    EvaluationOutcome? Outcome = null, string? ReasonCode = null, int? ExitCode = null, long? DurationMs = null,
    string? OutputHash = null, bool TerminationConfirmed = false, bool FindingPublished = false, DateTimeOffset? CancellationRequestedAt = null);
