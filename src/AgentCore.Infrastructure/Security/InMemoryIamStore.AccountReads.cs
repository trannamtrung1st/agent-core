using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private void RequireAccountReadSources(AgentRun run, CancellationToken ct)
    {
        foreach (var reference in TenantAccountReadReferences.Read(run))
        {
            var tenant = run.Owner.TenantId!.Value; var state = _tenants[tenant]; var iam = new IamAuthorizer(this);
            var request = _approvals.GetValueOrDefault(reference.RequestId); var receipt = _operationReceipts.GetValueOrDefault(reference.OperationId);
            var decision = _approvalDecisions.GetValueOrDefault(reference.RequestId);
            if (request?.TenantId != tenant || request.Operation.AccountRead is null || receipt is null || decision is null) throw new AccessDeniedException("account_read_unavailable");
            var agent = FindTenantAgentAsync(tenant, run.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("executor_unavailable");
            var bound = RequireAccountReadOperation(request, run, agent, exactSecret: false);
            TenantAccountReadReferences.Require(reference, request, bound, agent);
            TenantAccountReadRules.Receipt(request, run, receipt, decision, _operationGrants.GetValueOrDefault(request.RequestId));
            TenantExecutionLineage.RequireRequesterAsync(run, state, iam, this, ct).GetAwaiter().GetResult();
            TenantAccountReadRules.ReviewerAsync(state, request, decision.HumanPrincipalId, iam, ct).GetAwaiter().GetResult();
        }
    }
    private TenantCredentialBinding RequireAccountReadOperation(TenantApprovalRequest request, AgentRun run, TenantAgent agent, bool exactSecret = true)
    {
        var read = request.Operation.AccountRead!;
        var session = sessionBacking.ReadTenantSession(request.TenantId, read.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
        var bound = ReadTenantCredentialBindingsAsync(request.TenantId, agent.Instance.InstanceId).Result?.Bindings.SingleOrDefault(b => b.Binding.BindingId == read.Credential.BindingId)
            ?? throw new AccessDeniedException("account_source_changed");
        TenantAccountReadRules.Require(read, run, agent, session, TenantAgentDefinitionBaseline(agent), bound, exactSecret);
        if (read.Source is { } original)
            TenantAccountOperationReadRules.Require(read, request.TenantId, agent.Instance.InstanceId, agent.PrincipalId,
                _approvals.GetValueOrDefault(original.RequestId), _operationReceipts.GetValueOrDefault(original.OperationId),
                sessionBacking.ReadTenantRun(request.TenantId, original.RunId), sessionBacking.ReadTenantSession(request.TenantId, original.SessionId));
        TenantAccountReadRules.PermissionsAsync(_tenants[request.TenantId], read, agent.PrincipalId, new IamAuthorizer(this), request.Kind == ApprovalKind.Review, CancellationToken.None).GetAwaiter().GetResult();
        if (request.Snapshot?.Credential != TenantAccountReadRules.Snapshot(agent, bound).Credential)
            throw new SecurityConflictException("account_source_changed");
        return bound;
    }
    public ValueTask<bool> TryCommitRuntimeAccountReadAsync(TenantSecurityState next, long expectedSecurityRevision, TenantRuntimeAccountReadProof proof,
        CredentialOAuthCheck? maintenance, AuthorizationAudit audit, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next); ct.ThrowIfCancellationRequested();
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            if (!_tenants.TryGetValue(next.Tenant.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult(false);
            var request = _approvals.GetValueOrDefault(proof.RequestId); var run = sessionBacking.ReadTenantRun(next.Tenant.TenantId, proof.Frame.RunId);
            var receipt = _operationReceipts.GetValueOrDefault(proof.OperationId); var decision = _approvalDecisions.GetValueOrDefault(proof.RequestId);
            if (request is null || run is null || receipt is null || decision is null || request.Operation.AccountRead is null) return ValueTask.FromResult(false);
            var agent = FindTenantAgentAsync(request.TenantId, run.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("executor_unavailable");
            var bound = RequireAccountReadOperation(request, run, agent, exactSecret: false);
            var session = sessionBacking.ReadTenantSession(request.TenantId, run.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
            TenantRuntimeRules.Require(proof.Frame, session.Owner, run, ApprovalNow);
            if (run.Revision != proof.RunRevision || bound.Credential.Value.Revision != proof.CredentialRevision || bound.Credential.Value.SecretRevision != proof.SecretRevision)
                return ValueTask.FromResult(false);
            TenantAccountReadRules.Receipt(request, run, receipt, decision, _operationGrants.GetValueOrDefault(request.RequestId));
            TenantSessionRules.RequireExecutor(before, agent);
            TenantExecutionLineage.RequireRequesterAsync(run, before, new IamAuthorizer(this), this, ct).GetAwaiter().GetResult();
            TenantAccountReadRules.ReviewerAsync(before, request, decision.HumanPrincipalId, new IamAuthorizer(this), ct).GetAwaiter().GetResult();
            TenantRuntimeAccountReadRules.Transaction(next, expectedSecurityRevision, proof, request, maintenance, audit);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); EnsureReferences(next, audit, ct: ct);
            audit = TenantOperationAuditRules.WithEffectResult(audit, before, run, request, _operationGrants.GetValueOrDefault(request.RequestId)?.GrantId);
            ct.ThrowIfCancellationRequested();
            if (maintenance is not null)
            {
                if (!CredentialBacking().ApplyTenantOAuthCheck(request.TenantId, bound.Credential.Value.CredentialId, proof.CredentialRevision, proof.SecretRevision, maintenance, OAuthNow)) return ValueTask.FromResult(false);
                if (maintenance.ReconnectRequired) SupersedeOAuth(request.TenantId, bound.Credential.Value.CredentialId);
            }
            _tenants[request.TenantId] = next; _audit.Add(audit); return ValueTask.FromResult(true);
        }
    }
}
