using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Events;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Security;

// Admission consumed the source once. Runtime keeps independent raw read and
// live policy authority; later token rotation or subscription edits do not cancel it.
public static class TenantEventReadRules
{
    public static bool IsEventRoot(AgentRun root) => root.Admission.ExecutionContext?.Origin is ExecutionOrigin.CoreEvent or ExecutionOrigin.ApplicationEvent;
    public static async ValueTask RequireAsync(AgentRun root, TriggerOccurrence occurrence, TenantSecurityState state, IamAuthorizer iam,
        Func<TriggerSourceKind, Guid, Guid, CancellationToken, ValueTask<TenantEventDelivery?>> deliveryReader,
        Func<Guid, CancellationToken, ValueTask<TenantEventBucket?>> bucketReader,
        Func<Guid, CancellationToken, ValueTask<TenantApprovalRequest?>> requestReader,
        Func<Guid, CancellationToken, ValueTask<TenantApprovalDecision?>> decisionReader,
        Func<Guid, CancellationToken, ValueTask<TenantOperationGrant?>> grantReader, CancellationToken ct)
    {
        if (!IsEventRoot(root)) return;
        if (occurrence.Owner != TriggerOwner.ForTenant(state.Tenant.TenantId, root.AgentInstanceId, root.Owner.ExecutorPrincipalId!.Value)
            || occurrence.Disposition != OccurrenceRoutingDisposition.AcceptedDurable || occurrence.AcceptedAgentRunId != root.AgentRunId
            || occurrence.ExecutionSessionId != root.SessionId || occurrence.OccurrenceId != root.Admission.Activation.TriggerOccurrenceId)
            throw new AccessDeniedException("event_source_unavailable");
        using var evidence = JsonDocument.Parse(occurrence.EvidenceJson);
        if (!evidence.RootElement.TryGetProperty("triggerContext", out var context) || !context.TryGetProperty("admissionSource", out var sourceJson))
            throw new AccessDeniedException("event_source_unavailable");
        var source = sourceJson.Deserialize<PreparedEventAdmissionSource>() ?? throw new AccessDeniedException("event_source_unavailable");
        if (source.TenantId != state.Tenant.TenantId || source.TriggerId != occurrence.TriggerId || source.SourceKind != occurrence.SourceKind
            || root.Admission.TaskIntent?.EventSourceKind != source.SourceKind || source.SourceId != occurrence.SourceEventId || source.DedupeKey != occurrence.DedupeKey)
            throw new AccessDeniedException("event_source_unavailable");
        PreparedEventAdmissionSource frozen;
        if (!source.IsBucket)
        {
            var delivery = await deliveryReader(source.SourceKind, source.SourceId, source.TriggerId, ct).ConfigureAwait(false)
                ?? throw new AccessDeniedException("event_source_unavailable");
            if (delivery.Status != EventMatchStatus.Admitted || delivery.ReservedOccurrenceId != occurrence.OccurrenceId
                || delivery.Subscription.Owner != occurrence.Owner || delivery.Subscription.AutomationId != occurrence.AutomationId)
                throw new AccessDeniedException("event_source_unavailable");
            frozen = TenantEventAdmissionRules.Freeze(delivery with { Status = EventMatchStatus.Matched, ReservedOccurrenceId = null }, source.ResourceRevision);
        }
        else
        {
            var value = await bucketReader(source.SourceId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("event_source_unavailable");
            var bucket = value.Bucket;
            if (!bucket.Flushed || bucket.CompletionCode is not null || bucket.ReservedOccurrenceId != occurrence.OccurrenceId
                || bucket.Subscription.Owner != occurrence.Owner || bucket.Subscription.AutomationId != occurrence.AutomationId)
                throw new AccessDeniedException("event_source_unavailable");
            foreach (var entry in bucket.Sources)
            {
                var delivery = await deliveryReader(source.SourceKind, entry.EventId, source.TriggerId, ct).ConfigureAwait(false)
                    ?? throw new AccessDeniedException("event_source_unavailable");
                if (delivery.Status != EventMatchStatus.Coalesced || delivery.Subscription != bucket.Subscription
                    || JsonSerializer.Serialize(entry) != JsonSerializer.Serialize(TenantEventAdmissionRules.BucketSource(delivery, entry.Key)))
                    throw new AccessDeniedException("event_source_unavailable");
            }
            frozen = TenantEventAdmissionRules.Freeze(value with { Bucket = bucket with { Flushed = false, ReservedOccurrenceId = null } }, source.ResourceRevision);
        }
        if (JsonSerializer.Serialize(frozen) != JsonSerializer.Serialize(source)) throw new AccessDeniedException("event_source_changed");
        var review = false;
        foreach (var (key, scope) in TenantAutomationAdmissionApprovalRules.SourceTargets(source))
        {
            if (!(await iam.CheckOwnedTenantAsync(state, root.Owner.ExecutorPrincipalId!.Value, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("event_source_read_required");
            review |= await TenantSandboxReadOperationRules.NeedsReviewAsync(state, null, key, scope, iam, ct).ConfigureAwait(false);
        }
        if (!review) return;
        if (!evidence.RootElement.TryGetProperty("admissionApproval", out var approved)
            || !approved.TryGetProperty("requestId", out var requestId) || !requestId.TryGetGuid(out var id))
            throw new AccessDeniedException("event_source_review_required");
        var request = await requestReader(id, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("event_source_review_required");
        var decision = await decisionReader(id, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("event_source_review_required");
        var prepared = request.Operation.AutomationAdmission;
        if (request.Status != TenantApprovalStatus.Approved || request.TenantId != state.Tenant.TenantId
            || request.RunId != root.AgentRunId || request.SessionId != root.SessionId || request.RootActivationId != root.ActivationId
            || request.ExecutorPrincipalId != root.Owner.ExecutorPrincipalId || prepared?.Occurrence.OccurrenceId != occurrence.OccurrenceId
            || JsonSerializer.Serialize(prepared.EventSource) != JsonSerializer.Serialize(source)
            || decision.RequestId != request.RequestId || decision.TenantId != request.TenantId || decision.Status != request.Status
            || decision.RequestRevision != request.Revision || decision.ActionHash != request.Operation.ActionHash
            || decision.DecidedAt < request.CreatedAt || decision.DecidedAt >= request.ExpiresAt)
            throw new AccessDeniedException("event_source_review_required");
        if (request.Kind == ApprovalKind.ExceptionalGrant)
        {
            var grant = await grantReader(id, ct).ConfigureAwait(false);
            if (grant is not { Status: OperationGrantStatus.Consumed } || grant.TenantId != request.TenantId || grant.RequestId != request.RequestId
                || grant.OperationId != request.Operation.OperationId || grant.ExecutorPrincipalId != request.ExecutorPrincipalId || grant.ActionHash != request.Operation.ActionHash)
                throw new AccessDeniedException("event_source_review_required");
        }
        await TenantAutomationAdmissionApprovalRules.ReviewerAsync(state, request, decision.HumanPrincipalId, iam, ct).ConfigureAwait(false);
    }
}

public sealed class TenantEventReadAuthority(IIamStore security, ITenantRunStore runs, ITenantAutomationStore automations,
    ITenantEventStore events, ITenantApprovalStore approvals, ITenantApprovedOperationStore effects, IamAuthorizer iam)
{
    public async ValueTask RequireAsync(AgentRun run, CancellationToken ct)
    {
        var root = await TenantExecutionLineage.RootAsync(run, runs, ct).ConfigureAwait(false);
        if (!TenantEventReadRules.IsEventRoot(root)) return;
        var tenant = run.Owner.TenantId!.Value;
        var state = await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        var occurrence = await automations.FindTenantOccurrenceAsync(tenant, root.Admission.Activation.TriggerOccurrenceId!.Value, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("event_source_unavailable");
        await TenantEventReadRules.RequireAsync(root, occurrence, state, iam,
            (kind, id, trigger, token) => events.FindTenantEventDeliveryAsync(tenant, kind, id, trigger, token),
            (id, token) => events.FindTenantEventBucketAsync(tenant, id, token),
            (id, token) => approvals.FindApprovalAsync(tenant, id, token),
            (id, token) => effects.FindApprovalDecisionAsync(tenant, id, token),
            (id, token) => approvals.FindOperationGrantAsync(tenant, id, token), ct).ConfigureAwait(false);
        if ((await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false))?.Tenant.Revision != state.Tenant.Revision)
            throw new SecurityConflictException("event_source_changed");
    }
}
