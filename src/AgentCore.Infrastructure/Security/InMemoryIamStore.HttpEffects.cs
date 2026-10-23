using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantHttpOperationStore
{
    public ValueTask<TenantHttpEffectStart?> TryStartHttpEffectAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantOperationCommit commit, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next);
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            if (_operationReceipts.GetValueOrDefault(commit.OperationId) is { } old)
                return ValueTask.FromResult<TenantHttpEffectStart?>(new(null, TenantLocalEffectRules.MatchingReceipt(old, next.Tenant.TenantId, commit)));
            if (!_tenants.TryGetValue(next.Tenant.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision)
                return ValueTask.FromResult<TenantHttpEffectStart?>(null);
            var request = _approvals.GetValueOrDefault(commit.RequestId); var decision = _approvalDecisions.GetValueOrDefault(commit.RequestId);
            var run = sessionBacking.ReadTenantRun(next.Tenant.TenantId, commit.RunId);
            if (request is null || decision is null || run is null || run.Revision != commit.ExpectedRunRevision) return ValueTask.FromResult<TenantHttpEffectStart?>(null);
            var source = request.RunId == run.AgentRunId ? null : sessionBacking.ReadTenantRun(request.TenantId, request.RunId);
            var grant = _operationGrants.GetValueOrDefault(request.RequestId);
            TenantHttpEffectRules.Start(request, decision, grant, run, commit, source);
            if (run.SideEffect.Disposition is AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate)
                return ValueTask.FromResult<TenantHttpEffectStart?>(null);
            TenantHttpEffectRules.Transaction(next, expectedSecurityRevision, request, audit, commit.Outcome == TenantOperationOutcome.Completed);
            var at = ApprovalNow;
            if (run.Claim!.LeaseExpiresAtUtc <= at) throw new SecurityConflictException("execution_claim_expired");
            var agent = FindTenantAgentAsync(request.TenantId, request.Operation.InstanceId, ct).Result;
            var reason = commit.Outcome == TenantOperationOutcome.Denied ? commit.ReasonCode : request.ExpiresAt <= at ? "approval_expired"
                : grant?.ExpiresAt <= at ? "grant_expired" : null;
            if (reason is null)
            {
                try
                {
                    if (agent is null) throw new AccessDeniedException("executor_unavailable");
                    TenantApprovalRules.Validate(request, run, agent, source: source);
                    RequireHttpOperation(request, run, agent, source);
                    TenantHttpEffectRules.AuthorityAsync(before, request, decision, agent, run, new IamAuthorizer(this), grant, OperationGrantStatus.Available, at, ct).GetAwaiter().GetResult();
                    if (RootApproval(request.TenantId, request.RootActivationId, request.ExecutorPrincipalId, request.Fingerprint)?.RequestId != request.RequestId)
                        reason = "root_operation_conflict";
                }
                catch (AccessDeniedException e) { reason = TenantHttpEffectRules.Denial(e.ReasonCode); }
                catch (SecurityConflictException e) { reason = TenantHttpEffectRules.Denial(e.ReasonCode); }
            }
            var saved = reason is null ? run.BeginApprovedExternalEffect(run.Revision, commit.Generation, request.RequestId, commit.OperationId, commit.ActionHash, at)
                : run.RecordApprovedLocalEffect(run.Revision, commit.Generation, request.RequestId, commit.OperationId, commit.ActionHash, false, at);
            var receipt = reason is null ? null : TenantHttpEffectRules.Receipt(request, run, TenantOperationOutcome.Denied, reason, at);
            audit = TenantOperationAuditRules.WithEffectResult(audit with { Action = reason is null ? TenantHttpEffectRules.Dispatch : TenantHttpEffectRules.Result,
                At = at, Decision = reason is null ? "allowed" : "denied", ReasonCode = reason ?? "http_request_dispatched" }, before, run, request, grant?.GrantId);
            if (reason is null) audit = audit with { Execution = audit.Execution! with { Purpose = ExecutionAuthorizationPurpose.EffectEligibility } };
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); EnsureReferences(next, audit, ct: ct); ct.ThrowIfCancellationRequested();
            sessionBacking.SaveTenantOperationRun(saved, run.Revision);
            if (grant is not null) _operationGrants[request.RequestId] = grant with { Status = reason is null ? OperationGrantStatus.Claimed : OperationGrantStatus.Revoked, Revision = checked(grant.Revision + 1) };
            if (receipt is not null) _operationReceipts.Add(receipt.OperationId, receipt);
            _tenants[request.TenantId] = next; _audit.Add(audit);
            return ValueTask.FromResult<TenantHttpEffectStart?>(new(reason is null ? TenantHttpEffectRules.Fence(request, saved) : null, receipt));
        }
    }
    public ValueTask<IReadOnlyList<TenantHttpDispatch>> RecoverableHttpEffectsAsync(DateTimeOffset at, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate)
        {
            var fences = new List<TenantHttpDispatch>();
            foreach (var run in sessionBacking.AgentRuns.Runs.Values.Where(r => r.Owner.TenantId is not null && r.ApprovalRequestId is not null
                && r.SideEffect.ExternalEffect is { DirectOperation: null } && r.SideEffect.Disposition is AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate)
                .OrderBy(r => r.SideEffect.ExternalEffect!.StartedAtUtc).ThenBy(r => r.AgentRunId))
            {
                var request = _approvals.GetValueOrDefault(run.ApprovalRequestId!.Value);
                if (request?.Operation.HttpRequest is null || _operationReceipts.ContainsKey(request.Operation.OperationId)
                    || sessionBacking.ReadRetainedTenantOperationRun(request.TenantId, run.AgentRunId) is null || !TenantHttpEffectRules.Recoverable(run, at)) continue;
                var fence = TenantHttpEffectRules.Fence(request, run);
                if (run.SideEffect.ToolCallId != "operation:" + fence.OperationId.ToString("D") || run.SideEffect.ActionHash != fence.ActionHash) continue;
                fences.Add(fence); if (fences.Count >= Math.Clamp(limit, 1, 100)) break;
            }
            return ValueTask.FromResult<IReadOnlyList<TenantHttpDispatch>>(fences);
        }
    }
    public ValueTask<TenantOperationReceipt?> TrySettleHttpEffectAsync(TenantSecurityState next, long expectedSecurityRevision, TenantHttpDispatch fence,
        TenantOperationOutcome outcome, string reason, AuthorizationAudit audit, bool recovery = false, CancellationToken ct = default, TenantHttpResult? result = null)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next); TenantHttpEffectRules.Outcome(outcome, reason, recovery);
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            if (_operationReceipts.GetValueOrDefault(fence.OperationId) is { } old)
                return ValueTask.FromResult<TenantOperationReceipt?>(TenantHttpEffectRules.Matching(old, fence));
            if (!_tenants.TryGetValue(fence.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult<TenantOperationReceipt?>(null);
            var request = _approvals.GetValueOrDefault(fence.RequestId); var run = sessionBacking.ReadRetainedTenantOperationRun(fence.TenantId, fence.RunId);
            if (request is null || run is null) return ValueTask.FromResult<TenantOperationReceipt?>(null);
            var source = request.RunId == run.AgentRunId ? null : sessionBacking.ReadRetainedTenantOperationRun(fence.TenantId, request.RunId);
            var grant = _operationGrants.GetValueOrDefault(request.RequestId);
            TenantHttpEffectRules.RequireFence(fence, request, run, source, grant); TenantHttpEffectRules.Transaction(next, expectedSecurityRevision, request, audit, false);
            var at = ApprovalNow;
            if (at < fence.Origin.StartedAtUtc || recovery && !TenantHttpEffectRules.Recoverable(run, at)) return ValueTask.FromResult<TenantOperationReceipt?>(null);
            var fact = result is null ? null : TenantHttpEffectRules.Metadata(result);
            if (result is not null)
            {
                try
                {
                    var agent = FindTenantAgentAsync(request.TenantId, request.Operation.InstanceId, ct).Result ?? throw new AccessDeniedException("executor_unavailable");
                    RequireHttpOperation(request, run, agent, source);
                    var decision = _approvalDecisions.GetValueOrDefault(request.RequestId) ?? throw new AccessDeniedException("approval_unavailable");
                    TenantHttpEffectRules.AuthorityAsync(before, request, decision, agent, run, new IamAuthorizer(this), grant, OperationGrantStatus.Claimed, at, ct).GetAwaiter().GetResult();
                }
                catch (Exception e) when (e is AccessDeniedException or SecurityConflictException) { result = null; }
            }
            var saved = TenantHttpEffectRules.SettleRun(run, fence, outcome, at);
            saved = TenantHttpContinuationProjection.Save(run, saved, request, outcome, reason, at, result);
            var receipt = TenantHttpEffectRules.Receipt(request, run, outcome, reason, at, fact);
            audit = TenantOperationAuditRules.WithEffectResult(audit with { At = at, Decision = outcome switch { TenantOperationOutcome.Completed => "allowed", TenantOperationOutcome.Denied => "denied", _ => "indeterminate" }, ReasonCode = reason }, before, run, request, grant?.GrantId);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); EnsureReferences(next, audit, ct: ct); ct.ThrowIfCancellationRequested();
            if (saved.Revision != run.Revision) sessionBacking.SaveRetainedTenantOperationRun(saved, run.Revision);
            if (grant is not null) _operationGrants[request.RequestId] = grant with { Status = outcome switch {
                TenantOperationOutcome.Completed => OperationGrantStatus.Consumed, TenantOperationOutcome.Denied => OperationGrantStatus.Revoked, _ => OperationGrantStatus.Indeterminate }, Revision = checked(grant.Revision + 1) };
            _operationReceipts.Add(receipt.OperationId, receipt); _tenants[fence.TenantId] = next; _audit.Add(audit);
            return ValueTask.FromResult<TenantOperationReceipt?>(receipt);
        }
    }
}
