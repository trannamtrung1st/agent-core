using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantApprovalService
{
    public async ValueTask<TenantApprovalPreparationResult> PrepareReportSourceReadAsync(TenantBackgroundCompletion source, CancellationToken ct)
    {
        var reads = reportSourceReads;
        if (reads is null) return new(OperationDecisionKind.Allowed, "read_checked_at_admission", TenantReportSourceReadRules.Lookup(source), null);
        var current = await reads.ResolveAsync(source, ct).ConfigureAwait(false); source = current.Source;
        var context = source.Child.Admission.ExecutionContext!;
        var op = TenantApprovalRules.PrepareReportSourceRead(ids.NewId(), source.Child.AgentInstanceId, TenantReportSourceReadRules.Witness(source),
            current.Agent.Instance.Revision, current.Agent.Instance.PersonaRevision);
        var prior = await reads.PriorAsync(source, ct).ConfigureAwait(false);
        if (prior is not null)
        {
            if (prior.Status != TenantApprovalStatus.Approved)
                return new(prior.Status == TenantApprovalStatus.Pending ? OperationDecisionKind.RequiresApproval : OperationDecisionKind.Denied,
                    TenantApprovalRules.DuplicateReason(prior), prior.Operation, prior);
            try { await reads.RequireAuthorizedAsync(source, current.State, ct).ConfigureAwait(false); return new(OperationDecisionKind.Allowed, TenantReportSourceReadRules.Reason, prior.Operation, null); }
            catch (AccessDeniedException error) when (error.ReasonCode == "completion_read_review_required")
            { return new(OperationDecisionKind.RequiresApproval, error.ReasonCode, prior.Operation, prior); }
        }
        if (!await TenantReportReadRules.NeedsReviewAsync(current.State, source, authorizer, ct).ConfigureAwait(false))
            return new(OperationDecisionKind.Allowed, "operation_allowed", op, null);
        var at = time.GetUtcNow();
        var request = new TenantApprovalRequest(ids.NewId(), context.TenantId, source.Child.AgentRunId, source.Child.SessionId,
            context.RootActivationId, context.AgentPrincipalId, context.OriginalInitiatorPrincipalId, ApprovalKind.Review, op,
            TenantApprovalStatus.Pending, 1, at, at.AddHours(1), new(current.Agent.Instance.Persona.Name));
        await reads.RequireAsync(request, ct, pending: true).ConfigureAwait(false);
        var qualified = false;
        foreach (var user in current.State.Users.Where(u => u.Status == IdentityStatus.Active))
        {
            try { await TenantReportSourceReadRules.ReviewerAsync(current.State, request, user.PrincipalId, authorizer, ct).ConfigureAwait(false); qualified = true; break; }
            catch (AccessDeniedException) { }
        }
        if (!qualified) return await PreparationOutcome(current.State, source.Child, op, OperationDecisionKind.Denied, "no_eligible_approver", null, ct).ConfigureAwait(false);
        try
        {
            var saved = await approvals.TryCreateApprovalAsync(Next(current.State), current.State.Tenant.Revision, request, source.Child.Revision, null,
                new(ids.NewId(), request.TenantId, request.ExecutorPrincipalId, "approvals.request", request.RequestId.ToString("D"), "allowed", "approval_pending", at), ct).ConfigureAwait(false)
                ?? throw new SecurityConflictException("stale_revision");
            return new(OperationDecisionKind.RequiresApproval, "approval_pending", saved.Operation, saved);
        }
        catch (SecurityConflictException error) when (error.ReasonCode is "approval_rate_limited" or "approval_root_limit")
        { return await PreparationOutcome(current.State, source.Child, op, OperationDecisionKind.Denied, error.ReasonCode, null, ct).ConfigureAwait(false); }
    }
}
