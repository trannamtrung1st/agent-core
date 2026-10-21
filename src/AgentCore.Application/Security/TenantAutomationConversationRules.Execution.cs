using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Security;

public static partial class TenantAutomationConversationRules
{
    public static bool IsConversationRoot(AgentRun root) => root.Admission.OutputContract == AgentRunOutputContract.ConversationResponse
        && root.Admission.TaskIntent?.Source == AgentRunTaskSource.Automation;

    public static long? InputBoundary(AgentRun run)
    {
        if (!IsConversationRoot(run)) return null;
        var boundary = run.Admission.Activation.EvidenceJson is { } evidence
            ? JsonSerializer.Deserialize<AgentRunAdmissionFactory.SignalInput>(evidence)?.InputBoundarySequence : null;
        if (boundary is null || boundary < 0) throw new AccessDeniedException("automation_conversation_input_changed");
        return boundary;
    }

    // This is a prompt projection only; the mailbox keeps the actual current snapshot.
    public static SessionSnapshot Projection(SessionSnapshot snapshot, AgentRun run)
    {
        if (!IsConversationRoot(run)) return snapshot;
        var boundary = InputBoundary(run);
        if (boundary is null || boundary > snapshot.DurableLastEntrySequence || snapshot.SummarizedThroughEntrySequence > boundary)
            throw new AccessDeniedException("automation_conversation_input_changed");
        var entries = snapshot.Entries.Where(e => e.Sequence <= boundary).ToArray();
        return snapshot with { Entries = entries, LastEntrySequence = boundary.Value,
            PendingAgentInputIds = snapshot.PendingAgentInputIds.Intersect(entries.Select(e => e.EntryId)).ToArray() };
    }

    public static async ValueTask RequireExecutionAsync(AgentRun root, TriggerOccurrence occurrence, TenantSession? target,
        TenantSecurityState state, IamAuthorizer iam,
        Func<Guid, CancellationToken, ValueTask<TenantApprovalRequest?>> requestReader,
        Func<Guid, CancellationToken, ValueTask<TenantApprovalDecision?>> decisionReader,
        Func<Guid, CancellationToken, ValueTask<TenantOperationGrant?>> grantReader, CancellationToken ct)
    {
        if (!IsConversationRoot(root)) return;
        if (!TenantCompletionDestinationRules.Eligible(target, state.Tenant.TenantId, root.AgentInstanceId, root.Owner.ExecutorPrincipalId!.Value)
            || target!.Owner.SessionId != root.SessionId || occurrence.ExecutionTarget != AutomationExecutionTarget.Existing(root.SessionId)
            || occurrence.CompletionDelivery != AutomationCompletionDelivery.None || occurrence.AcceptedAgentRunId != root.AgentRunId
            || occurrence.ExecutionSessionId != root.SessionId || occurrence.Disposition != OccurrenceRoutingDisposition.AcceptedDurable
            || occurrence.OccurrenceId != root.Admission.Activation.TriggerOccurrenceId || occurrence.Owner.TenantId != state.Tenant.TenantId
            || occurrence.Owner.AgentInstanceId != root.AgentInstanceId || occurrence.Owner.ExecutorPrincipalId != root.Owner.ExecutorPrincipalId
            || root.Admission.Activation.SourceSessionId is not null || root.Admission.Activation.SourceAgentRunId is not null)
            throw new AccessDeniedException("automation_conversation_unavailable");
        var requester = root.Admission.ExecutionContext!.OriginalInitiatorPrincipalId;
        var review = false;
        foreach (var (key, scope) in TenantCompletionDestinationRules.Communication(state.Tenant.TenantId, root.AgentInstanceId, root.SessionId))
        {
            if (!(await iam.CheckOwnedTenantAsync(state, root.Owner.ExecutorPrincipalId.Value, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed
                || requester is { } human && !(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("automation_conversation_permission_required");
            review |= await TenantSandboxReadOperationRules.NeedsReviewAsync(state, requester, key, scope, iam, ct).ConfigureAwait(false);
        }
        if (!review) return;
        using var evidence = JsonDocument.Parse(occurrence.EvidenceJson);
        if (!evidence.RootElement.TryGetProperty("admissionApproval", out var approval)
            || !approval.TryGetProperty("requestId", out var value) || !value.TryGetGuid(out var id))
            throw new AccessDeniedException("automation_conversation_review_required");
        var request = await requestReader(id, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("automation_conversation_review_required");
        var decision = await decisionReader(id, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("automation_conversation_review_required");
        var prepared = request.Operation.AutomationAdmission;
        var boundary = root.Admission.Activation.EvidenceJson is { } signal ? JsonSerializer.Deserialize<AgentRunAdmissionFactory.SignalInput>(signal)?.InputBoundarySequence : null;
        if (request.Status != TenantApprovalStatus.Approved || request.TenantId != state.Tenant.TenantId || request.RunId != root.AgentRunId
            || request.SessionId != root.SessionId || request.RootActivationId != root.ActivationId || request.ExecutorPrincipalId != root.Owner.ExecutorPrincipalId
            || request.OriginalInitiatorPrincipalId != requester || prepared?.Conversation is null || prepared.SessionId != root.SessionId
            || prepared.Current.ExecutionTarget != occurrence.ExecutionTarget || prepared.Occurrence.OccurrenceId != occurrence.OccurrenceId
            || prepared.Conversation.InputBoundarySequence != boundary || request.Operation != TenantApprovalRules.Rebuild(request.Operation)
            || decision.TenantId != request.TenantId || decision.RequestId != request.RequestId || decision.Status != request.Status
            || decision.RequestRevision != request.Revision || decision.ActionHash != request.Operation.ActionHash
            || decision.DecidedAt < request.CreatedAt || decision.DecidedAt >= request.ExpiresAt)
            throw new AccessDeniedException("automation_conversation_review_required");
        if (request.Kind == ApprovalKind.ExceptionalGrant)
        {
            var grant = await grantReader(id, ct).ConfigureAwait(false);
            if (grant is not { Status: OperationGrantStatus.Consumed } || grant.TenantId != request.TenantId || grant.RequestId != request.RequestId
                || grant.OperationId != request.Operation.OperationId || grant.ExecutorPrincipalId != request.ExecutorPrincipalId || grant.ActionHash != request.Operation.ActionHash)
                throw new AccessDeniedException("automation_conversation_review_required");
        }
        await TenantAutomationAdmissionApprovalRules.ReviewerAsync(state, request, decision.HumanPrincipalId, iam, ct).ConfigureAwait(false);
    }
}
