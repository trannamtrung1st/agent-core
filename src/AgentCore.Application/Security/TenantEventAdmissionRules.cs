using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Events;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Security;

// Deterministic reconstruction only. The source store must independently verify
// the actual owned receipt, matched delivery/bucket, current source and authority
// in the same transaction that reserves or admits the occurrence.
public static class TenantEventAdmissionRules
{
    public static AccessScope Scope(Guid tenant, Guid eventId) => AccessScope.ForResource(tenant, "event", eventId.ToString("D"));
    public static bool TryId(AccessScope scope, out Guid id)
    {
        id = Guid.Empty; return scope.Kind == AccessScopeKind.Resource && scope.TenantId is { } tenant && tenant != Guid.Empty && scope.ResourceType == "event"
            && Guid.TryParseExact(scope.ResourceId, "D", out id) && id != Guid.Empty && scope.ResourceId == id.ToString("D");
    }
    public static async ValueTask RawReadAsync(TenantSecurityState state, Guid executor, EventSubscriptionSnapshot source,
        IReadOnlyList<Guid> eventIds, IamAuthorizer iam, CancellationToken ct)
    {
        if (state.Tenant.TenantId != source.Owner.TenantId || source.Owner.ExecutorPrincipalId != executor || state.Tenant.Status != IdentityStatus.Active
            || !state.SystemIdentities.Any(i => i.PrincipalId == executor && i.AgentInstanceId == source.Owner.AgentInstanceId && i.Kind == SystemIdentityKind.Agent && i.Status == IdentityStatus.Active))
            throw new AccessDeniedException("event_executor_unavailable");
        var key = source.SourceKind == TriggerSourceKind.ApplicationEvent ? "webhooks.read" : "core-events.read";
        var scopes = source.SourceKind == TriggerSourceKind.ApplicationEvent && source.ResourceId is { } resource
            ? new[] { TenantWebhookRules.Scope(state.Tenant.TenantId, resource) }
            : source.SourceKind == TriggerSourceKind.CoreEvent && source.ResourceId is null && eventIds.Count > 0
                ? eventIds.Select(id => Scope(state.Tenant.TenantId, id)).ToArray() : throw new AccessDeniedException("event_source_unavailable");
        foreach (var scope in scopes)
            if (!(await iam.CheckOwnedTenantAsync(state, executor, PermissionCatalog.Get(key), scope, ct)).Allowed)
                throw new AccessDeniedException("event_source_read_required");
    }

