using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Workspaces;

namespace AgentCore.Infrastructure.Sandbox;

public sealed partial class OpenSandboxService
{
    public string HostScope => SandboxVerificationHost.Fingerprint(options);
    private static string VerificationOwner(Guid id) => id.ToString("N");
    private static bool VerificationMetadata(IReadOnlyDictionary<string, string>? metadata, Guid? tenant, string owner) => metadata is not null
        && metadata.GetValueOrDefault("agent-core-image-verification") == owner
        && metadata.GetValueOrDefault("agent-core-verification-tenant") == tenant?.ToString("N")
        && metadata.GetValueOrDefault("agent-core-filesystem-contract") == SandboxProfileVerification.CurrentContractVersion.ToString();
    private async ValueTask RequireVerificationAsync(SandboxVerificationAttempt attempt, SandboxProfile profile, SandboxImageAuthorization proof, CancellationToken ct)
    {
        if (attempt.TenantId != profile.TenantId || attempt.ProfileId != profile.Id || attempt.ProfileRevision != profile.Revision || attempt.HostScope != HostScope
            || proof.SecurityRevision != attempt.SecurityRevision || proof.ExpectedProfileRevision != attempt.ProfileRevision
            || proof.InstanceId != attempt.InstanceId || proof.ExpectedInstanceRevision != attempt.InstanceRevision
            || proof.Human.AccountId != attempt.AccountId || proof.Human.AuthenticationSessionId != attempt.AuthenticationSessionId
            || proof.Human.PrincipalId != attempt.PrincipalId) throw new AccessDeniedException("verification_unavailable");
        var current = tenantVerifications is null ? null : await tenantVerifications.FindSandboxVerificationAsync(attempt.TenantId, attempt.ProfileId, attempt.AttemptId, ct, attempt.InstanceId);
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
                TenantId: attempt.TenantId, IncludesInitialization: !attempt.ImageOnly, HostScope: attempt.HostScope, InstanceId: attempt.InstanceId, InstanceRevision: attempt.InstanceRevision), true);
        }
        var result = await VerifyProfileCoreAsync(profile, ct, attempt.ImageOnly, attempt.AttemptId,
            token => RequireVerificationAsync(attempt, profile, proof, token), attempt.InstanceId);
        return new(result, result.FailureCode != "cleanup");
    }
    private async ValueTask<bool> CurrentVerificationCleanupAsync(SandboxVerificationCleanup claim, CancellationToken ct)
    {
        var current = tenantVerifications is null ? null : await tenantVerifications.FindSandboxVerificationAsync(claim.TenantId, claim.ProfileId, claim.AttemptId, ct, claim.InstanceId);
        return current?.Revision == claim.Revision && current.Status == SandboxVerificationStatus.CleanupPending && current.CleanupClaim == claim.ClaimId
            && current.CleanupClaimExpiresAt is { } expires && expires > time.GetUtcNow() && current.HostScope == claim.HostScope && HostScope == claim.HostScope;
    }
    private async ValueTask<bool> TryRetireVerificationAsync(Guid tenant, Guid attempt, string expectedHost, CancellationToken ct)
    {
        if (HostScope != expectedHost) return false;
        var owner = new { tenant = tenant.ToString("N"), attempt = attempt.ToString("N") };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.Origin(), "/agent-core/verifications/retire"));
            request.Headers.Add("OPEN-SANDBOX-API-KEY", options.ApiKey); request.Content = System.Net.Http.Json.JsonContent.Create(owner);
            using var response = await RetirementClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return false;
            using var document = await ReadRetirementResponseAsync(response, ct);
            if (document is null || HostScope != expectedHost) return false;
            var value = document.RootElement;
            return value.GetProperty("contractVersion").GetInt32() == 1 && value.GetProperty("retired").GetBoolean()
                && value.GetProperty("tenant").GetString() == owner.tenant && value.GetProperty("attempt").GetString() == owner.attempt;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }
    public async ValueTask<bool> CleanupAsync(SandboxVerificationCleanup claim, CancellationToken ct = default)
    {
        if (!await CurrentVerificationCleanupAsync(claim, ct)) return false;
        if (!await TryRetireVerificationAsync(claim.TenantId, claim.AttemptId, claim.HostScope, ct)) return false;
        if (!await CurrentVerificationCleanupAsync(claim, ct)) return false;
        var path = Path.Combine(options.CanonicalRoot(), ".verification", claim.AttemptId.ToString("N"));
        if (Directory.Exists(path)) CanonicalWorkspaceFilesystem.PurgeOwner(path, ct);
        return true;
    }
}
