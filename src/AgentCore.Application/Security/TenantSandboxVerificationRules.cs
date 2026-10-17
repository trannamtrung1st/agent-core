using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Security;

public static class TenantSandboxVerificationRules
{
    public static readonly TimeSpan MaximumLease = TimeSpan.FromMinutes(10);
    public static void ValidateStart(SandboxVerificationAttempt attempt, SandboxProfile profile, HumanSessionReference human, long expectedSecurityRevision)
    {
        profile.Validate();
        if (attempt.AttemptId == Guid.Empty || profile.TenantId != attempt.TenantId || attempt.TenantId == Guid.Empty
            || !TenantSandboxProfileRules.ValidId(profile.Id) || attempt.ProfileId != profile.Id || attempt.ProfileRevision != profile.Revision
            || attempt.Revision != 1 || attempt.Status != SandboxVerificationStatus.Running || attempt.Result is not null || attempt.CleanupClaim is not null
            || attempt.CleanupClaimExpiresAt is not null || attempt.LeaseExpiresAt != attempt.StartedAt + MaximumLease
            || attempt.AccountId != human.AccountId || attempt.AuthenticationSessionId != human.AuthenticationSessionId || attempt.PrincipalId != human.PrincipalId
            || attempt.SecurityRevision != checked(expectedSecurityRevision + 1) || attempt.HostScope.Length != 64 || attempt.HostScope.Any(c => !char.IsAsciiHexDigit(c))
            || attempt.RegistryCredentialId != profile.RegistryCredentialId || attempt.RegistryCredentialRevision != profile.RegistryCredentialRevision || attempt.RegistrySecretRevision != profile.RegistrySecretRevision)
            throw new ArgumentException("Invalid disposable verification ownership.");
    }
    public static void ValidateOutcome(SandboxVerificationAttempt attempt, SandboxVerificationOutcome outcome)
    {
        var r = outcome.Result;
        if (r.HostScope != attempt.HostScope || r.TenantId != attempt.TenantId || r.ProfileId != attempt.ProfileId || r.ProfileRevision != attempt.ProfileRevision
            || r.RegistryCredentialId != attempt.RegistryCredentialId || r.RegistryCredentialRevision != attempt.RegistryCredentialRevision
            || r.RegistrySecretRevision != attempt.RegistrySecretRevision || r.IncludesInitialization == attempt.ImageOnly
            || r.ContractVersion != SandboxProfileVerification.CurrentContractVersion || r.VerifiedAt < attempt.StartedAt
            || r.Message is null || r.Message.Length > 2000 || r.Stdout is null || r.Stderr is null
            || Encoding.UTF8.GetByteCount(r.Stdout) > 32768 || Encoding.UTF8.GetByteCount(r.Stderr) > 32768 || r.DurationMs < 0)
            throw new ArgumentException("Verification evidence does not match its saved attempt.");
    }
    public static HumanSessionReference Human(SandboxVerificationAttempt a) => new(a.AccountId, a.AuthenticationSessionId, a.PrincipalId);
    public static void RequireCurrent(SandboxProfile profile, SandboxProfile? current)
    {
        if (current is null || profile != (current with { RegistryCredentialRevision = profile.RegistryCredentialRevision, RegistrySecretRevision = profile.RegistrySecretRevision }))
            throw new SecurityConflictException("stale_revision");
    }
    public static SandboxVerificationAttempt Complete(SandboxVerificationAttempt current, SandboxVerificationOutcome outcome, bool authority, DateTimeOffset at)
    {
        ValidateOutcome(current, outcome);
        return current with { Revision = checked(current.Revision + 1), Result = outcome.Result,
            Status = !outcome.CleanupConfirmed ? SandboxVerificationStatus.CleanupPending : !authority ? SandboxVerificationStatus.Retired
                : outcome.Result.Passed ? SandboxVerificationStatus.Succeeded : SandboxVerificationStatus.Failed,
            LeaseExpiresAt = at, CleanupClaim = null, CleanupClaimExpiresAt = null };
    }
    public static bool AcceptObservation(SandboxVerificationAttempt a) => !a.ImageOnly && a.Status is SandboxVerificationStatus.Succeeded or SandboxVerificationStatus.Failed;
    public static bool CleanupDue(SandboxVerificationAttempt a, DateTimeOffset at) => a.Active && a.LeaseExpiresAt <= at && (a.CleanupClaimExpiresAt is null || a.CleanupClaimExpiresAt <= at);
}
