using AgentCore.Application.Ports;
using AgentCore.Application.Tasks;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;

namespace AgentCore.Application.Security;

public sealed record TenantHumanTaskPreparation(TaskMutationResult? Task, TenantApprovalRequest? Approval,
    long SecurityRevision, bool Replayed = false);

public sealed partial class TenantApprovalService
{
    private async ValueTask<TenantApprovalReview> ReviewHumanTaskAsync(TenantApprovalView view, TenantSecurityState state,
        TenantSessionInitiator human, CancellationToken ct)
    {
        var request = view.Request; TenantTaskConsentRules.RequireHumanShape(request); var at = time.GetUtcNow();
        var independent = await TenantTaskConsentRules.IndependentAsync(state, request, authorizer, ct);
        string? unavailable = request.Status != TenantApprovalStatus.Pending ? "request_terminal" : request.ExpiresAt <= at ? "approval_expired" : null;
        if (unavailable is null)
        {
            try
            {
                await (taskTargets ?? throw new AccessDeniedException("task_consent_unavailable")).RequireHumanAsync(request, state, ct);
                await TenantTaskConsentRules.ReviewerAsync(state, request, human.PrincipalId, authorizer, ct);
            }
            catch (AccessDeniedException error) { unavailable = error.ReasonCode; }
            catch (SecurityConflictException error) when (error.ReasonCode != "stale_revision") { unavailable = error.ReasonCode; }
        }
        var cancel = request.Status == TenantApprovalStatus.Pending && at < request.ExpiresAt
            && (await authorizer.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("approvals.cancel"),
                AccessScope.ForResource(request.TenantId, "approval", request.RequestId.ToString("D")), ct)).Allowed;
        async ValueTask<string?> Name(Guid? principal)
        {
            var user = state.Users.SingleOrDefault(u => u.PrincipalId == principal);
            return user is null ? null : (await security.ReadAccountAsync(user.AccountId, ct))?.DisplayName;
        }
        var execution = view.HumanTaskOutcome is not null ? "recorded" : request.Status == TenantApprovalStatus.Pending ? "waitingDecision"
            : request.Status == TenantApprovalStatus.Approved ? "unrecorded" : "notAuthorized";
        return new(request.Snapshot?.AgentName ?? "Source conversation Agent", await Name(request.OriginalInitiatorPrincipalId), await Name(view.Decision?.HumanPrincipalId),
            [new("Source actor", null, "Human supervisor", false), ..await PresentedChangesAsync(request.Operation, request.Snapshot, ct)],
            unavailable is null, cancel, unavailable, independent, at, execution);
    }
    private async ValueTask<TenantApprovalRequest> ChangeHumanTaskAsync(TenantSecurityState state, TenantApprovalRequest request,
        TenantSessionInitiator human, TenantApprovalStatus outcome, CancellationToken ct)
    {
        TenantTaskConsentRules.RequireHumanShape(request);
        if (outcome != TenantApprovalStatus.Cancelled)
        {
            await (taskTargets ?? throw new AccessDeniedException("task_consent_unavailable")).RequireHumanAsync(request, state, ct);
            await TenantTaskConsentRules.ReviewerAsync(state, request, human.PrincipalId, authorizer, ct);
        }
        var at = time.GetUtcNow(); var nextRequest = request.Decide(outcome, request.Revision, at);
        var decision = new TenantApprovalDecision(ids.NewId(), request.RequestId, request.TenantId, human.PrincipalId,
            nextRequest.Status, request.Operation.ActionHash, nextRequest.Revision, at);
        var signal = new ApprovalProcessedSignal(ids.NewId(), request.TenantId, request.RequestId, request.RunId, nextRequest.Revision, 1, at, null);
        var audit = new AuthorizationAudit(ids.NewId(), request.TenantId, human.PrincipalId, "approvals.decide", request.RequestId.ToString("D"), "allowed",
            outcome switch { TenantApprovalStatus.Approved => "approval_approved", TenantApprovalStatus.Cancelled => "approval_cancelled", _ => "approval_rejected" }, at);
        if (!await approvals.TryDecideApprovalAsync(Next(state), state.Tenant.Revision, nextRequest, request.Revision, decision, null, signal, human, audit, ct))
            throw new SecurityConflictException("stale_revision");
        return nextRequest;
    }
    private async ValueTask<bool> ProcessHumanTaskAsync(ApprovalProcessedSignal signal, TenantApprovalRequest request, CancellationToken ct)
    {
        var state = await security.ReadTenantAsync(request.TenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        TenantHumanTaskAdmission? admission = null; string? refusal = null;
        if (request.Status == TenantApprovalStatus.Approved)
        {
            try
            {
                var current = await (taskTargets ?? throw new AccessDeniedException("task_consent_unavailable")).RequireHumanAsync(request, state, ct);
                var decision = await effects.FindApprovalDecisionAsync(request.TenantId, request.RequestId, ct) ?? throw new AccessDeniedException("task_consent_unavailable");
                await TenantTaskConsentRules.ReviewerAsync(state, request, decision.HumanPrincipalId, authorizer, ct);
                admission = TenantTaskDelegationTarget.BuildHuman(request, current, state.Tenant.Revision, ids.NewId());
            }
            catch (AccessDeniedException error) { refusal = error.ReasonCode; }
            catch (SecurityConflictException error) { refusal = error.ReasonCode; }
        }
        return await (humanTaskApprovals ?? throw new AccessDeniedException("task_consent_unavailable"))
            .TrySettleHumanTaskApprovalAsync(signal, admission, refusal, state.Tenant.Revision, ct);
    }
    public async ValueTask<TenantHumanTaskPreparation> PrepareHumanTaskDelegationAsync(Guid tenant, Guid session,
        HumanSessionReference human, TaskRequest request, long expectedSecurityRevision, CancellationToken ct = default)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct);
        var taskOwner = security as ITenantTaskStore ?? throw new AccessDeniedException("tenant_effect_unavailable");
        if (await taskOwner.FindTaskRequestAsync(tenant, session, new(Human: human), request.RequestKey, ct) is { } previousTask)
        {
            if (previousTask.Task.Request.Fingerprint() != request.Fingerprint()) throw new SecurityConflictException("task_request_conflict");
            return new(previousTask, null, previousTask.SecurityRevision, true);
        }
        if (await approvals.FindTaskDelegationApprovalAsync(tenant, $"human:{human.PrincipalId:D}:session:{session:D}", request.RequestKey, ct) is { } previousApproval)
        {
            TenantApprovalRules.MatchTaskDelegationRequest(previousApproval, request);
            return new(null, previousApproval, state.Tenant.Revision, true);
        }
        if (state.Tenant.Revision != expectedSecurityRevision) throw new SecurityConflictException("stale_revision");
        var prepared = await (taskDelegations ?? throw new AccessDeniedException("tenant_effect_unavailable"))
            .PrepareAsync(tenant, session, new(Human: human), request, expectedSecurityRevision, ct);
        if (prepared.Replay is { } replay) return new(replay, null, replay.SecurityRevision, true);
        var admission = prepared.Admission ?? throw new AccessDeniedException("task_preparation_unavailable");
        if (await approvals.FindTaskDelegationApprovalAsync(tenant, admission.Task.Source.OperationScope, request.RequestKey, ct) is { } previous)
        {
            TenantApprovalRules.MatchTaskDelegationRequest(previous, request);
            return new(null, previous, state.Tenant.Revision, true);
        }
        if (!await TenantTaskConsentRules.HumanNeedsReviewAsync(state, admission.Task, authorizer, ct))
        {
            var saved = await (security as ITenantTaskStore ?? throw new AccessDeniedException("tenant_effect_unavailable")).AdmitTaskAsync(admission, ct);
            return new(saved, null, saved.SecurityRevision, saved.Replayed);
        }
        var sourceAgent = await agents.FindTenantAgentAsync(tenant, admission.Task.Source.InstanceId, ct) ?? throw new AccessDeniedException("task_source_unavailable");
        var reservation = new PreparedTaskDelegation(admission.Task, null, admission.WorkerSession?.Entries.Single().EntryId,
            admission.WorkerRun?.ActivationId, admission.WorkerRun?.ResponseId,
            admission.Task.State == DelegatedTaskState.PendingDependencies ? ids.NewId() : null);
        var operation = TenantApprovalRules.PrepareTaskDelegation(ids.NewId(), sourceAgent.Instance.InstanceId, reservation,
            sourceAgent.Instance.Revision, sourceAgent.Instance.PersonaRevision);
        var at = DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds());
        var expires = at.AddHours(1) < admission.Task.DeadlineAt ? at.AddHours(1) : admission.Task.DeadlineAt;
        if (expires <= at) throw new AccessDeniedException("task_deadline_exceeded");
        // RunId is the reserved future worker identity in this detached purpose. The
        // indexed source Run columns remain null, just as for occurrence admission.
        var approval = new TenantApprovalRequest(ids.NewId(), tenant, reservation.PlannedWorkerRunId, session,
            admission.Task.Source.RootActivationId, human.PrincipalId, human.PrincipalId, ApprovalKind.Review,
            operation, TenantApprovalStatus.Pending, 1, at, expires, new(sourceAgent.Instance.Persona.Name));
        TenantTaskConsentRules.RequireHumanShape(approval);
        var qualified = false;
        foreach (var candidate in state.Users.Where(u => u.Status == IdentityStatus.Active))
        {
            try { await TenantTaskConsentRules.ReviewerAsync(state, approval, candidate.PrincipalId, authorizer, ct); qualified = true; break; }
            catch (AccessDeniedException) { }
        }
        if (!qualified) throw new AccessDeniedException("no_eligible_approver");
        var savedApproval = await (humanTaskApprovals ?? throw new AccessDeniedException("task_review_unavailable"))
            .TryPrepareHumanTaskApprovalAsync(admission, approval, ct) ?? throw new SecurityConflictException("stale_revision");
        TenantApprovalRules.MatchTaskDelegationRequest(savedApproval, request);
        return new(null, savedApproval, checked(state.Tenant.Revision + 1), savedApproval.RequestId != approval.RequestId);
    }
}
