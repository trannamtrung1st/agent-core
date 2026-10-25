using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantCredentialCheckStore
{
    private readonly Dictionary<Guid, TenantCredentialCheckDispatch> _credentialCheckDispatches = [];
    public ValueTask<TenantCredentialCheckDispatch?> FindCredentialCheckDispatchAsync(Guid tenantId, Guid operationId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(_credentialCheckDispatches.GetValueOrDefault(operationId) is { } d && d.TenantId == tenantId ? d : null); }

    public ValueTask<IReadOnlyList<TenantCredentialCheckDispatch>> RecoverableCredentialChecksAsync(DateTimeOffset at, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate)
            return ValueTask.FromResult<IReadOnlyList<TenantCredentialCheckDispatch>>(_credentialCheckDispatches.Values
                .Where(d => d.CompletedAt is null && sessionBacking.ReadRetainedTenantOperationRun(d.TenantId, d.RunId) is { } r && TenantCredentialCheckRules.Recoverable(d, r, at))
                .OrderBy(d => d.StartedAt).ThenBy(d => d.OperationId).Take(Math.Clamp(limit, 1, 100)).ToArray());
    }

    public ValueTask<TenantCredentialCheckStart?> TryStartCredentialCheckAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantOperationCommit commit, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next);
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate)
        {
            if (_operationReceipts.GetValueOrDefault(commit.OperationId) is { } previous)
                return ValueTask.FromResult<TenantCredentialCheckStart?>(new(null, TenantLocalEffectRules.MatchingReceipt(previous, next.Tenant.TenantId, commit)));
            if (_credentialCheckDispatches.ContainsKey(commit.OperationId) || !_tenants.TryGetValue(next.Tenant.TenantId, out var before)
                || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult<TenantCredentialCheckStart?>(null);
            var request = _approvals.GetValueOrDefault(commit.RequestId); var decision = _approvalDecisions.GetValueOrDefault(commit.RequestId);
            var run = sessionBacking.ReadTenantRun(next.Tenant.TenantId, commit.RunId);
            if (request is null || decision is null || run is null || run.Revision != commit.ExpectedRunRevision) return ValueTask.FromResult<TenantCredentialCheckStart?>(null);
            var source = request.RunId == run.AgentRunId ? null : sessionBacking.ReadTenantRun(request.TenantId, request.RunId);
            var grant = _operationGrants.GetValueOrDefault(request.RequestId);
            TenantCredentialCheckRules.Start(request, decision, grant, run, commit, source);
            TenantCredentialCheckRules.Transaction(next, expectedSecurityRevision, request, audit, commit.Outcome == TenantOperationOutcome.Completed);
            var at = ApprovalNow;
            if (run.Claim!.LeaseExpiresAtUtc <= at) throw new SecurityConflictException("execution_claim_expired");
            var agent = FindTenantAgentAsync(request.TenantId, request.Operation.InstanceId, ct).Result;
            var reason = commit.Outcome == TenantOperationOutcome.Denied ? commit.ReasonCode : request.ExpiresAt <= at ? "approval_expired"
                : grant is not null && grant.ExpiresAt <= at ? "grant_expired" : null;
            if (reason is null)
            {
                TenantCredentialBinding? bound = null;
                try { bound = ContextCredentialBindings(run, ct).SingleOrDefault(b => b.Binding.BindingId == request.Operation.CredentialCheck!.BindingId); }
                catch (AccessDeniedException ex) { reason = ex.ReasonCode; }
                reason ??= TenantCredentialCheckRules.TargetReason(request, run, agent, bound, source);
            }
            if (reason is null && _credentialCheckDispatches.Values.Any(d => d.TenantId == request.TenantId && d.CredentialId == request.Operation.CredentialCheck!.CredentialId && d.CompletedAt is null))
                reason = "credential_check_in_progress";
            if (reason is null)
            {
                try { TenantSessionRules.RequireExecutor(before, agent!); ValidateAgentActor(before, request.ExecutorPrincipalId); }
                catch (AccessDeniedException) { reason = "executor_unavailable"; }
                try { ValidateAgentActor(before, decision.HumanPrincipalId); }
                catch (AccessDeniedException) { reason ??= "grantor_authority_lost"; }
                if (request.OriginalInitiatorPrincipalId is { } requester)
                    try { ValidateAgentActor(before, requester); } catch (AccessDeniedException) { reason ??= "requester_unavailable"; }
                if (before.Tenant.Status != IdentityStatus.Active) reason = "tenant_unavailable";
                var root = RootApproval(request.TenantId, request.RootActivationId, request.ExecutorPrincipalId, request.Fingerprint);
                if (root?.RequestId != request.RequestId) reason ??= "root_operation_conflict";
            }
            var started = reason is null
                ? run.BeginApprovedExternalEffect(run.Revision, commit.Generation, request.RequestId, commit.OperationId, commit.ActionHash, at)
                : run.RecordApprovedLocalEffect(run.Revision, commit.Generation, request.RequestId, commit.OperationId, commit.ActionHash, false, at);
            var dispatch = reason is null ? TenantCredentialCheckRules.Dispatch(request, started, at) : null;
            var receipt = reason is null ? null : TenantCredentialCheckRules.Receipt(request, run, agent, TenantOperationOutcome.Denied, reason, at);
            audit = audit with { Action = dispatch is null ? TenantCredentialCheckRules.Action : TenantCredentialCheckRules.DispatchAction,
                At = at, Decision = reason is null ? "allowed" : "denied", ReasonCode = reason ?? "credential_check_dispatched" };
            audit = TenantOperationAuditRules.WithEffectResult(audit, before, run, request, grant?.GrantId);
            if (dispatch is not null) audit = audit with { Execution = audit.Execution! with { Purpose = ExecutionAuthorizationPurpose.EffectEligibility } };
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); EnsureReferences(next, audit, ct: ct); ct.ThrowIfCancellationRequested();
            sessionBacking.SaveTenantOperationRun(started, run.Revision);
            if (grant is not null) _operationGrants[request.RequestId] = grant with { Status = dispatch is null ? OperationGrantStatus.Revoked : OperationGrantStatus.Claimed, Revision = checked(grant.Revision + 1) };
            if (dispatch is not null) _credentialCheckDispatches.Add(dispatch.OperationId, dispatch);
            if (receipt is not null) _operationReceipts.Add(receipt.OperationId, receipt);
            _tenants[request.TenantId] = next; _audit.Add(audit);
            return ValueTask.FromResult<TenantCredentialCheckStart?>(new(dispatch, receipt));
        }
    }

    public ValueTask<TenantOperationReceipt?> TrySettleCredentialCheckAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantCredentialCheckDispatch dispatch, CredentialOAuthCheck? check, AuthorizationAudit audit, bool recovery = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next);
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate)
        {
            if (_operationReceipts.GetValueOrDefault(dispatch.OperationId) is { } old)
                return ValueTask.FromResult<TenantOperationReceipt?>(CredentialCheckReceipt(old, dispatch));
            if (_credentialCheckDispatches.GetValueOrDefault(dispatch.OperationId) != dispatch || dispatch.CompletedAt is not null
                || !_tenants.TryGetValue(dispatch.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult<TenantOperationReceipt?>(null);
            var request = _approvals.GetValueOrDefault(dispatch.RequestId); var run = sessionBacking.ReadRetainedTenantOperationRun(dispatch.TenantId, dispatch.RunId);
            if (request is null || run is null) return ValueTask.FromResult<TenantOperationReceipt?>(null);
            var grant = _operationGrants.GetValueOrDefault(dispatch.RequestId);
            var source = request.RunId == run.AgentRunId ? null : sessionBacking.ReadRetainedTenantOperationRun(request.TenantId, request.RunId);
            TenantCredentialCheckRules.RequireDispatch(dispatch, request, run, grant, source);
            TenantCredentialCheckRules.Transaction(next, expectedSecurityRevision, request, audit, false);
            var at = ApprovalNow;
            if (at < dispatch.StartedAt || recovery && (!TenantCredentialCheckRules.Recoverable(dispatch, run, at) || check is not null)) return ValueTask.FromResult<TenantOperationReceipt?>(null);
            var value = FindTenantCredentialAsync(dispatch.TenantId, dispatch.CredentialId, ct).Result?.Value;
            var result = TenantCredentialCheckRules.Completion(request, value, check, at);
            var agent = FindTenantAgentAsync(dispatch.TenantId, dispatch.InstanceId, ct).Result;
            var receipt = TenantCredentialCheckRules.Receipt(request, run, agent, result.Outcome, result.Reason, at);
            var saved = TenantCredentialCheckRules.SettleRun(run, dispatch, result.Outcome, at);
            audit = TenantOperationAuditRules.WithEffectResult(audit with { At = at, Decision = TenantCredentialCheckRules.AuditDecision(result.Outcome), ReasonCode = result.Reason }, before, run, request, grant?.GrantId);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); EnsureReferences(next, audit, ct: ct); ct.ThrowIfCancellationRequested();
            if (result.Apply is { } apply)
            {
                if (!CredentialBacking().ApplyTenantOAuthCheck(dispatch.TenantId, dispatch.CredentialId, value!.Revision, value.SecretRevision, apply, at))
                    throw new SecurityConflictException("operation_witness_changed");
                if (apply.ReconnectRequired) SupersedeOAuth(dispatch.TenantId, dispatch.CredentialId);
            }
            if (saved.Revision != run.Revision) sessionBacking.SaveTenantOperationRun(saved, run.Revision);
            if (grant is not null) _operationGrants[dispatch.RequestId] = grant with { Status = result.Outcome == TenantOperationOutcome.Completed ? OperationGrantStatus.Consumed : OperationGrantStatus.Indeterminate, Revision = checked(grant.Revision + 1) };
            _credentialCheckDispatches[dispatch.OperationId] = dispatch with { CompletedAt = TenantCredentialCheckRules.Milliseconds(at) }; _operationReceipts.Add(receipt.OperationId, receipt);
            _tenants[dispatch.TenantId] = next; _audit.Add(audit); return ValueTask.FromResult<TenantOperationReceipt?>(receipt);
        }
    }

    private static TenantOperationReceipt CredentialCheckReceipt(TenantOperationReceipt receipt, TenantCredentialCheckDispatch dispatch)
    {
        if (receipt.OperationId != dispatch.OperationId || receipt.TenantId != dispatch.TenantId || receipt.RequestId != dispatch.RequestId
            || receipt.RunId != dispatch.RunId || receipt.SessionId != dispatch.SessionId || receipt.RootActivationId != dispatch.RootActivationId
            || receipt.ExecutorPrincipalId != dispatch.ExecutorPrincipalId || receipt.InstanceId != dispatch.InstanceId || receipt.ActionHash != dispatch.ActionHash)
            throw new SecurityConflictException("operation_mismatch");
        return receipt;
    }
}
