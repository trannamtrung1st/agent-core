using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed class TenantSandboxOperationTarget(ITenantSessionStore sessions, ITenantSandboxProfileStore profiles, ITenantSandboxRuntimeStore runtime,
    TenantRunConfigurationResolver configuration, IamAuthorizer authorizer, IIamStore security, ISandboxImageAuthority images, ISandboxReadiness readiness, TimeProvider time, ISandboxVerificationHost? host = null)
{
    public async ValueTask<(TenantAgent Agent, TenantSession Session, AgentCore.Domain.Definitions.AgentDefinition Baseline,
        AgentCore.Domain.Definitions.SandboxProfile Profile, SandboxLease? Lease)> ResolveAsync(AgentRun run, string profileId, CancellationToken ct, bool storedResult = false)
    {
        var tenant = run.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var (agent, baseline) = await configuration.BaselineAsync(tenant, run.AgentInstanceId, ct).ConfigureAwait(false);
        await configuration.RequireCurrentSpaceAsync(run, ct).ConfigureAwait(false);
        var session = await sessions.FindTenantSessionAsync(tenant, run.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var profile = TenantSandboxOperationRules.Profile(run, profileId);
        var source = await profiles.FindTenantSandboxProfileAsync(tenant, profileId, profile.Revision, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var latest = await profiles.FindTenantSandboxProfileAsync(tenant, profileId, ct: ct).ConfigureAwait(false);
        if (latest?.Enabled != true) throw new AccessDeniedException("resource_unavailable");
        var state = await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        await TenantSandboxRuntimeRules.RequirePermissionsAsync(state, run, profile, authorizer, ct).ConfigureAwait(false);
        if (!storedResult)
        {
            var available = await images.InspectAsync(profile, ct).ConfigureAwait(false);
            if (!available.Eligible || readiness.State != "Ready" || available.CredentialRevision != profile.RegistryCredentialRevision || available.SecretRevision != profile.RegistrySecretRevision)
                throw new AccessDeniedException("sandbox_source_unavailable");
        }
        if (storedResult)
        {
            if (session.Snapshot.ArchivedAt is not null || session.Snapshot.LifecycleStatus != SessionLifecycleStatus.Active
                || session.Snapshot.Status is SessionStatus.Ended or SessionStatus.Ending) throw new AccessDeniedException("resource_unavailable");
            TenantSandboxRuntimeRules.RequireCurrentPolicy(run, agent, baseline, profile, source, null);
        }
        var saved = await runtime.ReadTenantSandboxRuntimeAsync(tenant, run.SessionId, ct: ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        if (!storedResult) TenantSandboxControlRules.RequireCurrentSelection(saved.Lease, agent, latest);
        if (!storedResult && saved.Lease is { State: SandboxLeaseState.Terminated, SelectionWitness: { } witness } && (host is null || witness.HostScope != host.Scope))
            throw new SecurityConflictException("sandbox_selection_changed");
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
        var state = await security.ReadTenantAsync(request.TenantId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        var baseline = AgentCore.Application.Agents.InstanceSettingsResolver.Resolve(target.Baseline, target.Agent.Instance.SettingsOverrides);
        var policy = AgentCore.Domain.Definitions.StateIsolationPolicy.Restrict(run.Admission.Configuration!.Definition.StateIsolationPolicy ?? AgentCore.Domain.Definitions.StateIsolationPolicy.Default,
            baseline.StateIsolationPolicy ?? AgentCore.Domain.Definitions.StateIsolationPolicy.Default).HomeAccess;
        TenantSandboxWorkspaceRules.Require(value.WorkspaceAccess ?? throw new AccessDeniedException("sandbox_workspace_context_required"),
            await TenantSandboxWorkspaceRules.ResolveAsync(state, run, policy, authorizer, ct).ConfigureAwait(false));
        if (request.Snapshot != TenantSandboxOperationRules.Snapshot(target.Agent, TenantSandboxOperationRules.Profile(run, value.ProfileId), target.Lease, reserved?.InitializesGeneration))
            throw new SecurityConflictException("sandbox_operation_witness_changed");
    }
    internal async ValueTask RequireRetainedCommandAsync(TenantApprovalRequest request, AgentRun run, Guid reviewer, CancellationToken ct)
    {
        var command = request.Operation.SandboxCommand ?? throw new AccessDeniedException("sandbox_source_unavailable");
        var target = await ResolveAsync(run, command.ProfileId, ct, storedResult: true);
        var state = await security.ReadTenantAsync(request.TenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        await TenantSandboxWorkspaceRules.RequireCurrentAsync(state, run, target.Agent, target.Baseline,
            command.WorkspaceAccess ?? throw new AccessDeniedException("sandbox_workspace_context_required"), authorizer, ct);
        await RequireStoredReviewerAsync(request, reviewer, ct);
    }
    internal async ValueTask RequireStoredReviewerAsync(TenantApprovalRequest request, Guid reviewer, CancellationToken ct)
    {
        var state = await security.ReadTenantAsync(request.TenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        var scope = AccessScope.ForResource(request.TenantId, request.ResourceType, request.ResourceId);
        var policy = TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(request.Permission));
        if (!state.Users.Any(u => u.PrincipalId == reviewer && u.Status == IdentityStatus.Active)
            || policy.IndependentReview && request.OriginalInitiatorPrincipalId == reviewer)
            throw new AccessDeniedException("approver_unqualified");
        foreach (var key in new[] { request.Permission, "approvals.approve" })
            if (!(await authorizer.CheckOwnedTenantAsync(state, reviewer, PermissionCatalog.Get(key), scope, ct)).Allowed)
                throw new AccessDeniedException("approver_unqualified");
    }

}
