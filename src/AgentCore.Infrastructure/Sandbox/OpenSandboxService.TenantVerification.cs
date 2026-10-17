using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Workspaces;
using OpenSandbox;
using OpenSandbox.Models;

namespace AgentCore.Infrastructure.Sandbox;

public sealed partial class OpenSandboxService
{
    public string HostScope => SandboxVerificationHost.Fingerprint(options);
    private static string VerificationOwner(Guid? tenant, Guid id) => tenant is { } owner ? owner.ToString("N") + "." + id.ToString("N") : id.ToString("N");
    private static bool VerificationMetadata(IReadOnlyDictionary<string, string>? metadata, string owner) => metadata is not null
        && metadata.GetValueOrDefault("agent-core-image-verification") == owner
        && metadata.GetValueOrDefault("agent-core-filesystem-contract") == SandboxProfileVerification.CurrentContractVersion.ToString();
    private async ValueTask RequireVerificationAsync(SandboxVerificationAttempt attempt, SandboxProfile profile, SandboxImageAuthorization proof, CancellationToken ct)
    {
        if (attempt.TenantId != profile.TenantId || attempt.ProfileId != profile.Id || attempt.ProfileRevision != profile.Revision || attempt.HostScope != HostScope
            || proof.SecurityRevision != attempt.SecurityRevision || proof.ExpectedProfileRevision != attempt.ProfileRevision
            || proof.Human != TenantSandboxVerificationRules.Human(attempt)) throw new AccessDeniedException("verification_unavailable");
        var current = tenantVerifications is null ? null : await tenantVerifications.FindSandboxVerificationAsync(attempt.TenantId, attempt.ProfileId, attempt.AttemptId, ct);
        if (current != attempt || current.Status != SandboxVerificationStatus.Running || current.LeaseExpiresAt <= time.GetUtcNow()) throw new AccessDeniedException("verification_unavailable");
        await (verificationAuthority ?? throw new AccessDeniedException("verification_unavailable")).RequireAsync(profile, proof, ct);
    }
    public async ValueTask<SandboxVerificationOutcome> VerifyAsync(SandboxVerificationAttempt attempt, SandboxProfile profile, SandboxImageAuthorization proof, CancellationToken ct = default)
    {
        // An unclaimed/forged invocation cannot create directories or contact the private server.
        try { await RequireVerificationAsync(attempt, profile, proof, ct); }
        catch (Exception error) when (error is AccessDeniedException or SecurityConflictException or OperationCanceledException)
        {
            return new(new(profile.Id, profile.Revision, time.GetUtcNow(), false, "Verification authority was unavailable before dispatch.", "authority_changed",
                ContractVersion: SandboxProfileVerification.CurrentContractVersion, RegistryCredentialId: attempt.RegistryCredentialId,
                RegistryCredentialRevision: attempt.RegistryCredentialRevision, RegistrySecretRevision: attempt.RegistrySecretRevision,
                TenantId: attempt.TenantId, IncludesInitialization: !attempt.ImageOnly, HostScope: attempt.HostScope), true);
        }
        var result = await VerifyProfileCoreAsync(profile, ct, attempt.ImageOnly, attempt.AttemptId,
            token => RequireVerificationAsync(attempt, profile, proof, token));
        return new(result, result.FailureCode != "cleanup");
    }
    public async ValueTask<bool> CleanupAsync(SandboxVerificationCleanup claim, CancellationToken ct = default)
    {
        var current = tenantVerifications is null ? null : await tenantVerifications.FindSandboxVerificationAsync(claim.TenantId, claim.ProfileId, claim.AttemptId, ct);
        if (current?.Revision != claim.Revision || current.Status != SandboxVerificationStatus.CleanupPending || current.CleanupClaim != claim.ClaimId
            || current.CleanupClaimExpiresAt is not { } expires || expires <= time.GetUtcNow() || current.HostScope != claim.HostScope || HostScope != claim.HostScope) return false;
        var owner = VerificationOwner(claim.TenantId, claim.AttemptId);
        await using var manager = SandboxManager.Create(new SandboxManagerOptions { ConnectionConfig = Connection() });
        var candidates = await manager.ListSandboxInfosAsync(new SandboxFilter { Metadata = new Dictionary<string, string> {
            ["agent-core-image-verification"] = owner, ["agent-core-filesystem-contract"] = SandboxProfileVerification.CurrentContractVersion.ToString() }, PageSize = 100 }, ct);
        foreach (var candidate in candidates.Items)
        {
            if (!VerificationMetadata(candidate.Metadata, owner)) return false;
            await manager.KillSandboxAsync(candidate.Id, ct);
        }
        if (candidates.Items.Count >= 100) return false;
        var path = Path.Combine(options.CanonicalRoot(), ".verification", claim.AttemptId.ToString("N"));
        if (Directory.Exists(path)) CanonicalWorkspaceFilesystem.PurgeOwner(path, ct);
        return true;
    }
}
