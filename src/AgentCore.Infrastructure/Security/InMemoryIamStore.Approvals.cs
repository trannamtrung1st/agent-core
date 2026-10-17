using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantApprovalStore
{
    private readonly Dictionary<Guid, TenantApprovalRequest> _approvals = [];
    private readonly Dictionary<Guid, TenantApprovalDecision> _approvalDecisions = [];
    private readonly Dictionary<Guid, TenantOperationGrant> _operationGrants = [];
    private readonly Dictionary<Guid, ApprovalProcessedSignal> _approvalSignals = [];
    private DateTimeOffset ApprovalNow => (clock ?? TimeProvider.System).GetUtcNow();
    public ValueTask<TenantApprovalRequest?> FindApprovalAsync(Guid tenantId, Guid requestId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(_approvals.GetValueOrDefault(requestId) is { } r && r.TenantId == tenantId ? r : null); }
    public ValueTask<TenantApprovalRequest?> FindRootApprovalAsync(Guid tenantId, Guid rootId, Guid executorId, PreparedTenantOperation operation, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(RootApproval(tenantId, rootId, executorId, operation.OperationFingerprint)); }
    private TenantApprovalRequest? RootApproval(Guid tenant, Guid root, Guid executor, string hash) => _approvals.Values.Where(r => r.TenantId == tenant && r.RootActivationId == root && r.ExecutorPrincipalId == executor && r.Fingerprint == hash)
        .OrderBy(r => r.Status is TenantApprovalStatus.Rejected or TenantApprovalStatus.Expired or TenantApprovalStatus.Cancelled ? 0 : r.Status == TenantApprovalStatus.Pending ? 1 : 2)
        .ThenBy(r => r.CreatedAt.ToUnixTimeMilliseconds()).ThenBy(r => r.RequestId).FirstOrDefault();
    public ValueTask<TenantApprovalPage> ListApprovalsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedRequests, IReadOnlyList<Guid>? allowedInstances, TenantApprovalQuery page, CancellationToken ct = default, IReadOnlyList<Guid>? allowedCredentials = null)
    {
        ct.ThrowIfCancellationRequested(); page.Validate();
        lock (_gate)
        {
            var rows = _approvals.Values.Where(r => r.TenantId == tenantId
                && (allowedRequests is null || allowedRequests.Contains(r.RequestId)) && (r.ResourceType != "agent" || allowedInstances is null || allowedInstances.Contains(r.Operation.InstanceId))
                && (r.ResourceType != "credential" || allowedCredentials is null || allowedCredentials.Contains(r.Operation.CredentialCheck!.CredentialId))
                && (page.Status is null || r.Status == page.Status) && (page.Kind is null || r.Kind == page.Kind)).ToArray();
            return ValueTask.FromResult(new TenantApprovalPage(rows.OrderByDescending(r => r.CreatedAt.ToUnixTimeMilliseconds()).ThenBy(r => r.RequestId)
                .Skip(page.Page * page.PageSize).Take(page.PageSize).ToArray(), rows.Length, page.Page, page.PageSize));
        }
    }
    public ValueTask<TenantOperationGrant?> FindOperationGrantAsync(Guid tenantId, Guid requestId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(_operationGrants.GetValueOrDefault(requestId) is { } g && g.TenantId == tenantId ? g : null); }

    public ValueTask<TenantApprovalRequest?> TryCreateApprovalAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantApprovalRequest request, long expectedRunRevision, Guid? generation, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next);
        if (request.TenantId != next.Tenant.TenantId || request.Status != TenantApprovalStatus.Pending || request.Revision != 1
            || next.Tenant.Revision != checked(expectedSecurityRevision + 1) || audit.ActorPrincipalId != request.ExecutorPrincipalId
            || audit.TenantId != request.TenantId || audit.Action != "approvals.request" || audit.Target != request.RequestId.ToString("D")) throw new ArgumentException("Invalid approval request transaction.");
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate)
        {
            if (!_tenants.TryGetValue(request.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult<TenantApprovalRequest?>(null);
            var duplicate = RootApproval(request.TenantId, request.RootActivationId, request.ExecutorPrincipalId, request.Fingerprint);
            if (duplicate is not null) return ValueTask.FromResult<TenantApprovalRequest?>(duplicate);
            if (request.ExpiresAt <= ApprovalNow) throw new SecurityConflictException("approval_expired");
            var run = sessionBacking.ReadTenantRun(request.TenantId, request.RunId);
            var agent = FindTenantAgentAsync(request.TenantId, request.Operation.InstanceId, ct).Result;
            if (run is null || agent is null) return ValueTask.FromResult<TenantApprovalRequest?>(null);
            TenantApprovalRules.Validate(request, run, agent); TenantSessionRules.RequireExecutor(before, agent);
            if (request.Operation.CredentialCheck is not null) RequireCredentialOperation(request, run, agent);
            ValidateAgentActor(before, request.ExecutorPrincipalId);
            if (request.OriginalInitiatorPrincipalId is { } requester) ValidateAgentActor(before, requester);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); EnsureReferences(next, audit, ct: ct);
            if (_approvals.ContainsKey(request.RequestId) || _approvals.Values.Any(r => r.Operation.OperationId == request.Operation.OperationId)) throw new SecurityConflictException("identity_conflict");
            var recent = _approvals.Values.Where(r => r.TenantId == request.TenantId
                && (r.Status == TenantApprovalStatus.Pending || r.CreatedAt >= ApprovalNow - TenantApprovalLimits.Window)).ToArray();
            TenantApprovalLimits.Require(recent.Length, recent.Count(r => r.ExecutorPrincipalId == request.ExecutorPrincipalId),
                recent.Count(r => r.OriginalInitiatorPrincipalId == request.OriginalInitiatorPrincipalId), recent.Count(r => r.Fingerprint == request.Fingerprint),
                _approvals.Values.Count(r => r.TenantId == request.TenantId && r.RootActivationId == request.RootActivationId));
            if (run.Status == AgentCore.Domain.Conversation.AgentRunStatus.Running && run.Claim!.LeaseExpiresAtUtc <= ApprovalNow)
                throw new SecurityConflictException("execution_claim_expired");
            ct.ThrowIfCancellationRequested();
            if (sessionBacking.WaitTenantApproval(request.TenantId, request.RunId, expectedRunRevision, request.RequestId, generation, request.CreatedAt) is null) return ValueTask.FromResult<TenantApprovalRequest?>(null);
            _approvals.Add(request.RequestId, request); _tenants[request.TenantId] = next; _audit.Add(audit);
            return ValueTask.FromResult<TenantApprovalRequest?>(request);
        }
    }
    public ValueTask<bool> TryDecideApprovalAsync(TenantSecurityState next, long expectedSecurityRevision, TenantApprovalRequest request,
        long expectedRequestRevision, TenantApprovalDecision decision, TenantOperationGrant? grant, ApprovalProcessedSignal signal,
        TenantSessionInitiator human, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next);
        if (next.Tenant.TenantId != request.TenantId || next.Tenant.Revision != checked(expectedSecurityRevision + 1)) throw new ArgumentException("Invalid approval decision transaction.");
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate)
        {
            if (!_tenants.TryGetValue(request.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision
                || !_approvals.TryGetValue(request.RequestId, out var old) || old.Revision != expectedRequestRevision || old.Status != TenantApprovalStatus.Pending) return ValueTask.FromResult(false);
            TenantApprovalRules.ValidateDecision(old, request, expectedRequestRevision, decision, grant, signal, human, audit);
            if (old.ExpiresAt <= ApprovalNow) throw new SecurityConflictException("approval_expired");
            RequireHumanSession(request.TenantId, human);
            if (request.Status != TenantApprovalStatus.Cancelled)
            {
                ValidateAgentActor(before, request.ExecutorPrincipalId);
                if (request.OriginalInitiatorPrincipalId is { } requester) ValidateAgentActor(before, requester);
            }
            var run = sessionBacking.ReadTenantRun(request.TenantId, request.RunId);
            var agent = FindTenantAgentAsync(request.TenantId, request.Operation.InstanceId, ct).Result;
            if (run is null || agent is null || run.ApprovalRequestId != request.RequestId || run.Status != AgentCore.Domain.Conversation.AgentRunStatus.WaitingForApproval) return ValueTask.FromResult(false);
            TenantApprovalRules.Validate(request, run, agent, request.Status != TenantApprovalStatus.Cancelled);
            if (request.Operation.CredentialCheck is not null && request.Status != TenantApprovalStatus.Cancelled) RequireCredentialOperation(request, run, agent);
            if (request.Status != TenantApprovalStatus.Cancelled) TenantSessionRules.RequireExecutor(before, agent);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); EnsureReferences(next, audit, ct: ct); ct.ThrowIfCancellationRequested();
            if (_approvalSignals.ContainsKey(signal.SignalId) || _approvalDecisions.Values.Any(d => d.DecisionId == decision.DecisionId)
                || grant is not null && _operationGrants.Values.Any(g => g.GrantId == grant.GrantId || g.EffectIdempotencyKey == grant.EffectIdempotencyKey))
                throw new SecurityConflictException("identity_conflict");
            _approvals[request.RequestId] = request; _approvalDecisions.Add(request.RequestId, decision);
            if (grant is not null) _operationGrants.Add(request.RequestId, grant);
            _approvalSignals.Add(signal.SignalId, signal); _tenants[request.TenantId] = next; _audit.Add(audit); return ValueTask.FromResult(true);
        }
    }
    private void RequireCredentialOperation(TenantApprovalRequest request, AgentCore.Domain.Conversation.AgentRun run, TenantAgent agent)
    {
        var snapshot = ReadTenantCredentialBindingsAsync(request.TenantId, request.Operation.InstanceId).Result;
        var bound = snapshot?.Bindings.SingleOrDefault(b => b.Binding.BindingId == request.Operation.CredentialCheck!.BindingId)
            ?? throw new AccessDeniedException("credential_binding_changed");
        TenantCredentialOperationRules.RequireRequest(request, run, agent, bound);
    }

    public ValueTask<IReadOnlyList<ApprovalProcessedSignal>> PendingApprovalSignalsAsync(int limit, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult<IReadOnlyList<ApprovalProcessedSignal>>(_approvalSignals.Values.Where(s => s.ProcessedAt is null).OrderBy(s => s.CreatedAt).ThenBy(s => s.SignalId).Take(Math.Clamp(limit, 1, 100)).ToArray()); }
    public ValueTask<IReadOnlyList<TenantApprovalRequest>> DueApprovalsAsync(DateTimeOffset at, int limit, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult<IReadOnlyList<TenantApprovalRequest>>(_approvals.Values.Where(r => r.Status == TenantApprovalStatus.Pending && r.ExpiresAt <= at).OrderBy(r => r.ExpiresAt).ThenBy(r => r.RequestId).Take(Math.Clamp(limit, 1, 100)).ToArray()); }
    public ValueTask<bool> TryExpireApprovalAsync(Guid tenantId, Guid requestId, long expectedRevision, ApprovalProcessedSignal signal, DateTimeOffset at, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate)
        {
            if (!_approvals.TryGetValue(requestId, out var request) || request.TenantId != tenantId || request.Revision != expectedRevision
                || request.Status != TenantApprovalStatus.Pending || request.ExpiresAt > at) return ValueTask.FromResult(false);
            var next = TenantApprovalRules.Expire(request, expectedRevision, signal, at);
            if (_approvalSignals.ContainsKey(signal.SignalId)) throw new SecurityConflictException("identity_conflict");
            _approvals[requestId] = next; _approvalSignals.Add(signal.SignalId, signal); return ValueTask.FromResult(true);
        }
    }
    public ValueTask<bool> TryResumeApprovalAsync(ApprovalProcessedSignal signal, DateTimeOffset at, CancellationToken ct = default, TenantApprovalContinuationIds? continuation = null)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate)
        {
            if (!_approvalSignals.TryGetValue(signal.SignalId, out var saved) || saved != signal || saved.ProcessedAt is not null || at < saved.CreatedAt
                || !_approvals.TryGetValue(signal.RequestId, out var request) || request.TenantId != signal.TenantId || request.Revision != signal.DecisionRevision
                || request.Status == TenantApprovalStatus.Pending) return ValueTask.FromResult(false);
            var source = sessionBacking.ReadTenantRun(signal.TenantId, signal.RunId);
            if (source is null || request.RunId != source.AgentRunId || source.ApprovalRequestId != request.RequestId) return ValueTask.FromResult(false);
            if (!source.IsTerminal && source.Checkpoint is not null)
            {
                if (continuation is null) return ValueTask.FromResult(false);
                var next = TenantApprovalContinuationRules.Create(source, request, signal, continuation, at);
                ct.ThrowIfCancellationRequested(); sessionBacking.AdmitTenantApprovalContinuation(source, next, request.RequestId, at);
                _audit.Add(new(signal.SignalId, signal.TenantId, request.ExecutorPrincipalId, "agent-runs.approval-continuation", next.AgentRunId.ToString("D"), "allowed", "approval_continued", at));
            }
            else if (!sessionBacking.ResumeTenantApproval(signal.TenantId, signal.RunId, signal.RequestId, at)) return ValueTask.FromResult(false);
            _approvalSignals[signal.SignalId] = signal with { Revision = checked(signal.Revision + 1), ProcessedAt = at }; return ValueTask.FromResult(true);
        }
    }
}
