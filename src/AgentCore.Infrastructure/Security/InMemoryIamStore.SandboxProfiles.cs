using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<SandboxProfile?> FindTenantSandboxProfileAsync(Guid tenant, string id, long? revision = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sandboxBacking.TenantGate)
            return ValueTask.FromResult(sandboxBacking.TenantProfile(tenant, id, revision));
    }
    public ValueTask<TenantSandboxProfilePage> ListTenantSandboxProfilesAsync(Guid tenant, IReadOnlyList<string>? allowedIds,
        int page, int pageSize, string search, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); TenantSandboxProfileRules.Query(tenant, page, pageSize, search);
        lock (_gate) lock (sandboxBacking.TenantGate) return ValueTask.FromResult(sandboxBacking.TenantProfiles(tenant, allowedIds, page, pageSize, search));
    }
    public ValueTask<bool> TrySaveTenantSandboxProfileAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantSandboxProfileRoot root, SandboxProfile profile, long expectedProfileRevision,
        HumanSessionReference human, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next); TenantSandboxProfileRules.Validate(root, profile, expectedProfileRevision);
        if (root.TenantId != next.Tenant.TenantId || audit.TenantId != root.TenantId || audit.ActorPrincipalId != human.PrincipalId
            || next.Tenant.Revision != checked(expectedSecurityRevision + 1)) throw new ArgumentException("Invalid Sandbox profile transaction.");
        lock (_gate) lock (instances.CredentialGate) lock (sandboxBacking.TenantGate)
        {
            if (!_tenants.TryGetValue(root.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult(false);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); ValidateAgentActor(before, human.PrincipalId); EnsureReferences(next, audit, ct: ct);
            var old = sandboxBacking.TenantProfile(root.TenantId, profile.Id);
            var disabling = TenantSandboxProfileRules.DisablingUnchangedSource(old, profile);
            if (!disabling && profile.RegistryCredentialId is { } credential)
                TenantSandboxProfileRules.RequireCredential(profile, credentials?.ReadTenantCredential(root.TenantId, credential)?.Value, (clock ?? TimeProvider.System).GetUtcNow());
            RequireAgentHuman(root.TenantId, audit.ActorPrincipalId, human); ct.ThrowIfCancellationRequested();
            if (!sandboxBacking.SaveTenantProfile(root, profile, expectedProfileRevision)) return ValueTask.FromResult(false);
            _tenants[root.TenantId] = next; _audit.Add(audit); return ValueTask.FromResult(true);
        }
    }
}
