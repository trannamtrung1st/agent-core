namespace AgentCore.Domain.Conversation;

public enum SandboxVerificationStatus { Running, Succeeded, Failed, Retired, CleanupPending }

// Durable ownership of one disposable verification. Recovery may clean it, never rerun scripts.
public sealed record SandboxVerificationAttempt(Guid AttemptId, Guid TenantId, string ProfileId, long ProfileRevision,
    bool ImageOnly, long Revision, SandboxVerificationStatus Status, DateTimeOffset StartedAt, DateTimeOffset LeaseExpiresAt,
    Guid AccountId, Guid AuthenticationSessionId, Guid PrincipalId, long SecurityRevision, string HostScope,
    Guid? RegistryCredentialId = null, long? RegistryCredentialRevision = null, long? RegistrySecretRevision = null,
    SandboxProfileVerification? Result = null, Guid? CleanupClaim = null, DateTimeOffset? CleanupClaimExpiresAt = null,
    Guid? InstanceId = null, long? InstanceRevision = null)
{
    public bool Active => Status is SandboxVerificationStatus.Running or SandboxVerificationStatus.CleanupPending;
}
