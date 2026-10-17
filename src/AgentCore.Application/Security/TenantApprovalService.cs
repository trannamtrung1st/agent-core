using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantApprovalPreparationResult(OperationDecisionKind Decision, string ReasonCode,
    PreparedPersonaOperation Operation, TenantApprovalRequest? Request);
public sealed record TenantApprovalView(TenantApprovalRequest Request, long SecurityRevision);

public sealed class TenantApprovalService(IIamStore security, ITenantApprovalStore approvals, ITenantAgentStore agents,
    TenantOperationAuthorizer operations, IamAuthorizer authorizer, IdentityService identity, IIdGenerator ids, TimeProvider time)
{
    // Trusted internal typed preparation. No model-supplied tenant, principal, permission, grant or arbitrary action JSON.
    public async ValueTask<TenantApprovalPreparationResult> PreparePersonaAsync(TenantRunReference reference, AgentIdentity persona,
        long expectedRunRevision, Guid? generation = null, CancellationToken ct = default)
    {
        var (run, state) = await operations.ResolveAsync(reference, ct).ConfigureAwait(false);
        var agent = await agents.FindTenantAgentAsync(reference.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("executor_unavailable");
        var operation = TenantApprovalRules.Prepare(ids.NewId(), agent.Instance.InstanceId, persona, agent.Instance.Revision, agent.Instance.PersonaRevision);
        var existing = await approvals.FindRootApprovalAsync(reference.TenantId, run.Admission.Activation.RootActivationId, agent.PrincipalId, operation.ActionHash, ct).ConfigureAwait(false);
        if (existing is not null) return new(existing.Status == TenantApprovalStatus.Pending ? OperationDecisionKind.RequiresApproval : OperationDecisionKind.Denied,
            TenantApprovalRules.DuplicateReason(existing), existing.Operation, existing);
        var decision = await operations.Check(state, run.Admission.ExecutionContext!, TenantApprovalRequest.Permission,
            AccessScope.ForResource(reference.TenantId, TenantApprovalRequest.ResourceType, operation.InstanceId.ToString("D")), ct).ConfigureAwait(false);
        if (decision.Decision != OperationDecisionKind.RequiresApproval) return new(decision.Decision, decision.ReasonCode, operation, null);
        var at = time.GetUtcNow();
        var request = new TenantApprovalRequest(ids.NewId(), reference.TenantId, run.AgentRunId, run.SessionId, run.Admission.Activation.RootActivationId,
            agent.PrincipalId, run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, decision.ApprovalKind!.Value,
            operation, TenantApprovalStatus.Pending, 1, at, at.AddHours(1));
        TenantApprovalRules.Validate(request, run, agent);
        var next = Next(state);
        var audit = new AuthorizationAudit(ids.NewId(), reference.TenantId, agent.PrincipalId, "approvals.request", request.RequestId.ToString("D"), "allowed", "approval_pending", at);
        var saved = await approvals.TryCreateApprovalAsync(next, state.Tenant.Revision, request, expectedRunRevision, generation, audit, ct).ConfigureAwait(false);
        if (saved is null)
        {
            // A concurrent same-root preparer may have won security/Run CAS. Return its durable request after fresh identity checks.
            var (current, _) = await operations.ResolveAsync(reference, ct).ConfigureAwait(false);
            saved = await approvals.FindRootApprovalAsync(reference.TenantId, current.Admission.Activation.RootActivationId,
                current.Admission.ExecutionContext!.AgentPrincipalId, operation.ActionHash, ct).ConfigureAwait(false)
                ?? throw new SecurityConflictException("stale_revision");
        }
        return new(saved.Status == TenantApprovalStatus.Pending ? OperationDecisionKind.RequiresApproval : OperationDecisionKind.Denied,
            saved.RequestId == request.RequestId ? "approval_pending" : TenantApprovalRules.DuplicateReason(saved), saved.Operation, saved);
    }

    public async ValueTask<TenantApprovalRequest> ReadAsync(Guid tenant, Guid requestId, TenantSessionInitiator human, CancellationToken ct = default)
        => (await ReadViewAsync(tenant, requestId, human, ct).ConfigureAwait(false)).Request;

    public async ValueTask<TenantApprovalView> ReadViewAsync(Guid tenant, Guid requestId, TenantSessionInitiator human, CancellationToken ct = default)
    {
        await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var request = await approvals.FindApprovalAsync(tenant, requestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        await RequireRead(state, human.PrincipalId, request, ct).ConfigureAwait(false); return new(request, state.Tenant.Revision);
    }

    public async ValueTask<IReadOnlyList<TenantApprovalView>> ListAsync(Guid tenant, TenantSessionInitiator human, int limit, CancellationToken ct = default)
    {
        var before = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var requestIds = await VisibleIds(before, human.PrincipalId, "approvals.read", "approval", ct).ConfigureAwait(false);
        var instanceIds = await VisibleIds(before, human.PrincipalId, TenantApprovalRequest.Permission, TenantApprovalRequest.ResourceType, ct).ConfigureAwait(false);
        var requests = await approvals.ListApprovalsAsync(tenant, requestIds, instanceIds, limit, ct).ConfigureAwait(false);
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var result = new List<TenantApprovalView>();
        foreach (var request in requests)
        {
            try { await RequireRead(state, human.PrincipalId, request, ct).ConfigureAwait(false); result.Add(new(request, state.Tenant.Revision)); }
            catch (AccessDeniedException) { }
        }
        return result;
    }

    private async ValueTask<IReadOnlyList<Guid>?> VisibleIds(TenantSecurityState state, Guid human, string permission, string type, CancellationToken ct)
    {
        var metadata = PermissionCatalog.Get(permission);
        var inherited = await authorizer.CheckTenantAsync(state, human, metadata, AccessScope.ForTenant(state.Tenant.TenantId), ct).ConfigureAwait(false);
        if (inherited.Allowed) return null;
        if (inherited.ReasonCode != "permission_denied") throw new AccessDeniedException(inherited.ReasonCode);
        var candidates = state.Assignments.Where(a => a.PrincipalId == human && a.Scope.ResourceType == type
            && state.Roles.Any(r => r.RoleId == a.RoleId && r.Permissions.Contains(permission, StringComparer.Ordinal))).Select(a => a.Scope).Distinct();
        var visible = new List<Guid>();
        foreach (var scope in candidates)
            if ((await authorizer.CheckTenantAsync(state, human, metadata, scope, ct).ConfigureAwait(false)).Allowed && Guid.TryParse(scope.ResourceId, out var id)) visible.Add(id);
        return visible;
    }

    public ValueTask<TenantApprovalRequest> DecideAsync(Guid tenant, Guid requestId, TenantSessionInitiator human,
        bool approve, string actionHash, long expectedSecurityRevision, long expectedRequestRevision, CancellationToken ct = default)
        => ChangeAsync(tenant, requestId, human, approve ? TenantApprovalStatus.Approved : TenantApprovalStatus.Rejected, actionHash, expectedSecurityRevision, expectedRequestRevision, ct);

    public ValueTask<TenantApprovalRequest> CancelAsync(Guid tenant, Guid requestId, TenantSessionInitiator human,
        string actionHash, long expectedSecurityRevision, long expectedRequestRevision, CancellationToken ct = default)
        => ChangeAsync(tenant, requestId, human, TenantApprovalStatus.Cancelled, actionHash, expectedSecurityRevision, expectedRequestRevision, ct);

    private async ValueTask<TenantApprovalRequest> ChangeAsync(Guid tenant, Guid requestId, TenantSessionInitiator human,
        TenantApprovalStatus outcome, string actionHash, long expectedSecurityRevision, long expectedRequestRevision, CancellationToken ct)
    {
        if (actionHash is not { Length: 64 } || actionHash.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("An exact bounded action hash is required.");
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var request = await approvals.FindApprovalAsync(tenant, requestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        if (outcome == TenantApprovalStatus.Cancelled)
        {
            var allowed = await authorizer.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("approvals.cancel"),
                AccessScope.ForResource(tenant, "approval", requestId.ToString("D")), ct).ConfigureAwait(false);
            if (!allowed.Allowed) throw new AccessDeniedException("approval_unavailable");
        }
        else await RequireRead(state, human.PrincipalId, request, ct).ConfigureAwait(false);
        if (state.Tenant.Revision != expectedSecurityRevision || request.Revision != expectedRequestRevision || request.Status != TenantApprovalStatus.Pending)
            throw new SecurityConflictException("stale_revision");
        if (!string.Equals(request.Operation.ActionHash, actionHash, StringComparison.Ordinal)) throw new SecurityConflictException("operation_mismatch");
        var at = time.GetUtcNow();
        if (at >= request.ExpiresAt) throw new SecurityConflictException("approval_expired");
        if (outcome != TenantApprovalStatus.Cancelled)
        {
            var (run, current) = await operations.ResolveAsync(new(tenant, request.RunId), ct).ConfigureAwait(false);
            if (current.Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
            var agent = await agents.FindTenantAgentAsync(tenant, request.Operation.InstanceId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("executor_unavailable");
            TenantApprovalRules.Validate(request, run, agent);
            var target = AccessScope.ForResource(tenant, TenantApprovalRequest.ResourceType, request.ResourceId);
            var eligibility = await operations.Check(state, run.Admission.ExecutionContext!, TenantApprovalRequest.Permission, target, ct).ConfigureAwait(false);
            if (eligibility.Decision == OperationDecisionKind.Denied || request.Kind == ApprovalKind.Review && eligibility.ApprovalKind == ApprovalKind.ExceptionalGrant)
                throw new AccessDeniedException("approval_authority_changed");
            if (!await operations.QualifiesAsync(state, request.OriginalInitiatorPrincipalId, human.PrincipalId,
                PermissionCatalog.Get(TenantApprovalRequest.Permission), target, request.Kind, ct).ConfigureAwait(false)) throw new AccessDeniedException("approver_unqualified");
        }
        var nextRequest = request.Decide(outcome, expectedRequestRevision, at);
        var decision = new TenantApprovalDecision(ids.NewId(), requestId, tenant, human.PrincipalId, nextRequest.Status, actionHash, nextRequest.Revision, at);
        var grant = outcome == TenantApprovalStatus.Approved && request.Kind == ApprovalKind.ExceptionalGrant
            ? new TenantOperationGrant(ids.NewId(), requestId, tenant, request.ExecutorPrincipalId, request.Operation.OperationId, actionHash,
                "operation:" + request.Operation.OperationId.ToString("D"), OperationGrantStatus.Available, 1, request.ExpiresAt) : null;
        var signal = new ApprovalProcessedSignal(ids.NewId(), tenant, requestId, request.RunId, nextRequest.Revision, 1, at, null);
        var audit = new AuthorizationAudit(ids.NewId(), tenant, human.PrincipalId, "approvals.decide", requestId.ToString("D"), "allowed",
            outcome switch { TenantApprovalStatus.Approved => "approval_approved", TenantApprovalStatus.Cancelled => "approval_cancelled", _ => "approval_rejected" }, at);
        if (!await approvals.TryDecideApprovalAsync(Next(state), state.Tenant.Revision, nextRequest, expectedRequestRevision, decision, grant, signal, human, audit, ct).ConfigureAwait(false))
            throw new SecurityConflictException("stale_revision");
        return nextRequest;
    }

    public async ValueTask<int> DispatchPendingAsync(CancellationToken ct = default)
    {
        var at = time.GetUtcNow();
        foreach (var request in await approvals.DueApprovalsAsync(at, 100, ct).ConfigureAwait(false))
            await approvals.TryExpireApprovalAsync(request.TenantId, request.RequestId, request.Revision,
                new(ids.NewId(), request.TenantId, request.RequestId, request.RunId, checked(request.Revision + 1), 1, at, null), at, ct).ConfigureAwait(false);
        var count = 0;
        foreach (var signal in await approvals.PendingApprovalSignalsAsync(100, ct).ConfigureAwait(false))
            if (await approvals.TryResumeApprovalAsync(signal, time.GetUtcNow(), ct).ConfigureAwait(false)) count++;
        return count;
    }

    private async ValueTask RequireRead(TenantSecurityState state, Guid human, TenantApprovalRequest request, CancellationToken ct)
    {
        foreach (var pair in new[] { ("approvals.read", AccessScope.ForResource(request.TenantId, "approval", request.RequestId.ToString("D"))),
            (TenantApprovalRequest.Permission, AccessScope.ForResource(request.TenantId, TenantApprovalRequest.ResourceType, request.ResourceId)) })
        {
            var decision = await authorizer.CheckTenantAsync(state, human, PermissionCatalog.Get(pair.Item1), pair.Item2, ct).ConfigureAwait(false);
            if (!decision.Allowed) throw new AccessDeniedException("approval_unavailable");
        }
    }
    private static TenantSecurityState Next(TenantSecurityState state) => state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
}
