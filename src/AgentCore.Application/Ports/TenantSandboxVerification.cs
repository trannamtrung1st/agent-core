using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record SandboxVerificationOutcome(SandboxProfileVerification Result, bool CleanupConfirmed);
public sealed record SandboxVerificationEvidence(SandboxProfileVerification? Observation, bool Pending);
public sealed record SandboxVerificationCleanup(Guid TenantId, string ProfileId, Guid AttemptId, long Revision, string HostScope, Guid ClaimId, Guid? InstanceId = null);

public interface ISandboxVerificationHost
{
    string Scope { get; }
}

public interface ITenantSandboxVerifier
{
    string HostScope { get; }
    ValueTask<SandboxVerificationOutcome> VerifyAsync(SandboxVerificationAttempt attempt, SandboxProfile profile,
        SandboxImageAuthorization proof, CancellationToken ct = default);
    ValueTask<bool> CleanupAsync(SandboxVerificationCleanup claim, CancellationToken ct = default);
}

public interface ITenantSandboxVerificationStore
{
    ValueTask<SandboxVerificationAttempt?> FindSandboxVerificationAsync(Guid tenant, string profileId, Guid? attemptId = null, CancellationToken ct = default, Guid? instanceId = null);
    ValueTask<SandboxVerificationEvidence> ReadSandboxVerificationAsync(Guid tenant, string profileId, long revision, CancellationToken ct = default);
    ValueTask<SandboxVerificationEvidence> ReadSandboxInstanceVerificationAsync(Guid tenant, Guid instanceId, long instanceRevision, string profileId, long profileRevision, CancellationToken ct = default);
    ValueTask<bool> TryBeginSandboxVerificationAsync(TenantSecurityState next, long expectedSecurityRevision,
        SandboxVerificationAttempt attempt, SandboxProfile profile, HumanSessionReference human, AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<SandboxVerificationAttempt?> FinishSandboxVerificationAsync(Guid tenant, Guid attemptId, long expectedRevision,
        SandboxVerificationOutcome outcome, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default);
    // Technical cleanup inventory only: no private configuration or output projection to the worker.
    ValueTask<IReadOnlyList<SandboxVerificationCleanup>> ClaimSandboxVerificationCleanupAsync(DateTimeOffset at, Guid claimId, int limit = 10, CancellationToken ct = default);
    ValueTask<bool> FinishSandboxVerificationCleanupAsync(SandboxVerificationCleanup claim, bool confirmed, DateTimeOffset at, CancellationToken ct = default);
}
