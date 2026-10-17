using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed class TenantSandboxOperationTarget(ITenantSessionStore sessions, ITenantSandboxProfileStore profiles, ITenantSandboxRuntimeStore runtime,
    TenantRunConfigurationResolver configuration, IamAuthorizer authorizer, IIamStore security, ISandboxImageAuthority images, ISandboxReadiness readiness, TimeProvider time)
{
    public async ValueTask<(TenantAgent Agent, TenantSession Session, AgentCore.Domain.Definitions.AgentDefinition Baseline,
        AgentCore.Domain.Definitions.SandboxProfile Profile, SandboxLease? Lease)> ResolveAsync(AgentRun run, string profileId, CancellationToken ct)
    {
        var tenant = run.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var (agent, baseline) = await configuration.BaselineAsync(tenant, run.AgentInstanceId, ct).ConfigureAwait(false);
        var session = await sessions.FindTenantSessionAsync(tenant, run.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var profile = TenantSandboxOperationRules.Profile(run, profileId);
        var source = await profiles.FindTenantSandboxProfileAsync(tenant, profileId, profile.Revision, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        if ((await profiles.FindTenantSandboxProfileAsync(tenant, profileId, ct: ct).ConfigureAwait(false))?.Enabled != true) throw new AccessDeniedException("resource_unavailable");
        var state = await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        await TenantSandboxRuntimeRules.RequirePermissionsAsync(state, run, profile, authorizer, ct).ConfigureAwait(false);
        var available = await images.InspectAsync(profile, ct).ConfigureAwait(false);
        if (!available.Eligible || readiness.State != "Ready" || available.CredentialRevision != profile.RegistryCredentialRevision || available.SecretRevision != profile.RegistrySecretRevision)
            throw new AccessDeniedException("sandbox_source_unavailable");
        var saved = await runtime.ReadTenantSandboxRuntimeAsync(tenant, run.SessionId, ct: ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        return (agent, session, baseline, source, saved.Lease);
    }
    public async ValueTask RequireAsync(TenantApprovalRequest request, AgentRun run, CancellationToken ct, SandboxExecution? reserved = null)
    {
        var value = request.Operation.SandboxCommand ?? throw new ArgumentException("Sandbox operation required.");
        var target = await ResolveAsync(run, value.ProfileId, ct).ConfigureAwait(false);
        if (target.Agent.Instance.Revision != request.Operation.ExpectedInstanceRevision
            || target.Agent.Instance.PersonaRevision != request.Operation.ExpectedPersonaRevision)
            throw new SecurityConflictException("sandbox_operation_witness_changed");
        TenantSandboxOperationRules.Require(value, run, target.Agent, target.Session, target.Baseline, target.Profile, target.Lease, time.GetUtcNow(), reserved);
        if (request.Snapshot != TenantSandboxOperationRules.Snapshot(target.Agent, TenantSandboxOperationRules.Profile(run, value.ProfileId), target.Lease, reserved?.InitializesGeneration))
            throw new SecurityConflictException("sandbox_operation_witness_changed");
    }
}
