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
            || !RolePermissions.AllowsTool(current, ToolCatalog.BackgroundStart)
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
        if (TenantOperationPolicyService.Effective(state, PermissionCatalog.Get("agent-runs.create")).IndependentReview && human == request.OriginalInitiatorPrincipalId)
            throw new AccessDeniedException("independent_reviewer_required");
        foreach (var key in request.Kind == ApprovalKind.ExceptionalGrant ? new[] { "approvals.approve", "approvals.grant_exception" } : new[] { "approvals.approve" })
            if (!(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get(key), AccessScope.ForTenant(request.TenantId), ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("approver_unqualified");
    }
    public static async ValueTask ExecutorAsync(TenantSecurityState state, TenantApprovalRequest request, AgentRun root, AgentDefinition current, IamAuthorizer iam, CancellationToken ct)
    {
        if (state.Tenant.Status != IdentityStatus.Active || !RolePermissions.AllowsTool(root.Admission.Configuration!.Definition, ToolCatalog.BackgroundStart)
            || !RolePermissions.AllowsTool(current, ToolCatalog.BackgroundStart)) throw new AccessDeniedException("background_unavailable");
        if (request.Kind == ApprovalKind.Review && !(await iam.CheckOwnedTenantAsync(state, request.ExecutorPrincipalId,
            PermissionCatalog.Get("agent-runs.create"), AccessScope.ForTenant(request.TenantId), ct).ConfigureAwait(false)).Allowed)
            throw new AccessDeniedException("operation_authority_changed");
    }
    public static AgentRun Build(TenantApprovalRequest request, TenantBackgroundCompletion source, ResolvedAgentRunConfiguration resolved)
    {
        var p = request.Operation.BackgroundReport!;
        if (TenantBackgroundCreationRules.Hash(resolved) != p.ConfigurationHash) throw new AccessDeniedException("operation_witness_changed");
        return TenantBackgroundCompletionFactory.Create(source, resolved, new(p.Model.CatalogKey, p.Model.ProviderAlias, p.Model.ModelId, ModelSelectionSource.Host, p.Model.ReasoningEffort),
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
