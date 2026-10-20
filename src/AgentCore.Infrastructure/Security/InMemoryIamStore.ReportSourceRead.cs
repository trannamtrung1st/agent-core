using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private TenantBackgroundCompletion RequireReportSourceReadOperation(TenantApprovalRequest request, bool pending = false)
    {
        var source = CompletionSource(AgentRunOwner.ForTenant(request.TenantId, request.Operation.InstanceId, request.ExecutorPrincipalId), request.RunId)
            ?? throw new AccessDeniedException("completion_unavailable");
        var state = _tenants[request.TenantId]; var iam = new IamAuthorizer(this);
        var agent = FindTenantAgentAsync(request.TenantId, request.Operation.InstanceId).Result ?? throw new AccessDeniedException("executor_unavailable");
        TenantSessionRules.RequireExecutor(state, agent);
        var current = InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(agent), agent.Instance.SettingsOverrides);
        TenantExecutionLineage.RequireRequesterAsync(source.Child, state, iam, this, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        var root = TenantExecutionLineage.RootAsync(source.Child, this, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        TenantReportSourceReadRules.Require(request, source, agent, current, root, pending);
        TenantReportReadRules.RequireRawAsync(state, source, iam, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        return source;
    }
    private void RequireTenantReportRead(TenantSecurityState state, TenantBackgroundCompletion source, CancellationToken ct)
    {
        TenantBackgroundCompletionFactory.RequireParent(source);
        var iam = new IamAuthorizer(this);
        TenantReportReadRules.RequireRawAsync(state, source, iam, ct).AsTask().GetAwaiter().GetResult();
        var lookup = TenantReportSourceReadRules.Lookup(source);
        var request = _approvals.Values.SingleOrDefault(r => r.TenantId == source.Child.Owner.TenantId && r.ExecutorPrincipalId == source.Child.Owner.ExecutorPrincipalId
            && r.RootActivationId == source.Child.Admission.ExecutionContext!.RootActivationId && r.Fingerprint == lookup.OperationFingerprint);
        if (request is null) { TenantReportReadRules.RequireAsync(state, source, iam, ct).AsTask().GetAwaiter().GetResult(); return; }
        if (!_operationReceipts.TryGetValue(request.Operation.OperationId, out var receipt) || !_approvalDecisions.TryGetValue(request.RequestId, out var decision))
            throw new AccessDeniedException("completion_read_review_required");
        TenantReportSourceReadRules.Receipt(request, receipt, decision, _operationGrants.GetValueOrDefault(request.RequestId));
        RequireReportSourceReadOperation(request);
        TenantReportSourceReadRules.ReviewerAsync(state, request, decision.HumanPrincipalId, iam, ct).AsTask().GetAwaiter().GetResult();
    }
    public ValueTask<bool> SettleTenantReportSourceReadAsync(ApprovalProcessedSignal signal, Guid auditId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); if (auditId == Guid.Empty) throw new ArgumentException("Audit identity is required.");
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            if (!_approvalSignals.TryGetValue(signal.SignalId, out var saved) || saved != signal || saved.ProcessedAt is not null
                || !_approvals.TryGetValue(signal.RequestId, out var request) || request.Operation.ReportSourceRead is null || request.Status == TenantApprovalStatus.Pending
                || request.Revision != signal.DecisionRevision || request.TenantId != signal.TenantId || request.RunId != signal.RunId) return ValueTask.FromResult(false);
            var now = ApprovalNow;
            if (now < signal.CreatedAt || !_tenants.TryGetValue(request.TenantId, out var state)) return ValueTask.FromResult(false);
            var approved = request.Status == TenantApprovalStatus.Approved;
            string? reason = approved ? null : TenantApprovalRules.DuplicateReason(request);
            if (approved)
            {
                try
                {
                    var decision = _approvalDecisions.GetValueOrDefault(request.RequestId) ?? throw new AccessDeniedException("approval_unavailable");
                    TenantBackgroundReportRules.Decision(request, decision, _operationGrants.GetValueOrDefault(request.RequestId), now);
                    RequireReportSourceReadOperation(request, pending: true);
                    TenantReportSourceReadRules.ReviewerAsync(state, request, decision.HumanPrincipalId, new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult();
                }
                catch (AccessDeniedException error) { reason = error.ReasonCode; }
                catch (SecurityConflictException error) { reason = error.ReasonCode; }
            }
            if (_audit.Any(a => a.AuditId == auditId) || approved && _operationReceipts.ContainsKey(request.Operation.OperationId)) throw new SecurityConflictException("identity_conflict");
            var next = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
            var code = reason ?? TenantReportSourceReadRules.Reason;
            ct.ThrowIfCancellationRequested();
            if (reason is not null && sessionBacking.AgentRuns.CompletionReceipts.TryGetValue(request.RunId, out var row))
            {
                var item = CompletionInboxMapping.Read(row);
                if (item.Owner == AgentRunOwner.ForTenant(request.TenantId, request.Operation.InstanceId, request.ExecutorPrincipalId) && item.Status == CompletionInboxStatus.Pending)
                    CompletionInboxMapping.Write(row, item with { Revision = item.Revision + 1, Status = CompletionInboxStatus.Skipped, SkipReason = code });
            }
            if (approved) _operationReceipts.Add(request.Operation.OperationId, new(request.Operation.OperationId, request.RequestId, request.TenantId, request.RunId,
                request.SessionId, request.RootActivationId, request.ExecutorPrincipalId, request.Operation.InstanceId, request.Operation.ActionHash,
                reason is null ? TenantOperationOutcome.Completed : TenantOperationOutcome.Denied, code, request.Operation.ExpectedInstanceRevision, request.Operation.ExpectedPersonaRevision, now));
            _approvalSignals[signal.SignalId] = signal with { Revision = signal.Revision + 1, ProcessedAt = now };
            _tenants[request.TenantId] = next;
            _audit.Add(new(auditId, request.TenantId, request.ExecutorPrincipalId, TenantReportSourceReadRules.AuditAction, request.ResourceId, reason is null ? "allowed" : "denied", code, now));
            return ValueTask.FromResult(true);
        }
    }
}
