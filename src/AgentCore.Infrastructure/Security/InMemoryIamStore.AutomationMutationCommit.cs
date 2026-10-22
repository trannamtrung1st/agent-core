using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;

namespace AgentCore.Infrastructure.Security;
public sealed partial class InMemoryIamStore
{
    public ValueTask<TenantOperationReceipt?> TryCommitTenantAutomationMutationAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantAutomationMutationCommit commit, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next);
        lock (_gate) lock (sessionBacking.AdmissionGate) return ValueTask.FromResult(WithTenantEvents<TenantOperationReceipt?>(() => {
            var run = sessionBacking.ReadTenantRun(next.Tenant.TenantId, commit.RunId) ?? throw new AccessDeniedException("run_unavailable");
            var old = _operationReceipts.Values.SingleOrDefault(r => r.TenantId == next.Tenant.TenantId && r.RunId == commit.RunId && r.SourceCallId == commit.SourceCallId);
            if (old is not null) return TenantLocalEffectRules.MatchDirectReceipt(old, run, commit.SourceCallId, commit.ParametersHash);
            if (!_tenants.TryGetValue(next.Tenant.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision || run.Revision != commit.ExpectedRunRevision) return null;
            var source = ObserveAutomationMutation(run, commit.Prepared.ToolName, commit.Prepared.ArgumentsJson, commit.Prepared, null, ct);
            var now = (clock ?? TimeProvider.System).GetUtcNow(); var at = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
            var prepared = TenantAutomationMutationCommitRules.Require(source, commit, now);
            TenantAutomationMutationCommitRules.AuthorityAsync(source, commit.Prepared, new IamAuthorizer(this), ct, noReview: true, at: now).AsTask().GetAwaiter().GetResult();
            RequireAutomationMutationCapacity(source, commit.Prepared);
            var previous = RootApproval(before.Tenant.TenantId, run.Admission.Activation.RootActivationId, source.Reader.Agent.PrincipalId, prepared.OperationFingerprint);
            if (previous is not null) throw new AccessDeniedException(TenantApprovalRules.DuplicateReason(previous));
            TenantAutomationMutationCommitRules.Transaction(next, expectedSecurityRevision, source, commit.Prepared, audit);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); EnsureReferences(next, audit, ct: ct);
            RequireAutomationInvocation(source, commit.Prepared, commit.InvocationAdmission, ct);
            var savedRun = run.RecordDirectLocalEffect(run.Revision, commit.Generation, commit.OperationId, commit.ActionHash, now);
            var receipt = new TenantOperationReceipt(commit.OperationId, null, before.Tenant.TenantId, run.AgentRunId, run.SessionId, run.Admission.ExecutionContext!.RootActivationId,
                source.Reader.Agent.PrincipalId, run.AgentInstanceId, commit.ActionHash, TenantOperationOutcome.Completed, TenantAutomationMutationRules.Completed,
                source.Reader.Agent.Instance.Revision, source.Reader.Agent.Instance.PersonaRevision, at, commit.SourceCallId, commit.ParametersHash, AutomationMutation: TenantAutomationMutationCommitRules.Evidence(commit.Prepared));
            if (_operationReceipts.ContainsKey(receipt.OperationId)) throw new SecurityConflictException("identity_conflict");
            ct.ThrowIfCancellationRequested();
            if (commit.InvocationAdmission is { } admitted) { StageTenantAutomationGraph(admitted); automationBacking.CommitTenantOccurrence(admitted.Next, admitted.Occurrence); }
            else automationBacking.SaveTenantAutomation(commit.Prepared.Proposed);
            sessionBacking.SaveTenantOperationRun(savedRun, run.Revision);
            _operationReceipts.Add(receipt.OperationId, receipt); _tenants[before.Tenant.TenantId] = next;
            _audit.Add(TenantOperationAuditRules.WithLocalEffectResult(audit, before, run, commit.OperationId) with { At = at }); return receipt;
        }));
    }
    private void RequireAutomationMutationCapacity(TenantAutomationMutationObservation source, PreparedAutomationMutation value)
    {
        if (value.Proposed.Status == AutomationStatus.Active && source.Current?.Status != AutomationStatus.Active
            && automationBacking.TenantAutomations(source.Reader.Security.Tenant.TenantId).Count(a => a.Owner == value.Proposed.Owner && a.Status == AutomationStatus.Active) >= (source.Reader.Current.TriggerPolicy?.MaxActiveRegistrations ?? 0))
            throw new AccessDeniedException("automation_limit_reached");
    }
    private TenantAutomationMutationObservation RequireAutomationMutationApproval(TenantApprovalRequest request, AgentCore.Domain.Conversation.AgentRun run, CancellationToken ct)
    {
        var value = request.Operation.AutomationMutation!; var source = ObserveAutomationMutation(run, value.ToolName, value.ArgumentsJson, value, null, ct);
        TenantAutomationMutationCommitRules.AuthorityAsync(source, value, new IamAuthorizer(this), ct, request.Kind == ApprovalKind.Review, at: (clock ?? TimeProvider.System).GetUtcNow()).AsTask().GetAwaiter().GetResult();
        RequireAutomationMutationCapacity(source, value);
        if (value.Invocation is not null && automationBacking.TenantBusy(value.Proposed.AutomationId)) throw new SecurityConflictException("automation_in_use");
        return source;
    }
}
