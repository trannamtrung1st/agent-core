using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private readonly Dictionary<Guid, SandboxVerificationAttempt> _sandboxVerifications = [];
    public ValueTask<SandboxVerificationAttempt?> FindSandboxVerificationAsync(Guid tenant, string profileId, Guid? attemptId = null, CancellationToken ct = default, Guid? instanceId = null)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sandboxBacking.TenantGate)
            return ValueTask.FromResult(sandboxBacking.OwnsTenantProfile(tenant, profileId) ? _sandboxVerifications.Values
                .Where(a => a.TenantId == tenant && a.ProfileId == profileId && a.InstanceId == instanceId && (attemptId is null || a.AttemptId == attemptId))
                .OrderByDescending(a => a.SecurityRevision).FirstOrDefault() : null);
    }
    public ValueTask<SandboxVerificationEvidence> ReadSandboxVerificationAsync(Guid tenant, string profileId, long revision, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sandboxBacking.TenantGate)
        {
            if (sandboxBacking.TenantProfile(tenant, profileId, revision) is null) return ValueTask.FromResult(new SandboxVerificationEvidence(null, false));
            var pending = _sandboxVerifications.Values.Any(a => a.TenantId == tenant && a.ProfileId == profileId && a.InstanceId is null && a.Active);
            return ValueTask.FromResult(new SandboxVerificationEvidence(sandboxBacking.TenantVerification(tenant, profileId, revision), pending));
        }
    }
    public ValueTask<SandboxVerificationEvidence> ReadSandboxInstanceVerificationAsync(Guid tenant, Guid instanceId, long instanceRevision, string profileId, long profileRevision, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (instances.CredentialGate) lock (sandboxBacking.TenantGate)
        {
            if (FindTenantAgentAsync(tenant, instanceId, ct).Result is null || sandboxBacking.TenantProfile(tenant, profileId, profileRevision) is null)
                return ValueTask.FromResult(new SandboxVerificationEvidence(null, false));
            var pending = _sandboxVerifications.Values.Any(a => a.TenantId == tenant && a.InstanceId == instanceId && a.Active);
            var result = _sandboxVerifications.Values.Where(a => a.TenantId == tenant && a.InstanceId == instanceId && a.InstanceRevision == instanceRevision
                && a.ProfileId == profileId && a.ProfileRevision == profileRevision && TenantSandboxVerificationRules.AcceptFullResult(a))
                .OrderByDescending(a => a.SecurityRevision).FirstOrDefault()?.Result;
            return ValueTask.FromResult(new SandboxVerificationEvidence(result, pending));
        }
    }
    public ValueTask<bool> TryBeginSandboxVerificationAsync(TenantSecurityState next, long expectedSecurityRevision,
        SandboxVerificationAttempt attempt, SandboxProfile profile, HumanSessionReference human, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next); TenantSandboxVerificationRules.ValidateStart(attempt, profile, human, expectedSecurityRevision);
        if (next.Tenant.TenantId != attempt.TenantId || next.Tenant.Revision != attempt.SecurityRevision || audit.TenantId != attempt.TenantId || audit.ActorPrincipalId != human.PrincipalId)
            throw new ArgumentException("Invalid verification reservation.");
        lock (_gate) lock (instances.CredentialGate) lock (sandboxBacking.TenantGate)
        {
            if (!_tenants.TryGetValue(attempt.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult(false);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); EnsureReferences(next, audit, ct: ct);
            var instance = attempt.InstanceId is { } id ? FindTenantAgentAsync(attempt.TenantId, id, ct).Result?.Instance : null;
            if (attempt.InstanceId is { } owned && !before.SystemIdentities.Any(i => i.AgentInstanceId == owned && i.Status == IdentityStatus.Active))
                throw new AccessDeniedException("resource_unavailable");
            TenantSandboxVerificationRules.RequireSetup(attempt, profile, sandboxBacking.TenantProfile(attempt.TenantId, profile.Id), instance);
            if (_sandboxVerifications.Values.Any(a => a.TenantId == attempt.TenantId && a.InstanceId == attempt.InstanceId && (attempt.InstanceId is not null || a.ProfileId == profile.Id) && a.Active))
                throw new SecurityConflictException("verification_in_progress");
            if (profile.RegistryCredentialId is { } credential) TenantSandboxProfileRules.RequireCredential(profile, credentials?.ReadTenantCredential(attempt.TenantId, credential)?.Value, (clock ?? TimeProvider.System).GetUtcNow());
            RequireAgentHuman(attempt.TenantId, audit.ActorPrincipalId, human); ct.ThrowIfCancellationRequested();
            if (!_sandboxVerifications.TryAdd(attempt.AttemptId, attempt)) return ValueTask.FromResult(false);
            _tenants[attempt.TenantId] = next; _audit.Add(audit); return ValueTask.FromResult(true);
        }
    }
    public ValueTask<SandboxVerificationAttempt?> FinishSandboxVerificationAsync(Guid tenant, Guid attemptId, long expectedRevision,
        SandboxVerificationOutcome outcome, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (instances.CredentialGate) lock (sandboxBacking.TenantGate)
        {
            if (!_sandboxVerifications.TryGetValue(attemptId, out var current) || current.TenantId != tenant || current.Revision != expectedRevision || current.Status != SandboxVerificationStatus.Running)
                return ValueTask.FromResult<SandboxVerificationAttempt?>(null);
            if (audit.TenantId != tenant || audit.ActorPrincipalId != current.PrincipalId) throw new ArgumentException("Invalid verification settlement.");
            var state = _tenants[tenant]; var allowed = current.LeaseExpiresAt > at && state.Tenant.Revision == current.SecurityRevision && sandboxBacking.TenantProfile(tenant, current.ProfileId)?.Revision == current.ProfileRevision;
            if (current.InstanceId is { } id)
            {
                var instance = FindTenantAgentAsync(tenant, id, ct).Result?.Instance;
                allowed &= instance is not null && instance.Revision == current.InstanceRevision && instance.Lifecycle == AgentInstanceLifecycle.Active
                    && state.SystemIdentities.Any(i => i.AgentInstanceId == id && i.Status == IdentityStatus.Active);
            }
            try { RequireAgentHuman(tenant, current.PrincipalId, TenantSandboxVerificationRules.Human(current)); }
            catch (AccessDeniedException) { allowed = false; }
            if (allowed && current.RegistryCredentialId is { } credential)
            {
                var profile = sandboxBacking.TenantProfile(tenant, current.ProfileId)! with { RegistryCredentialRevision = current.RegistryCredentialRevision, RegistrySecretRevision = current.RegistrySecretRevision };
                try { TenantSandboxProfileRules.RequireCredential(profile, credentials?.ReadTenantCredential(tenant, credential)?.Value, at); }
                catch (SecurityConflictException) { allowed = false; }
            }
            var settled = TenantSandboxVerificationRules.Complete(current, outcome, allowed, at);
            _sandboxVerifications[attemptId] = settled;
            if (TenantSandboxVerificationRules.AcceptObservation(settled)) sandboxBacking.SaveTenantVerification(settled.Result!);
            _tenants[tenant] = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
            _audit.Add(audit with { Decision = settled.Status.ToString(), ReasonCode = "sandbox_verification_settled" });
            return ValueTask.FromResult<SandboxVerificationAttempt?>(settled);
        }
    }
    public ValueTask<IReadOnlyList<SandboxVerificationCleanup>> ClaimSandboxVerificationCleanupAsync(DateTimeOffset at, Guid claimId, int limit = 10, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); if (claimId == Guid.Empty || limit is < 1 or > 100) throw new ArgumentException("Invalid cleanup claim.");
        lock (_gate)
        {
            var claims = new List<SandboxVerificationCleanup>();
            foreach (var a in _sandboxVerifications.Values.Where(a => TenantSandboxVerificationRules.CleanupDue(a, at)).OrderBy(a => a.LeaseExpiresAt).Take(limit).ToArray())
            {
                var next = a with { Revision = checked(a.Revision + 1), Status = SandboxVerificationStatus.CleanupPending, CleanupClaim = claimId, CleanupClaimExpiresAt = at.AddSeconds(45) };
                _sandboxVerifications[a.AttemptId] = next; claims.Add(new(a.TenantId, a.ProfileId, a.AttemptId, next.Revision, a.HostScope, claimId, a.InstanceId));
            }
            return ValueTask.FromResult<IReadOnlyList<SandboxVerificationCleanup>>(claims);
        }
    }
    public ValueTask<bool> FinishSandboxVerificationCleanupAsync(SandboxVerificationCleanup claim, bool confirmed, DateTimeOffset at, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate)
        {
            var a = _sandboxVerifications.GetValueOrDefault(claim.AttemptId);
            if (a?.TenantId != claim.TenantId || a.ProfileId != claim.ProfileId || a.InstanceId != claim.InstanceId || a.HostScope != claim.HostScope || a.Revision != claim.Revision || a.Status != SandboxVerificationStatus.CleanupPending || a.CleanupClaim != claim.ClaimId || a.CleanupClaimExpiresAt is not { } expires || expires <= at) return ValueTask.FromResult(false);
            _sandboxVerifications[a.AttemptId] = a with { Revision = checked(a.Revision + 1), Status = confirmed ? SandboxVerificationStatus.Retired : SandboxVerificationStatus.CleanupPending,
                CleanupClaim = null, CleanupClaimExpiresAt = null, LeaseExpiresAt = at.AddSeconds(15) };
            return ValueTask.FromResult(true);
        }
    }
}
