using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantBackgroundReportRules
{
    public static PreparedTenantOperation Lookup(TenantBackgroundCompletion source) => new(source.Child.AgentRunId,
        source.Child.AgentInstanceId, null, 1, 1, "", "", BackgroundReport: new(source.Child.Owner.TenantId!.Value,
            source.Child.SessionId, source.Child.AgentRunId, source.Child.Revision, source.Parent!.Owner.SessionId,
            1, 0, Guid.Empty, Guid.Empty, Guid.Empty, "", "", new("lookup", "lookup", "lookup", null)));

    // Consumed creation authority proves only this report. Current secondary access and
    // reviewer qualification still apply when its private result becomes visible.
    public static void DeliveryReceipt(TenantBackgroundCompletion source, AgentRun report, TenantApprovalRequest request,
        TenantOperationReceipt receipt, TenantApprovalDecision decision, TenantOperationGrant? grant)
    {
        var p = request.Operation.BackgroundReport;
        if (p is null || request.Status != TenantApprovalStatus.Approved || request.TenantId != report.Owner.TenantId
            || request.ExecutorPrincipalId != report.Owner.ExecutorPrincipalId || request.Operation.InstanceId != report.AgentInstanceId
            || request.RootActivationId != report.Admission.ExecutionContext!.RootActivationId
            || request.OriginalInitiatorPrincipalId != report.Admission.ExecutionContext.OriginalInitiatorPrincipalId
            || p.ChildRunId != source.Child.AgentRunId || p.SourceRevision != source.Child.Revision || p.SessionId != source.Child.SessionId
            || p.ParentSessionId != report.SessionId || p.RunId != report.AgentRunId || p.ActivationId != report.ActivationId
            || p.ResponseId != report.ResponseId || p.EvidenceJson != report.Admission.Activation.EvidenceJson
            || receipt.RequestId != request.RequestId || receipt.OperationId != request.Operation.OperationId
            || receipt.TenantId != request.TenantId || receipt.RunId != request.RunId || receipt.SessionId != request.SessionId
            || receipt.RootActivationId != request.RootActivationId || receipt.InstanceId != request.Operation.InstanceId
            || receipt.ExecutorPrincipalId != request.ExecutorPrincipalId || receipt.ActionHash != request.Operation.ActionHash
            || receipt.Outcome != TenantOperationOutcome.Completed || receipt.ReasonCode != "completion_report_created"
            || decision.RequestId != request.RequestId || decision.TenantId != request.TenantId || decision.Status != request.Status
            || decision.ActionHash != request.Operation.ActionHash || decision.RequestRevision != request.Revision
            || request.Kind == ApprovalKind.Review && grant is not null
            || request.Kind == ApprovalKind.ExceptionalGrant && (grant is null || grant.Status != OperationGrantStatus.Consumed
                || grant.RequestId != request.RequestId || grant.OperationId != request.Operation.OperationId
                || grant.TenantId != request.TenantId || grant.ExecutorPrincipalId != request.ExecutorPrincipalId || grant.ActionHash != request.Operation.ActionHash))
            throw new AccessDeniedException("completion_communication_review_required");
    }
    public static void Source(TenantApprovalRequest request, AgentRun child)
    {
        var p = request.Operation.BackgroundReport ?? throw new SecurityConflictException("operation_mismatch");
        if (!child.IsTerminal || child.Result?.OutcomeKind == AgentRunOutcomeKind.Continued || child.AgentRunId != request.RunId
            || child.AgentRunId != p.ChildRunId || child.SessionId != p.SessionId || p.TenantId != request.TenantId || child.Revision != p.SourceRevision
            || child.Admission.OutputContract != AgentRunOutputContract.BackgroundOutcome)
            throw new AccessDeniedException("completion_unavailable");
    }
    public static void Require(TenantApprovalRequest request, TenantBackgroundCompletion source, TenantAgent agent, AgentDefinition current)
    {
        Source(request, source.Child); TenantBackgroundCompletionFactory.RequireParent(source);
        var p = request.Operation.BackgroundReport!; var parent = source.Parent!;
        if (source.Item?.Status != CompletionInboxStatus.Pending || parent.Owner.SessionId != p.ParentSessionId
            || parent.Snapshot.Revision != p.ParentRevision || parent.Snapshot.DurableLastEntrySequence != p.InputBoundarySequence
            || agent.Instance.Revision != request.Operation.ExpectedInstanceRevision || agent.Instance.PersonaRevision != request.Operation.ExpectedPersonaRevision
            || agent.PrincipalId != request.ExecutorPrincipalId || agent.Instance.InstanceId != request.Operation.InstanceId
            || !TenantCompletionDestinationRules.Configured(source) && !RolePermissions.AllowsTool(current, ToolCatalog.BackgroundStart)
            || p.EvidenceJson != BackgroundCompletionProjection.Build(source.Child, source.Child.Admission.TaskIntent?.Text ?? source.ChildSession.Snapshot.Title, [], p.InputBoundarySequence))
            throw new AccessDeniedException("operation_witness_changed");
    }
    public static async ValueTask ReadAsync(TenantSecurityState state, TenantApprovalRequest request, Guid human, IamAuthorizer iam, CancellationToken ct)
    {
        var p = request.Operation.BackgroundReport!;
        foreach (var (key, scope) in new[] { ("agent-runs.create", AccessScope.ForTenant(request.TenantId)),
            ("sessions.read", TenantSessionRules.Scope(request.TenantId, p.SessionId)),
            ("sessions.read", TenantSessionRules.Scope(request.TenantId, p.ParentSessionId)),
            ("agent-runs.read", TenantRunRules.Scope(request.TenantId, p.ChildRunId)) })
            if (!(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("approval_unavailable");
    }
    public static async ValueTask ReviewerAsync(TenantSecurityState state, TenantApprovalRequest request, Guid human, IamAuthorizer iam, CancellationToken ct)
    {
        await ReadAsync(state, request, human, iam, ct).ConfigureAwait(false);
        // Includes existing immediate-child reports: a decision is a communication boundary.
        if (!(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get("agents.chat"),
            AccessScope.ForResource(request.TenantId, "agent", request.Operation.InstanceId.ToString("D")), ct)).Allowed)
            throw new AccessDeniedException("approver_unqualified");
        if (new[] { "agent-runs.create", "agents.chat", "sessions.read" }.Any(key =>
            TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(key)).IndependentReview) && human == request.OriginalInitiatorPrincipalId)
            throw new AccessDeniedException("independent_reviewer_required");
        foreach (var key in request.Kind == ApprovalKind.ExceptionalGrant ? new[] { "approvals.approve", "approvals.grant_exception" } : new[] { "approvals.approve" })
            if (!(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get(key), AccessScope.ForTenant(request.TenantId), ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("approver_unqualified");
    }
    public static async ValueTask ExecutorAsync(TenantSecurityState state, TenantApprovalRequest request, AgentRun root, AgentDefinition current, IamAuthorizer iam, CancellationToken ct, TenantBackgroundCompletion source)
    {
        if (state.Tenant.Status != IdentityStatus.Active || !TenantCompletionDestinationRules.Allows(source, root, current)) throw new AccessDeniedException("background_unavailable");
        await TenantCompletionDestinationRules.CommunicationAsync(state, source, iam, ct).ConfigureAwait(false);
        if (request.Kind == ApprovalKind.Review && !(await iam.CheckOwnedTenantAsync(state, request.ExecutorPrincipalId,
            PermissionCatalog.Get("agent-runs.create"), AccessScope.ForTenant(request.TenantId), ct).ConfigureAwait(false)).Allowed)
            throw new AccessDeniedException("operation_authority_changed");
    }
    public static AgentRun Build(TenantApprovalRequest request, TenantBackgroundCompletion source, ResolvedAgentRunConfiguration resolved)
    {
        var p = request.Operation.BackgroundReport!;
        if (TenantBackgroundCreationRules.Hash(resolved) != p.ConfigurationHash) throw new AccessDeniedException("operation_witness_changed");
        return TenantBackgroundCompletionFactory.Create(source, resolved, new(p.Model.OfferingId, p.Model.ConnectionId, p.Model.ExternalModelId, ModelSelectionSource.Host, p.Model.ReasoningEffort, Binding: p.Model.Binding),
            p.ActivationId, p.RunId, p.ResponseId, request.CreatedAt);
    }
    public static void Report(TenantApprovalRequest request, TenantBackgroundCompletion source, AgentRun report, TenantAgent agent, DateTimeOffset now)
    {
        var p = request.Operation.BackgroundReport!;
        TenantBackgroundCompletionRules.Shape(source, report, agent, p.ParentRevision, now);
        var resolved = new ResolvedAgentRunConfiguration(report.Admission.Configuration!, report.PinnedPersona, report.PinnedSkillCatalog, agent.Instance.ExecutionBudgets);
        if (report.AgentRunId != p.RunId || report.ActivationId != p.ActivationId || report.ResponseId != p.ResponseId
            || report.CreatedAtUtc != request.CreatedAt || report.Admission.Activation.EvidenceJson != p.EvidenceJson
            || JsonSerializer.Serialize(report.PinnedModel) != JsonSerializer.Serialize(p.Model) || TenantBackgroundCreationRules.Hash(resolved) != p.ConfigurationHash)
            throw new SecurityConflictException("operation_mismatch");
    }
    public static void Decision(TenantApprovalRequest request, TenantApprovalDecision decision, TenantOperationGrant? grant, DateTimeOffset now)
    {
        if (decision.RequestId != request.RequestId || decision.TenantId != request.TenantId || decision.Status != request.Status
            || decision.RequestRevision != request.Revision || decision.ActionHash != request.Operation.ActionHash)
            throw new SecurityConflictException("operation_mismatch");
        if (request.Status != TenantApprovalStatus.Approved) return;
        if (request.ExpiresAt <= now) throw new AccessDeniedException("approval_expired");
        if (request.Kind == ApprovalKind.ExceptionalGrant && (grant is null || grant.Status != OperationGrantStatus.Available
            || grant.RequestId != request.RequestId || grant.TenantId != request.TenantId || grant.ExecutorPrincipalId != request.ExecutorPrincipalId
            || grant.OperationId != request.Operation.OperationId || grant.ActionHash != request.Operation.ActionHash || grant.ExpiresAt <= now
            || grant.EffectIdempotencyKey != "operation:" + request.Operation.OperationId.ToString("D")))
            throw new AccessDeniedException("grant_unavailable");
        if (request.Kind == ApprovalKind.Review && grant is not null) throw new SecurityConflictException("operation_mismatch");
    }
}
