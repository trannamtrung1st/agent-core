using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// Stored exact cleanup targets remain controllable without source credentials, image readiness or setup eligibility.
public sealed class TenantSandboxControlOperationTarget(ITenantSessionStore sessions, ITenantSandboxRuntimeStore storage,
    TenantRunConfigurationResolver configuration, IIamStore security, IamAuthorizer authorizer)
{
    public async ValueTask<(TenantAgent Agent, TenantSession Session, SandboxLease Lease, SandboxExecution? Execution, AgentDefinition Baseline)> ResolveAsync(
        AgentRun run, SandboxControlKind kind, Guid? executionId, CancellationToken ct)
    {
        var tenant = run.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var session = await sessions.FindTenantSessionAsync(tenant, run.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var (agent, baseline) = await configuration.BaselineAsync(tenant, run.AgentInstanceId, ct).ConfigureAwait(false);
        var state = await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        var saved = await storage.ReadTenantSandboxRuntimeAsync(tenant, run.SessionId, executionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var lease = saved.Lease ?? throw new AccessDeniedException("resource_unavailable");
        if (saved.Owner != session.Owner || saved.SessionRevision != session.Snapshot.Revision || executionId is not null && saved.Execution?.Id != executionId)
            throw new AccessDeniedException("resource_unavailable");
        await TenantSandboxControlOperationRules.RequirePermissionsAsync(state, session.Owner, lease.Profile.Id, agent.PrincipalId, authorizer, ct).ConfigureAwait(false);
        var value = TenantSandboxControlOperationRules.Parameters(kind, session, lease, saved.Execution);
        TenantSandboxControlOperationRules.Require(value, run, agent, baseline, session, lease, saved.Execution);
        return (agent, session, lease, saved.Execution, baseline);
    }
    public async ValueTask RequireAsync(TenantApprovalRequest request, AgentRun run, CancellationToken ct)
    {
        var value = request.Operation.SandboxControl ?? throw new ArgumentException("Sandbox control required.");
        var target = await ResolveAsync(run, value.Kind, value.ExecutionId, ct).ConfigureAwait(false);
        TenantSandboxControlOperationRules.Require(value, run, target.Agent, target.Baseline, target.Session, target.Lease, target.Execution);
    }
    public async ValueTask RequireReviewerAsync(TenantApprovalRequest request, TenantSecurityState state, Guid human, CancellationToken ct)
    {
        var session = await sessions.FindTenantSessionAsync(request.TenantId, request.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        await TenantSandboxControlOperationRules.RequirePermissionsAsync(state, session.Owner, request.Operation.SandboxControl!.ProfileId, human, authorizer, ct).ConfigureAwait(false);
    }
}
