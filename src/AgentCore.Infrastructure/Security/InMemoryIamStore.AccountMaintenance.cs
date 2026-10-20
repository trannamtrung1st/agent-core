using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<bool> TryMaintainAccountEffectAsync(TenantSecurityState next, long expectedSecurityRevision, TenantAccountDispatch fence, long credentialRevision, long secretRevision,
        CredentialOAuthCheck maintenance, AuthorizationAudit audit, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next); ct.ThrowIfCancellationRequested();
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            if (!_tenants.TryGetValue(next.Tenant.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult(false);
            var request = _approvals.GetValueOrDefault(fence.RequestId); var run = sessionBacking.ReadTenantRun(next.Tenant.TenantId, fence.RunId);
            var receipt = _operationReceipts.GetValueOrDefault(fence.OperationId); var decision = _approvalDecisions.GetValueOrDefault(fence.RequestId);
            if (request is null || run is null || receipt is not null || decision is null || request.Operation.AccountAction is null) return ValueTask.FromResult(false);
            var source = request.RunId == run.AgentRunId ? null : sessionBacking.ReadTenantRun(request.TenantId, request.RunId);
            var agent = FindTenantAgentAsync(request.TenantId, run.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("executor_unavailable");
            var bound = RequireAccountActionOperation(request, run, agent, source, exactSecret: false);
            var session = sessionBacking.ReadTenantSession(request.TenantId, run.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
            TenantRuntimeRules.Require(new(run.Owner, run.SessionId, run.AgentRunId, fence.Origin.Generation), session.Owner, run, ApprovalNow);
            if (bound.Credential.Value.Revision != credentialRevision || bound.Credential.Value.SecretRevision != secretRevision)
                return ValueTask.FromResult(false);
            TenantAccountEffectRules.RequireFence(fence, request, run, source, _operationGrants.GetValueOrDefault(request.RequestId));
            TenantSessionRules.RequireExecutor(before, agent);
            TenantExecutionLineage.RequireRequesterAsync(run, before, new IamAuthorizer(this), this, ct).GetAwaiter().GetResult();
            TenantAccountEffectRules.RequireAuthorityAsync(before, request, decision, agent, new IamAuthorizer(this), _operationGrants.GetValueOrDefault(request.RequestId),
                OperationGrantStatus.Claimed, ApprovalNow, ct).GetAwaiter().GetResult();
            TenantAccountMaintenanceRules.Transaction(next, expectedSecurityRevision, fence, request, maintenance, audit);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); EnsureReferences(next, audit, ct: ct);
            audit = TenantOperationAuditRules.WithEffectResult(audit, before, run, request, _operationGrants.GetValueOrDefault(request.RequestId)?.GrantId);
            ct.ThrowIfCancellationRequested();
            if (maintenance.RotatedProtectedPayload is not null || maintenance.ReconnectRequired)
            {
                if (!CredentialBacking().ApplyTenantOAuthCheck(request.TenantId, bound.Credential.Value.CredentialId, credentialRevision, secretRevision, maintenance, OAuthNow)) return ValueTask.FromResult(false);
                if (maintenance.ReconnectRequired) SupersedeOAuth(request.TenantId, bound.Credential.Value.CredentialId);
            }
            _tenants[request.TenantId] = next; _audit.Add(audit); return ValueTask.FromResult(true);
        }
    }
}