    public static PreparedEventAdmissionSource Freeze(TenantEventDelivery delivery, long? resourceRevision = null)
    {
        var s = delivery.Subscription;
        if (delivery.TenantId != s.Owner.TenantId || s.TriggerId == Guid.Empty || s.TriggerRevision < 1
            || delivery.Status != EventMatchStatus.Matched || delivery.Decision?.Matched != true
            || (delivery.Core is null) == (delivery.Webhook is null)
            || s.SourceKind == TriggerSourceKind.CoreEvent && (delivery.Core?.Event.Owner != s.Owner || s.ResourceId is not null)
            || s.SourceKind == TriggerSourceKind.ApplicationEvent && (delivery.Webhook?.TenantId != delivery.TenantId || delivery.Webhook?.ResourceId != s.ResourceId))
            throw new ArgumentException("An exact owned matched event delivery is required.");
        var hash = TenantWorkspaceMutationContract.Hash(JsonSerializer.Serialize(new { delivery.TenantId, delivery.Subscription,
            core = delivery.Core is null ? null : new { delivery.Core.Event, delivery.Core.ReceivedAtUtc }, delivery.Webhook, delivery.Decision, resourceRevision }));
        return new(delivery.TenantId, s.TriggerId, s.SourceKind, delivery.EventId, false, hash, [delivery.EventId], resourceRevision, s.ResourceId);
    }
    public static PreparedEventAdmissionSource Freeze(TenantEventBucket source, long? resourceRevision = null)
    {
        var bucket = source.Bucket; var s = bucket.Subscription;
        if (source.TenantId != s.Owner.TenantId || bucket.Flushed || s.Dispatch.Mode != EventDispatchMode.CoalesceLatest
            || bucket.Sources.Any(e => e.Owner != s.Owner) || bucket.Sources.Count is < 1 or > TriggerLimits.MaxEventBucketSources)
            throw new ArgumentException("An exact owned unconsumed event bucket is required.");
        var hash = TenantWorkspaceMutationContract.Hash(JsonSerializer.Serialize(new { source.TenantId, bucket.BucketId, bucket.Subscription, bucket.DueAtUtc, bucket.Sources, resourceRevision }));
        return new(source.TenantId, s.TriggerId, s.SourceKind, bucket.BucketId, true, hash, bucket.Sources.Select(e => e.EventId).ToArray(), resourceRevision, s.ResourceId);
    }
    public static EventBucketSource BucketSource(TenantEventDelivery value, string key)
    {
        var core = value.Core?.Event;
        return new(value.EventId, value.Subscription.Owner, key, value.ReceivedAt,
            core?.DataJson ?? JsonSerializer.Deserialize<JsonElement>(value.Webhook!.EvidenceJson).GetProperty("data").GetRawText(),
            core?.RootAgentRunId, core?.TriggerDepth ?? 0, Array.AsReadOnly((core?.VisitedAutomationIds ?? []).ToArray()));
    }
    public static TriggerOccurrence Initial(TriggerOccurrence source) => new(source.OccurrenceId, source.DedupeKey, source.AutomationId, source.Owner,
        source.SourceKind, source.ScheduledAtUtc, source.ObservedAtUtc, source.AdmittedAtUtc, source.EvidenceJson, source.SourceEventId, source.TriggerRevision,
        OccurrenceRoutingDisposition.Pending, null, 0, null, null, null, modelPin: source.ModelPin, executionTarget: source.ExecutionTarget,
        completionDelivery: source.CompletionDelivery, triggerId: source.TriggerId);
    public static Automation Next(Automation current, DateTimeOffset at) => current.WithScheduleAdvance(current.Status, null,
        checked(current.OccurrenceCount + 1), checked(current.Revision + 1), at, null);
    public static TriggerOccurrence Occurrence(Automation current, TenantEventDelivery delivery, ExecutionModelPin model, DateTimeOffset at,
        long? resourceRevision = null)
    {
        var witness = Freeze(delivery, resourceRevision); var child = Child(current, delivery.Subscription);
        var core = delivery.Core?.Event;
        RequireCausation(core?.TriggerDepth ?? 0, core?.VisitedAutomationIds ?? [], current.AutomationId);
        var data = core is null ? JsonSerializer.Deserialize<JsonElement>(delivery.Webhook!.EvidenceJson).GetProperty("data") : JsonSerializer.Deserialize<JsonElement>(core.DataJson);
        var evidence = AutomationRules.Evidence(current, new { eventId = delivery.EventId, source = data, admissionSource = witness,
            causation = new { rootAgentRunId = core?.RootAgentRunId, triggerDepth = (core?.TriggerDepth ?? 0) + 1,
                visitedAutomationIds = (core?.VisitedAutomationIds ?? []).Append(current.AutomationId).ToArray() } }, child);
        return Create(current, witness, child, delivery.OccurredAt, at, evidence, model);
    }
    public static TriggerOccurrence Occurrence(Automation current, TenantEventBucket source, ExecutionModelPin model, DateTimeOffset at,
        long? resourceRevision = null)
    {
        var witness = Freeze(source, resourceRevision); var bucket = source.Bucket; var child = Child(current, bucket.Subscription);
        if (bucket.DueAtUtc > at) throw new ArgumentException("An event bucket is not due.");
        foreach (var entry in bucket.Sources) RequireCausation(entry.TriggerDepth, entry.VisitedAutomationIds ?? [], current.AutomationId);
        var evidence = AutomationRules.Evidence(current, new { bucketId = bucket.BucketId, count = bucket.Sources.Count, admissionSource = witness,
            sources = bucket.Sources.Select(e => new { eventId = e.EventId, data = JsonSerializer.Deserialize<JsonElement>(e.DataJson) }),
            causation = new { rootAgentRunId = bucket.Sources[0].RootAgentRunId, triggerDepth = bucket.Sources.Max(e => e.TriggerDepth) + 1,
                visitedAutomationIds = bucket.Sources.SelectMany(e => e.VisitedAutomationIds ?? []).Append(current.AutomationId).Distinct().ToArray() } }, child);
        return Create(current, witness, child, bucket.DueAtUtc, at, evidence, model);
    }
    public static AutomationTriggerRecord Child(Automation current, EventSubscriptionSnapshot saved)
    {
        var child = current.Triggers.SingleOrDefault(t => t.TriggerId == saved.TriggerId);
        if (current.Owner != saved.Owner || current.AutomationId != saved.AutomationId || current.Status != AutomationStatus.Active
            || child is not { Enabled: true } || child.Revision < saved.TriggerRevision || child.Configuration is not FilteredEventTrigger
            || saved.ExpressionVersion != "js-expression-v1"
            || AutomationRules.Source(child.Configuration) != saved.SourceKind
            || (child.Configuration is EventTrigger webhook ? saved.ResourceId != webhook.EventId : saved.ResourceId is not null))
            throw new AccessDeniedException("event_subscription_changed");
        // Receipt-time filters/dispatch stay frozen. Current source identity,
        // enablement, IAM and policy remain live; later edits affect new receipts.
        AutomationTrigger captured = child.Configuration switch {
            EventTrigger source => new EventTrigger(source.EventId, saved.FilterExpression, saved.Dispatch),
            CoreEventTrigger core => new CoreEventTrigger(core.CoreEventKey, saved.FilterExpression, saved.Dispatch),
            _ => throw new AccessDeniedException("event_subscription_changed") };
        return new(child.TriggerId, captured, true, saved.TriggerRevision);
    }
    public static void Shape(Automation automation, TriggerOccurrence occurrence, PreparedEventAdmissionSource source)
    {
        var child = automation.Triggers.SingleOrDefault(t => t.TriggerId == source.TriggerId);
        if (source.TenantId != automation.Owner.TenantId || occurrence.Owner != automation.Owner || occurrence.AutomationId != automation.AutomationId
            || child is not { Enabled: true } || child.Revision < occurrence.TriggerRevision || occurrence.TriggerId != source.TriggerId
            || (child.Configuration is EventTrigger webhook ? source.ResourceId != webhook.EventId : source.ResourceId is not null)
            || source.SourceKind != AutomationRules.Source(child.Configuration) || occurrence.SourceKind != source.SourceKind
            || occurrence.DedupeKey != source.DedupeKey || occurrence.OccurrenceId != TriggerScheduleAdmission.OccurrenceId(source.DedupeKey)
            || occurrence.SourceEventId != source.SourceId || occurrence.ScheduledAtUtc is not null)
            throw new ArgumentException("Event admission differs from its exact source and subscription.");
        using var json = JsonDocument.Parse(occurrence.EvidenceJson);
        if (!json.RootElement.TryGetProperty("triggerContext", out var context) || !context.TryGetProperty("admissionSource", out var pinned)
            || JsonSerializer.Serialize(source) != JsonSerializer.Serialize(pinned.Deserialize<PreparedEventAdmissionSource>())) throw new ArgumentException("Event admission source evidence differs.");
    }
    private static TriggerOccurrence Create(Automation current, PreparedEventAdmissionSource source, AutomationTriggerRecord child,
        DateTimeOffset occurredAt, DateTimeOffset at, string evidence, ExecutionModelPin model)
        => new(TriggerScheduleAdmission.OccurrenceId(source.DedupeKey), source.DedupeKey, current.AutomationId, current.Owner, source.SourceKind,
            null, occurredAt, at, evidence, source.SourceId, child.Revision, OccurrenceRoutingDisposition.Pending, null, 0, null, null, null,
            modelPin: model, executionTarget: current.ExecutionTarget, completionDelivery: current.CompletionDelivery, triggerId: child.TriggerId);
    private static void RequireCausation(int depth, IReadOnlyList<Guid> visited, Guid automation)
    { if (depth is < 0 or >= 4 || visited.Contains(automation)) throw new AccessDeniedException("event_causal_loop"); }
}
