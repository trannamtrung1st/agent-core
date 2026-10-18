using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<SandboxLease?> TryRequestTenantSandboxProfileSwitchAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantSandboxProfileSwitchPreparation selection, HumanSessionReference human, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next); var tenant = next.Tenant.TenantId; var c = selection.Change;
        TenantSandboxControlRules.SelectionAudit(tenant, c, human, audit);
        if (next.Tenant.Revision != checked(expectedSecurityRevision + 1)) throw new ArgumentException("Invalid profile selection transaction.");
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate) lock (sandboxBacking.TenantGate)
        {
            if (!_tenants.TryGetValue(tenant, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult<SandboxLease?>(null);
            RequireAgentHuman(tenant, human.PrincipalId, human);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); ValidateAgentActor(before, human.PrincipalId); EnsureReferences(next, audit, ct: ct);
            var session = sessionBacking.ReadTenantSession(tenant, c.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
            var lease = sandboxBacking.TenantLease(tenant, c.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
            var execution = (lease.ActiveExecutionId ?? lease.LastExecutionId) is { } id ? sandboxBacking.TenantExecution(tenant, c.SessionId, id) : null;
            foreach (var profile in new[] { lease.Profile.Id, c.ProfileId }.Distinct(StringComparer.Ordinal))
                TenantSandboxControlRules.RequirePermissionsAsync(before, session.Owner, profile, human, new IamAuthorizer(this), ct).GetAwaiter().GetResult();
            var agent = FindTenantAgentAsync(tenant, session.Owner.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("resource_unavailable");
            TenantSessionRules.RequireExecutor(before, agent); TenantSandboxControlRules.RequireSelection(selection, session, lease, execution, agent);
            var baseline = Guid.TryParse(agent.Instance.DefinitionId, out var definition)
                ? definitions.TenantPublication(tenant, definition, agent.Instance.ActiveVersion)?.Payload
                : builtIns?.GetAsync(agent.Instance.DefinitionId, agent.Instance.ActiveVersion, ct).GetAwaiter().GetResult();
            var source = sandboxBacking.TenantProfile(tenant, c.ProfileId) ?? throw new AccessDeniedException("resource_unavailable");
            if (baseline is null) throw new AccessDeniedException("definition_unavailable");
            if (source.RegistryCredentialId is { } credential) TenantSandboxProfileRules.RequireCredential(selection.Target, credentials?.ReadTenantCredential(tenant, credential)?.Value, (clock ?? TimeProvider.System).GetUtcNow());
            var evidence = ReadSandboxVerificationAsync(tenant, c.ProfileId, source.Revision, ct).Result;
            TenantSandboxControlRules.RequireSelectionSource(selection, agent, baseline, source, evidence);
            var requested = TenantSandboxControlRules.ApplySelection(lease, selection, audit);
            ct.ThrowIfCancellationRequested(); sandboxBacking.SaveTenantLease(requested); _tenants[tenant] = next; _audit.Add(audit);
            return ValueTask.FromResult<SandboxLease?>(requested);
        }
    }
}
