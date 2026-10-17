using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantApprovedOperationStore
{
    private readonly Dictionary<Guid, TenantOperationReceipt> _operationReceipts = [];
    public ValueTask<TenantApprovalDecision?> FindApprovalDecisionAsync(Guid tenantId, Guid requestId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(_approvalDecisions.GetValueOrDefault(requestId) is { } d && d.TenantId == tenantId ? d : null); }
    public ValueTask<TenantOperationReceipt?> FindOperationReceiptAsync(Guid tenantId, Guid operationId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(_operationReceipts.GetValueOrDefault(operationId) is { } r && r.TenantId == tenantId ? r : null); }

    public ValueTask<TenantOperationReceipt?> TryCommitLocalEffectAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantLocalEffectCommit commit, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next);
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate)
        {
            if (_operationReceipts.GetValueOrDefault(commit.OperationId) is { } previous)
                return ValueTask.FromResult<TenantOperationReceipt?>(TenantLocalEffectRules.MatchingReceipt(previous, next.Tenant.TenantId, commit));
            if (!_tenants.TryGetValue(next.Tenant.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult<TenantOperationReceipt?>(null);
            var request = _approvals.GetValueOrDefault(commit.RequestId); var decision = _approvalDecisions.GetValueOrDefault(commit.RequestId);
            var run = sessionBacking.ReadTenantRun(next.Tenant.TenantId, commit.RunId);
            if (request is null || decision is null || run is null || run.Revision != commit.ExpectedRunRevision) return ValueTask.FromResult<TenantOperationReceipt?>(null);
            var grant = _operationGrants.GetValueOrDefault(commit.RequestId);
            TenantLocalEffectRules.Validate(request, decision, grant, run, commit);
            var target = FindTenantAgentAsync(next.Tenant.TenantId, request.Operation.InstanceId, ct).Result;
            if (target is null) return ValueTask.FromResult<TenantOperationReceipt?>(null);
            TenantSessionRules.RequireExecutor(before, target); ValidateAgentActor(before, request.ExecutorPrincipalId);
            var at = (clock ?? TimeProvider.System).GetUtcNow();
            commit = TenantLocalEffectRules.AtCommit(commit, request, grant, run, target, at);
            if (commit.Outcome == TenantOperationOutcome.Completed)
            {
                try { ValidateAgentActor(before, decision.HumanPrincipalId); }
                catch (AccessDeniedException) { commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = "grantor_authority_lost" }; }
                if (commit.Outcome == TenantOperationOutcome.Completed && request.OriginalInitiatorPrincipalId is { } requester)
                    try { ValidateAgentActor(before, requester); }
                    catch (AccessDeniedException) { commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = "requester_unavailable" }; }
            }
            audit = audit with { Decision = commit.Outcome == TenantOperationOutcome.Completed ? "allowed" : "denied", ReasonCode = commit.ReasonCode, At = at };
            TenantLocalEffectRules.ValidateTransaction(next, expectedSecurityRevision, request, audit);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); EnsureReferences(next, audit, ct: ct);
            at = (clock ?? TimeProvider.System).GetUtcNow();
            commit = TenantLocalEffectRules.AtCommit(commit, request, grant, run, target, at);
            audit = audit with { Decision = commit.Outcome == TenantOperationOutcome.Completed ? "allowed" : "denied", ReasonCode = commit.ReasonCode, At = at };
            var complete = commit.Outcome == TenantOperationOutcome.Completed;
            var savedRun = run.RecordApprovedLocalEffect(run.Revision, commit.Generation, request.RequestId, commit.OperationId, commit.ActionHash, complete, at);
            var change = complete ? TenantLocalEffectRules.Change(request, target.Instance) : null;
            if (complete) _ = TenantAgentConfigurationRules.Apply(target.Instance, change!, at);
            ct.ThrowIfCancellationRequested();
            var updated = complete ? instances.ChangeTenantConfigurationAsync(new(target.Instance.InstanceId, target.Instance.Revision,
                Persona: change!.Persona, ExpectedPersonaRevision: change.ExpectedPersonaRevision,
                SetSettingsOverrides: change.SetSettingsOverrides, SettingsOverrides: change.SettingsOverrides, AutomationPolicyChanges: change.AutomationPolicyChanges), at, ct).Result : target.Instance;
            var receipt = new TenantOperationReceipt(commit.OperationId, request.RequestId, request.TenantId, run.AgentRunId, run.SessionId,
                run.Admission.Activation.RootActivationId, request.ExecutorPrincipalId, target.Instance.InstanceId, commit.ActionHash, commit.Outcome, commit.ReasonCode,
                updated.Revision, updated.PersonaRevision, DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds()));
            sessionBacking.SaveTenantOperationRun(savedRun, run.Revision);
            if (grant is not null) _operationGrants[request.RequestId] = grant with { Status = complete ? OperationGrantStatus.Consumed : OperationGrantStatus.Revoked, Revision = checked(grant.Revision + 1) };
            _operationReceipts.Add(receipt.OperationId, receipt); _tenants[request.TenantId] = next; _audit.Add(audit);
            return ValueTask.FromResult<TenantOperationReceipt?>(receipt);
        }
    }

}
