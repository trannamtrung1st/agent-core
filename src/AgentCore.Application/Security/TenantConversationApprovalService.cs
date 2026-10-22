using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantConversationApproval(Guid RequestId, Guid RunId, Guid? ResponseId, TenantOperationKind Operation,
    ApprovalKind Kind, TenantApprovalStatus Status, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    bool Waiting, bool CanInspect, TenantOperationOutcome? Outcome, bool? MemoryRead = null, string? ExperienceTool = null, string? AutomationTool = null);
public sealed record TenantConversationApprovalPage(IReadOnlyList<TenantConversationApproval> Items, int Total, int Page, int PageSize);

// Conversation authority permits this minimal status, never the protected action preview or a decision.
public sealed class TenantConversationApprovalService(TenantConversationService conversations, TenantApprovalService review,
    ITenantApprovalStore approvals, ITenantRunStore runs, ITenantApprovedOperationStore effects)
{
    public async ValueTask<TenantConversationApprovalPage> ListAsync(Guid tenant, Guid session, TenantSessionInitiator human,
        int page = 0, int pageSize = 20, CancellationToken ct = default)
    {
        var query = new TenantApprovalQuery(page, pageSize, SessionId: session); query.Validate();
        var before = await conversations.AuthorizeRecipientAsync(tenant, session, human, ct).ConfigureAwait(false);
        var saved = await approvals.ListApprovalsAsync(tenant, null, null, query, ct, automationVisibility: await review.AutomationReadVisibilityAsync(tenant, human, ct).ConfigureAwait(false), memoryVisibility: await review.MemoryVisibilityAsync(tenant, human, ct).ConfigureAwait(false), continuityVisibility: await review.ContinuityVisibilityAsync(tenant, human, ct).ConfigureAwait(false)).ConfigureAwait(false);
        var items = new List<TenantConversationApproval>(saved.Items.Count);
        foreach (var request in saved.Items)
        {
            if (request.TenantId != tenant || (request.Operation.ReportSourceRead is { } sourceRead ? sourceRead.ParentSessionId != session : request.Operation.BackgroundReport is { } report ? report.ParentSessionId != session : request.SessionId != session)) throw new AccessDeniedException("approval_unavailable");
            var run = await runs.FindTenantRunAsync(tenant, request.RunId, ct).ConfigureAwait(false);
            if (run is null || run.SessionId != request.SessionId || run.AgentInstanceId != before.Owner.AgentInstanceId) throw new AccessDeniedException("approval_unavailable");
            var result = await effects.FindOperationReceiptAsync(tenant, request.Operation.OperationId, ct).ConfigureAwait(false);
            var inspect = false;
            try { await review.ReadViewAsync(tenant, request.RequestId, human, ct).ConfigureAwait(false); inspect = true; }
            catch (AccessDeniedException) { }
            catch (SecurityConflictException) { } // A stale private preview cannot hide the factual decision/receipt.
            items.Add(new(request.RequestId, request.RunId, run.ResponseId, request.Operation.Kind, request.Kind, request.Status,
                request.CreatedAt, request.ExpiresAt, request.Status == TenantApprovalStatus.Pending && (request.Operation.BackgroundReport is not null || request.Operation.ReportSourceRead is not null ? run.IsTerminal : run.Status == AgentRunStatus.WaitingForApproval),
                inspect, result?.Outcome, request.Operation.MemoryOperation is { } memory ? AgentCore.Application.Tools.TenantMemoryToolContract.ReadOnly(memory.ToolName) : null,
                request.Operation.ExperienceOperation?.ToolName, request.Operation.AutomationMutation?.ToolName));
        }
        var after = await conversations.AuthorizeRecipientAsync(tenant, session, human, ct).ConfigureAwait(false);
        if (after.SecurityRevision != before.SecurityRevision || after.Owner != before.Owner) throw new SecurityConflictException("stale_revision");
        return new(items, saved.Total, saved.Page, saved.PageSize);
    }
}
