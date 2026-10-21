using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// Stateless proof checks. Requests, decisions, receipts and scheduling remain with their incumbent owners.
public sealed class TenantReportSourceReadService(ITenantRuntimeStore runtime, ITenantRunStore runs, IIamStore security,
    ITenantApprovalStore approvals, ITenantApprovedOperationStore effects, TenantRunConfigurationResolver configurations, IamAuthorizer iam)
{
    public async ValueTask<(TenantBackgroundCompletion Source, TenantSecurityState State, TenantAgent Agent)> ResolveAsync(TenantBackgroundCompletion source, CancellationToken ct)
    {
        source = await runtime.ReadTenantBackgroundCompletionAsync(source.Child.Owner, source.Child.AgentRunId, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("completion_unavailable");
        TenantBackgroundCompletionFactory.RequireParent(source);
        var current = await configurations.ResolveAsync(source.Child.Owner.TenantId!.Value, source.Child.AgentInstanceId, ct).ConfigureAwait(false);
        var state = await security.ReadTenantAsync(source.Child.Owner.TenantId.Value, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        TenantSessionRules.RequireExecutor(state, current.Agent);
        await TenantExecutionLineage.RequireRequesterAsync(source.Child, state, iam, runs, ct).ConfigureAwait(false);
        await TenantReportReadRules.RequireRawAsync(state, source, iam, ct).ConfigureAwait(false);
        var root = await TenantExecutionLineage.RootAsync(source.Child, runs, ct).ConfigureAwait(false);
        if (!TenantCompletionDestinationRules.Allows(source, root, current.Configuration.Configuration.Definition))
            throw new AccessDeniedException("background_unavailable");
        return (source, state, current.Agent);
    }
    public async ValueTask RequireAsync(TenantApprovalRequest request, CancellationToken ct, bool pending = false)
    {
        var source = await runtime.ReadTenantBackgroundCompletionAsync(AgentRunOwner.ForTenant(request.TenantId, request.Operation.InstanceId, request.ExecutorPrincipalId), request.RunId, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("completion_unavailable");
        var current = await ResolveAsync(source, ct).ConfigureAwait(false);
        var resolved = await configurations.ResolveAsync(request.TenantId, request.Operation.InstanceId, ct).ConfigureAwait(false);
        var root = await TenantExecutionLineage.RootAsync(source.Child, runs, ct).ConfigureAwait(false);
        TenantReportSourceReadRules.Require(request, current.Source, current.Agent, resolved.Configuration.Configuration.Definition, root, pending);
        if ((await security.ReadTenantAsync(request.TenantId, ct).ConfigureAwait(false))?.Tenant.Revision != current.State.Tenant.Revision)
            throw new SecurityConflictException("stale_revision");
    }
    public async ValueTask RequireInspectionAsync(TenantApprovalRequest request, TenantSecurityState state, Guid human, CancellationToken ct)
    {
        await TenantReportSourceReadRules.ReadAsync(state, request, human, iam, ct).ConfigureAwait(false);
        var source = await runtime.ReadTenantBackgroundCompletionAsync(AgentRunOwner.ForTenant(request.TenantId, request.Operation.InstanceId, request.ExecutorPrincipalId), request.RunId, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("approval_unavailable");
        TenantReportSourceReadRules.Source(request, source.Child);
        if (source.Parent is null || request.Operation.ReportSourceRead != TenantReportSourceReadRules.Witness(source)) throw new AccessDeniedException("approval_unavailable");
    }
    public async ValueTask<TenantApprovalRequest?> PriorAsync(TenantBackgroundCompletion source, CancellationToken ct) =>
        await approvals.FindRootApprovalAsync(source.Child.Owner.TenantId!.Value, source.Child.Admission.ExecutionContext!.RootActivationId,
            source.Child.Owner.ExecutorPrincipalId!.Value, TenantReportSourceReadRules.Lookup(source), ct).ConfigureAwait(false);
    public async ValueTask RequireAuthorizedAsync(TenantBackgroundCompletion source, TenantSecurityState state, CancellationToken ct)
    {
        TenantBackgroundCompletionFactory.RequireParent(source);
        await TenantReportReadRules.RequireRawAsync(state, source, iam, ct).ConfigureAwait(false);
        var prior = await PriorAsync(source, ct).ConfigureAwait(false);
        if (prior is null)
        {
            if (await TenantReportReadRules.NeedsReviewAsync(state, source, iam, ct).ConfigureAwait(false)) throw new AccessDeniedException("completion_read_review_required");
            return;
        }
        var receipt = await effects.FindOperationReceiptAsync(prior.TenantId, prior.Operation.OperationId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("completion_read_review_required");
        var decision = await effects.FindApprovalDecisionAsync(prior.TenantId, prior.RequestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("completion_read_review_required");
        TenantReportSourceReadRules.Receipt(prior, receipt, decision, await approvals.FindOperationGrantAsync(prior.TenantId, prior.RequestId, ct).ConfigureAwait(false));
        await RequireAsync(prior, ct).ConfigureAwait(false);
        await TenantReportSourceReadRules.ReviewerAsync(state, prior, decision.HumanPrincipalId, iam, ct).ConfigureAwait(false);
    }

    public async ValueTask RequireDeliveryAsync(TenantBackgroundCompletion source, TenantSecurityState state, AgentRun report, CancellationToken ct)
    {
        var current = await configurations.ResolveAsync(source.Child.Owner.TenantId!.Value, source.Child.AgentInstanceId, ct).ConfigureAwait(false);
        var root = await TenantExecutionLineage.RootAsync(source.Child, runs, ct).ConfigureAwait(false);
        if (!TenantCompletionDestinationRules.Allows(source, root, current.Configuration.Configuration.Definition)) throw new AccessDeniedException("background_unavailable");
        if (!await TenantCompletionDestinationRules.NeedsCommunicationReviewAsync(state, source, iam, ct).ConfigureAwait(false)) return;
        var request = await approvals.FindRootApprovalAsync(state.Tenant.TenantId, source.Child.Admission.ExecutionContext!.RootActivationId,
            source.Child.Owner.ExecutorPrincipalId!.Value, TenantBackgroundReportRules.Lookup(source), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("completion_communication_review_required");
        var receipt = await effects.FindOperationReceiptAsync(request.TenantId, request.Operation.OperationId, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("completion_communication_review_required");
        var decision = await effects.FindApprovalDecisionAsync(request.TenantId, request.RequestId, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("completion_communication_review_required");
        TenantBackgroundReportRules.DeliveryReceipt(source, report, request, receipt, decision,
            await approvals.FindOperationGrantAsync(request.TenantId, request.RequestId, ct).ConfigureAwait(false));
        TenantApprovalRules.Validate(request, source.Child, current.Agent);
        await TenantBackgroundReportRules.ReviewerAsync(state, request, decision.HumanPrincipalId, iam, ct).ConfigureAwait(false);
    }
}
