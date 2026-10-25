using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantApprovalService
{
    public async ValueTask<TenantApprovalPreparationResult> PrepareBackgroundReportAsync(TenantBackgroundCompletion source, CancellationToken ct)
    {
        try { return await PrepareBackgroundReportCoreAsync(source, ct).ConfigureAwait(false); }
        catch (Exception error) when (error is (AccessDeniedException or SecurityConflictException) && error is not AccessDeniedException { ReasonCode: "completion_read_review_required" })
        {
            await operations.RecordFailureAsync(new(source.Child.Owner.TenantId!.Value, source.Child.AgentRunId), "agent-runs.create",
                error is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)error).ReasonCode, ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false);
            throw;
        }
    }
    private async ValueTask<TenantApprovalPreparationResult> PrepareBackgroundReportCoreAsync(TenantBackgroundCompletion source, CancellationToken ct)
    {
        var target = backgroundReports ?? throw new AccessDeniedException("tenant_effect_unavailable");
        var current = await target.ResolveAsync(source.Child.Owner, source.Child.AgentRunId, ct).ConfigureAwait(false);
        source = current.Source; var run = source.Child; var context = run.Admission.ExecutionContext!; var model = current.Model; var at = time.GetUtcNow();
        var p = new PreparedBackgroundReport(context.TenantId, run.SessionId, run.AgentRunId, run.Revision, source.Parent!.Owner.SessionId,
            source.Parent.Snapshot.Revision, source.Parent.Snapshot.DurableLastEntrySequence, ids.NewId(), ids.NewId(), ids.NewId(),
            BackgroundCompletionProjection.Build(run, run.Admission.TaskIntent?.Text ?? source.ChildSession.Snapshot.Title, [], source.Parent.Snapshot.DurableLastEntrySequence),
            TenantBackgroundCreationRules.Hash(current.Configuration), new(model.OfferingId, model.ConnectionId, model.ExternalModelId, model.ReasoningEffort, model.Binding),
            source.Parent.Snapshot.ContextSelection ?? throw new AccessDeniedException("space_context_required"));
        var op = TenantApprovalRules.PrepareBackgroundReport(ids.NewId(), run.AgentInstanceId, p, current.Agent.Instance.Revision, current.Agent.Instance.PersonaRevision);
        op = TenantApprovalRules.ForRun(op, run);
        var prior = await approvals.FindRootApprovalAsync(context.TenantId, context.RootActivationId, context.AgentPrincipalId, op, ct).ConfigureAwait(false);
        if (prior is not null) return new(OperationDecisionKind.RequiresApproval, TenantApprovalRules.DuplicateReason(prior), prior.Operation, prior);
        var eligibility = await operations.Evaluate(current.State, context, "agent-runs.create", AccessScope.ForTenant(context.TenantId), ct).ConfigureAwait(false);
        if (eligibility.Decision == OperationDecisionKind.Allowed && await TenantCompletionDestinationRules.NeedsCommunicationReviewAsync(current.State, source, authorizer, ct).ConfigureAwait(false))
            eligibility = eligibility with { Decision = OperationDecisionKind.RequiresApproval, ApprovalKind = ApprovalKind.Review, ReasonCode = "review_required" };
        if (eligibility.Decision != OperationDecisionKind.RequiresApproval)
            return await PreparationOutcome(current.State, run, op, eligibility.Decision, eligibility.ReasonCode, null, ct).ConfigureAwait(false);
        var request = new TenantApprovalRequest(ids.NewId(), context.TenantId, run.AgentRunId, run.SessionId, context.RootActivationId, context.AgentPrincipalId,
            context.OriginalInitiatorPrincipalId, eligibility.ApprovalKind!.Value, op, TenantApprovalStatus.Pending, 1, at, at.AddHours(1), new(current.Agent.Instance.Persona.Name));
        _ = await target.RequireAsync(request, ct).ConfigureAwait(false);
        var qualified = false;
        foreach (var user in current.State.Users.Where(u => u.Status == IdentityStatus.Active))
        {
            try { await TenantBackgroundReportRules.ReviewerAsync(current.State, request, user.PrincipalId, authorizer, ct).ConfigureAwait(false); qualified = true; break; }
            catch (AccessDeniedException) { }
        }
        if (!qualified) return await PreparationOutcome(current.State, run, op, OperationDecisionKind.Denied, "no_eligible_approver", null, ct).ConfigureAwait(false);
        TenantApprovalRequest saved;
        try
        {
            saved = await approvals.TryCreateApprovalAsync(Next(current.State), current.State.Tenant.Revision, request, run.Revision, null,
                new(ids.NewId(), context.TenantId, context.AgentPrincipalId, "approvals.request", request.RequestId.ToString("D"), "allowed", "approval_pending", at), ct).ConfigureAwait(false)
            ?? throw new SecurityConflictException("stale_revision");
        }
        catch (SecurityConflictException error) when (error.ReasonCode is "approval_rate_limited" or "approval_root_limit")
        { return await PreparationOutcome(current.State, run, op, OperationDecisionKind.Denied, error.ReasonCode, null, ct).ConfigureAwait(false); }
        return new(OperationDecisionKind.RequiresApproval, saved.RequestId == request.RequestId ? "approval_pending" : TenantApprovalRules.DuplicateReason(saved), saved.Operation, saved);
    }

    private async ValueTask<bool> ProcessBackgroundReportAsync(ApprovalProcessedSignal signal, TenantApprovalRequest request, CancellationToken ct)
    {
        var runtime = reportRuntime ?? throw new AccessDeniedException("tenant_effect_unavailable");
        AgentRun? report = null; string? refusal = null;
        if (request.Status == TenantApprovalStatus.Approved)
        {
            try
            {
                var current = await (backgroundReports ?? throw new AccessDeniedException("tenant_effect_unavailable")).RequireAsync(request, ct).ConfigureAwait(false);
                var decision = await effects.FindApprovalDecisionAsync(request.TenantId, request.RequestId, ct).ConfigureAwait(false)
                    ?? throw new AccessDeniedException("approval_unavailable");
                TenantBackgroundReportRules.Decision(request, decision, await approvals.FindOperationGrantAsync(request.TenantId, request.RequestId, ct).ConfigureAwait(false), time.GetUtcNow());
                await TenantBackgroundReportRules.ReviewerAsync(current.State, request, decision.HumanPrincipalId, authorizer, ct).ConfigureAwait(false);
                report = TenantBackgroundReportRules.Build(request, current.Source, current.Configuration);
            }
            catch (AccessDeniedException error) { refusal = error.ReasonCode; }
            catch (SecurityConflictException error) { refusal = error.ReasonCode; }
        }
        return await runtime.SettleTenantBackgroundReportApprovalAsync(signal, report, refusal, ids.NewId(), ct).ConfigureAwait(false);
    }
}
