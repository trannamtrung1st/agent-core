using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<TenantBrowserEffectStart?> TryStartBrowserEffectAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantOperationCommit commit, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next);
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            if (_operationReceipts.GetValueOrDefault(commit.OperationId) is { } old)
                return ValueTask.FromResult<TenantBrowserEffectStart?>(new(null, TenantLocalEffectRules.MatchingReceipt(old, next.Tenant.TenantId, commit)));
            if (!_tenants.TryGetValue(next.Tenant.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision)
                return ValueTask.FromResult<TenantBrowserEffectStart?>(null);
            var request = _approvals.GetValueOrDefault(commit.RequestId); var decision = _approvalDecisions.GetValueOrDefault(commit.RequestId);
            var run = sessionBacking.ReadTenantRun(next.Tenant.TenantId, commit.RunId);
            if (request is null || decision is null || run is null || run.Revision != commit.ExpectedRunRevision) return ValueTask.FromResult<TenantBrowserEffectStart?>(null);
            var source = request.RunId == run.AgentRunId ? null : sessionBacking.ReadTenantRun(request.TenantId, request.RunId);
            var grant = _operationGrants.GetValueOrDefault(request.RequestId);
            TenantBrowserEffectRules.Start(request, decision, grant, run, commit, source);
            if (run.SideEffect.Disposition is AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate)
                return ValueTask.FromResult<TenantBrowserEffectStart?>(null);
            TenantBrowserEffectRules.Transaction(next, expectedSecurityRevision, request, audit, commit.Outcome == TenantOperationOutcome.Completed);
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
                    RequireBrowserOperation(request, run, agent);
                    RequireBrowserArtifactBudget(request);
                    TenantBrowserEffectRules.RequireAuthorityAsync(before, request, decision, run, agent, new IamAuthorizer(this), ct,
                        grant, OperationGrantStatus.Available, at).GetAwaiter().GetResult();
                    if (RootApproval(request.TenantId, request.RootActivationId, request.ExecutorPrincipalId, request.Fingerprint)?.RequestId != request.RequestId)
                        reason = "root_operation_conflict";
                }
                catch (AccessDeniedException e) { reason = TenantBrowserEffectRules.Denial(e.ReasonCode); }
                catch (SecurityConflictException e) { reason = TenantBrowserEffectRules.Denial(e.ReasonCode); }
            }
            var saved = reason is null ? run.BeginApprovedExternalEffect(run.Revision, commit.Generation, request.RequestId, commit.OperationId, commit.ActionHash, at)
                : run.RecordApprovedLocalEffect(run.Revision, commit.Generation, request.RequestId, commit.OperationId, commit.ActionHash, false, at);
            var receipt = reason is null ? null : TenantBrowserEffectRules.Receipt(request, run, TenantOperationOutcome.Denied, reason, at);
            audit = TenantOperationAuditRules.WithEffectResult(audit with { Action = reason is null ? TenantBrowserEffectRules.DispatchAction : TenantBrowserEffectRules.Action,
                At = at, Decision = reason is null ? "allowed" : "denied", ReasonCode = reason ?? "browser_command_dispatched" }, before, run, request, grant?.GrantId);
            if (reason is null) audit = audit with { Execution = audit.Execution! with { Purpose = ExecutionAuthorizationPurpose.EffectEligibility } };
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); EnsureReferences(next, audit, ct: ct); ct.ThrowIfCancellationRequested();
            sessionBacking.SaveTenantOperationRun(saved, run.Revision);
            if (grant is not null) _operationGrants[request.RequestId] = grant with { Status = reason is null ? OperationGrantStatus.Claimed : OperationGrantStatus.Revoked, Revision = checked(grant.Revision + 1) };
            if (receipt is not null) _operationReceipts.Add(receipt.OperationId, receipt);
            _tenants[request.TenantId] = next; _audit.Add(audit);
            return ValueTask.FromResult<TenantBrowserEffectStart?>(new(reason is null ? TenantBrowserEffectRules.Fence(request, saved) : null, receipt));
        }
    }
    public ValueTask<IReadOnlyList<TenantBrowserEffectFence>> RecoverableBrowserEffectsAsync(DateTimeOffset at, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate)
        {
            var fences = new List<TenantBrowserEffectFence>();
            foreach (var run in sessionBacking.AgentRuns.Runs.Values.Where(r => r.Owner.TenantId is not null && r.ApprovalRequestId is not null
                && r.SideEffect.ExternalEffect is not null && r.SideEffect.Disposition is AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate)
                .OrderBy(r => r.SideEffect.ExternalEffect!.StartedAtUtc).ThenBy(r => r.AgentRunId))
            {
                var request = _approvals.GetValueOrDefault(run.ApprovalRequestId!.Value);
                if (request?.Operation.BrowserCommand is null || _operationReceipts.ContainsKey(request.Operation.OperationId)
                    || sessionBacking.ReadRetainedTenantOperationRun(request.TenantId, run.AgentRunId) is null || !TenantBrowserEffectRules.Recoverable(run, at)) continue;
                var fence = TenantBrowserEffectRules.Fence(request, run);
                if (run.SideEffect.ToolCallId != "operation:" + fence.OperationId.ToString("D") || run.SideEffect.ActionHash != fence.ActionHash) continue;
                fences.Add(fence); if (fences.Count >= Math.Clamp(limit, 1, 100)) break;
            }
            return ValueTask.FromResult<IReadOnlyList<TenantBrowserEffectFence>>(fences);
        }
    }
    public ValueTask<TenantOperationReceipt?> TrySettleBrowserEffectAsync(TenantSecurityState next, long expectedSecurityRevision, TenantBrowserEffectFence fence,
        TenantOperationOutcome outcome, string reason, AuthorizationAudit audit, bool recovery = false, CancellationToken ct = default, TenantBrowserEffectObservation? observation = null, TenantBrowserArtifactBatch? artifacts = null)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next); TenantBrowserEffectRules.Outcome(outcome, reason, recovery);
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            if (_operationReceipts.GetValueOrDefault(fence.OperationId) is { } old)
                return ValueTask.FromResult<TenantOperationReceipt?>(TenantBrowserEffectRules.Matching(old, fence));
            if (!_tenants.TryGetValue(fence.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult<TenantOperationReceipt?>(null);
            var request = _approvals.GetValueOrDefault(fence.RequestId); var run = sessionBacking.ReadRetainedTenantOperationRun(fence.TenantId, fence.RunId);
            if (request is null || run is null) return ValueTask.FromResult<TenantOperationReceipt?>(null);
            var source = request.RunId == run.AgentRunId ? null : sessionBacking.ReadRetainedTenantOperationRun(fence.TenantId, request.RunId);
            var grant = _operationGrants.GetValueOrDefault(request.RequestId);
            TenantBrowserEffectRules.RequireFence(fence, request, run, source, grant); TenantBrowserEffectRules.Transaction(next, expectedSecurityRevision, request, audit, false);
            var at = ApprovalNow;
            if (at < fence.Origin.StartedAtUtc || recovery && !TenantBrowserEffectRules.Recoverable(run, at)) return ValueTask.FromResult<TenantOperationReceipt?>(null);
            var saved = TenantBrowserEffectRules.SettleRun(run, fence, outcome, at);
            if (observation is not null)
            {
                try
                {
                    var decision = _approvalDecisions.GetValueOrDefault(request.RequestId) ?? throw new AccessDeniedException("approval_unavailable");
                    var agent = FindTenantAgentAsync(request.TenantId, request.Operation.InstanceId, ct).Result ?? throw new AccessDeniedException("executor_unavailable");
                    TenantApprovalRules.Validate(request, run, agent, source: source); RequireBrowserOperation(request, run, agent);
                    TenantBrowserEffectRules.RequireAuthorityAsync(before, request, decision, run, agent, new IamAuthorizer(this), ct,
                        grant, OperationGrantStatus.Claimed, at).GetAwaiter().GetResult();
                }
                catch (Exception e) when (e is AccessDeniedException or SecurityConflictException) { observation = null; }
            }
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); EnsureReferences(next, audit, ct: ct); ct.ThrowIfCancellationRequested();
            var receipt = TenantBrowserEffectRules.Receipt(request, run, outcome, reason, at);
            if (request.Operation.BrowserCommand!.Artifacts is not null && outcome == TenantOperationOutcome.Completed)
                receipt = receipt with { BrowserArtifactsJson = observation is not null && artifacts is not null
                    ? PublishBrowserArtifacts(request, run, artifacts, ct) : TenantBrowserArtifactRules.Unavailable() };
            var publishedAt = ApprovalNow; if (publishedAt > at) at = publishedAt;
            receipt = receipt with { RecordedAt = TenantBrowserEffectRules.Milliseconds(at) };
            if (run.CancellationRequested || run.Claim?.LeaseExpiresAtUtc <= at) observation = null;
            if (observation is not null) observation = TenantBrowserContinuationProjection.WithArtifacts(observation, receipt.BrowserArtifactsJson);
            saved = TenantBrowserContinuationProjection.Save(run, saved, request, outcome, reason, at, observation, receipt);
            audit = TenantOperationAuditRules.WithEffectResult(audit with { At = at, Decision = outcome switch { TenantOperationOutcome.Completed => "allowed", TenantOperationOutcome.Denied => "denied", _ => "indeterminate" }, ReasonCode = reason }, before, run, request, grant?.GrantId);
            if (saved.Revision != run.Revision) sessionBacking.SaveRetainedTenantOperationRun(saved, run.Revision);
            if (grant is not null) _operationGrants[request.RequestId] = grant with { Status = outcome switch {
                TenantOperationOutcome.Completed => OperationGrantStatus.Consumed, TenantOperationOutcome.Denied => OperationGrantStatus.Revoked, _ => OperationGrantStatus.Indeterminate }, Revision = checked(grant.Revision + 1) };
            _operationReceipts.Add(receipt.OperationId, receipt); _tenants[fence.TenantId] = next; _audit.Add(audit);
            return ValueTask.FromResult<TenantOperationReceipt?>(receipt);
        }
    }
}
