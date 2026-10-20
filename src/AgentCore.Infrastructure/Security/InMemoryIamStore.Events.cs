using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Agents;
using AgentCore.Application.Security;
using AgentCore.Domain.Events;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantEventStore
{
    private T WithTenantEvents<T>(Func<T> action)
    {
        lock (_gate) lock (instances.CredentialGate) lock (definitions.TenantGate) lock (automationBacking.AdmissionGate)
        lock (webhookBacking.TenantGate) lock (sessionBacking.CoreEvents.TenantGate) return action();
    }
    private TenantEventDelivery? TenantEvent(Guid tenant, TriggerSourceKind kind, Guid id, Guid trigger)
    {
        if (kind == TriggerSourceKind.CoreEvent && sessionBacking.CoreEvents.TenantDelivery(tenant, id, trigger) is { } core
            && sessionBacking.CoreEvents.TenantReceipt(tenant, id) is { } fact)
            return new(tenant, core.Subscription, core.Status, core.Decision, fact, null, core.Code, core.ReservedOccurrenceId);
        if (kind == TriggerSourceKind.ApplicationEvent && webhookBacking.TenantDelivery(tenant, id, trigger) is { Snapshot: { } snapshot } external
            && webhookBacking.TenantReceiptById(tenant, id) is { } receipt)
        {
            var status = external.Status switch {
                ExternalEventDeliveryStatus.Pending => external.Decision?.Matched == true ? EventMatchStatus.Matched : EventMatchStatus.Pending,
                ExternalEventDeliveryStatus.Filtered => EventMatchStatus.Filtered, ExternalEventDeliveryStatus.FilterError => EventMatchStatus.FilterError,
                ExternalEventDeliveryStatus.Coalesced => EventMatchStatus.Coalesced, ExternalEventDeliveryStatus.Admitted => EventMatchStatus.Admitted,
                ExternalEventDeliveryStatus.AwaitingAdmission => EventMatchStatus.AwaitingAdmission, _ => EventMatchStatus.PolicySkipped };
            return new(tenant, snapshot, status, external.Decision, null, receipt, external.Code, external.ReservedOccurrenceId);
        }
        return null;
    }
    private void PutTenantEvent(TenantEventDelivery value)
    {
        var s = value.Subscription;
        if (s.SourceKind == TriggerSourceKind.CoreEvent)
            sessionBacking.CoreEvents.PutTenantDelivery(new(value.EventId, s, value.Status, value.Decision, value.Code, value.ReservedOccurrenceId));
        else
        {
            var status = value.Status switch {
                EventMatchStatus.Pending or EventMatchStatus.Matched => ExternalEventDeliveryStatus.Pending,
                EventMatchStatus.Filtered => ExternalEventDeliveryStatus.Filtered, EventMatchStatus.FilterError => ExternalEventDeliveryStatus.FilterError,
                EventMatchStatus.Coalesced => ExternalEventDeliveryStatus.Coalesced, EventMatchStatus.Admitted => ExternalEventDeliveryStatus.Admitted,
                EventMatchStatus.AwaitingAdmission => ExternalEventDeliveryStatus.AwaitingAdmission, _ => ExternalEventDeliveryStatus.Skipped };
            webhookBacking.PutTenantDelivery(new(value.EventId, s.AutomationId, s.Owner.AgentInstanceId, null, status, s, value.Decision, value.ReservedOccurrenceId, value.Code));
        }
    }
    private static bool PendingTenantEvent(TenantEventDelivery value) => value.ReservedOccurrenceId is null
        && (value.Status is EventMatchStatus.Pending or EventMatchStatus.Matched || value.Status == EventMatchStatus.FilterError && value.Decision?.Retryable == true);
    private static bool SameTenantEvent(TenantEventDelivery current, TenantEventDelivery expected) => current.TenantId == expected.TenantId
        && current.EventId == expected.EventId && JsonSerializer.Serialize(current.Subscription) == JsonSerializer.Serialize(expected.Subscription);
    private void RequireTenantEvent(TenantEventDelivery value, CancellationToken ct)
    {
        var s = value.Subscription;
        if (!_tenants.TryGetValue(value.TenantId, out var state)) throw new AccessDeniedException("tenant_unavailable");
        var agent = FindTenantAgentAsync(value.TenantId, s.Owner.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("event_executor_unavailable");
        if (agent.Instance.Lifecycle != AgentCore.Domain.Definitions.AgentInstanceLifecycle.Active || agent.PrincipalId != s.Owner.ExecutorPrincipalId)
            throw new AccessDeniedException("event_executor_unavailable");
        TenantSessionRules.RequireExecutor(state, agent);
        TenantEventAdmissionRules.RawReadAsync(state, agent.PrincipalId, s, [value.EventId], new IamAuthorizer(this), ct, TenantEventAdmissionRules.EmailAccounts(value)).GetAwaiter().GetResult();
        if (value.Core?.Event.Key == "email.message.received" && !CanDeliverTenantEmail(value.TenantId, value.EventId))
            throw new AccessDeniedException("email_event_source_changed");
        var automation = automationBacking.ReadTenantAutomation(value.TenantId, s.AutomationId) ?? throw new AccessDeniedException("event_subscription_changed");
        _ = TenantEventAdmissionRules.Child(automation, s);
        var policy = InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(agent), agent.Instance.SettingsOverrides);
        if (!AgentCore.Application.Triggers.OccurrenceCompatibility.Allows(policy, s.SourceKind)) throw new AccessDeniedException("event_policy_unavailable");
        if (s.SourceKind == TriggerSourceKind.ApplicationEvent && (s.ResourceId is not { } resource || webhookBacking.TenantSource(value.TenantId, resource)?.Status != WebhookEventStatus.Active))
            throw new AccessDeniedException("event_source_unavailable");
    }
    public ValueTask<TenantEventDelivery?> FindTenantEventDeliveryAsync(Guid tenant, TriggerSourceKind kind, Guid eventId, Guid triggerId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(WithTenantEvents(() => TenantEvent(tenant, kind, eventId, triggerId))); }
    public ValueTask<IReadOnlyList<TenantEventDelivery>> ListPendingTenantEventDeliveriesAsync(TriggerSourceKind kind, int limit, TenantEventCursor? after = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(WithTenantEvents<IReadOnlyList<TenantEventDelivery>>(() => {
            var rows = kind switch {
                TriggerSourceKind.CoreEvent => sessionBacking.CoreEvents.TenantDeliveries().Select(d => TenantEvent(d.Subscription.Owner.TenantId!.Value, kind, d.EventId, d.Subscription.TriggerId)),
                TriggerSourceKind.ApplicationEvent => webhookBacking.TenantDeliveries().Select(d => TenantEvent(d.Snapshot!.Owner.TenantId!.Value, kind, d.EventId, d.TriggerId)),
                _ => throw new ArgumentException("A concrete tenant event family is required.") };
            return rows.OfType<TenantEventDelivery>().Where(PendingTenantEvent)
                .Where(d => after is null || d.ReceivedAt > after.ReceivedAt || d.ReceivedAt == after.ReceivedAt && (string.CompareOrdinal(d.EventId.ToString("D"), after.EventId.ToString("D")) > 0
                    || d.EventId == after.EventId && string.CompareOrdinal(d.Subscription.TriggerId.ToString("D"), after.TriggerId.ToString("D")) > 0))
                .OrderBy(d => d.ReceivedAt).ThenBy(d => d.EventId.ToString("D"), StringComparer.Ordinal).ThenBy(d => d.Subscription.TriggerId.ToString("D"), StringComparer.Ordinal)
                .Take(Math.Clamp(limit, 1, 64)).ToArray();
        }));
    }
    public ValueTask<EventFilterResult> TryDecideTenantEventAsync(TenantEventDelivery source, EventFilterResult? expected, EventFilterResult decision, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(WithTenantEvents(() => {
            var current = TenantEvent(source.TenantId, source.Subscription.SourceKind, source.EventId, source.Subscription.TriggerId);
            if (current is null || !SameTenantEvent(current, source) || !PendingTenantEvent(current)) return new EventFilterResult(null, "error", "delivery-unavailable");
            RequireTenantEvent(current, ct);
            if (current.Decision is { } prior && (!prior.Retryable || prior != expected)) return prior;
            if (current.Decision != expected) return new EventFilterResult(null, "error", "delivery-changed");
            var next = current with { Decision = decision, Status = decision.Retryable ? EventMatchStatus.Pending : decision.Matched == true ? EventMatchStatus.Matched
                : decision.Matched == false ? EventMatchStatus.Filtered : EventMatchStatus.FilterError, Code = decision.Code };
            PutTenantEvent(next); return decision;
        }));
    }
    public ValueTask<bool> TryFinishTenantEventAsync(TenantEventDelivery source, EventMatchStatus status, string code, CancellationToken ct = default)
    {
        if (status is not (EventMatchStatus.PolicySkipped or EventMatchStatus.LoopSkipped or EventMatchStatus.BudgetSkipped)) throw new ArgumentException("Execution settlement belongs to the occurrence transaction.");
        if (string.IsNullOrWhiteSpace(code) || code.Length > 128) throw new ArgumentException("A bounded factual reason is required.");
        ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(WithTenantEvents(() => {
            var current = TenantEvent(source.TenantId, source.Subscription.SourceKind, source.EventId, source.Subscription.TriggerId);
            if (current is null || !SameTenantEvent(current, source) || !PendingTenantEvent(current)) return false;
            PutTenantEvent(current with { Status = status, Code = code }); return true;
        }));
    }
    public ValueTask<bool> TryCoalesceTenantEventAsync(TenantEventDelivery source, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(WithTenantEvents(() => {
            var current = TenantEvent(source.TenantId, source.Subscription.SourceKind, source.EventId, source.Subscription.TriggerId);
            if (current is null || !SameTenantEvent(current, source) || !PendingTenantEvent(current) || current.Status != EventMatchStatus.Matched || current.Decision?.Matched != true) return false;
            RequireTenantEvent(current, ct); var s = current.Subscription;
            if (s.Dispatch.Mode != EventDispatchMode.CoalesceLatest) throw new ArgumentException("A coalescing subscription is required.");
            var entry = TenantEventAdmissionRules.BucketSource(current, current.Core?.Event.Key ?? webhookBacking.TenantSource(current.TenantId, s.ResourceId!.Value)!.EventKey);
            var open = sessionBacking.CoreEvents.TenantBuckets().Where(b => b.Subscription == s && !b.Flushed && b.ReservedOccurrenceId is null);
            var bucket = open.Where(b => EventBucketPacking.Contains(b, entry.ReceivedAtUtc) && EventBucketPacking.CanAppend(b.Sources, entry)).OrderBy(b => b.DueAtUtc).FirstOrDefault();
            var fresh = bucket is null;
            bucket = fresh ? new(CoreEventPersistence.Id($"bucket:{entry.EventId:D}:{s.TriggerId:D}"), s, entry.ReceivedAtUtc.AddSeconds(s.Dispatch.WindowSeconds!.Value), [entry])
                : bucket! with { Sources = bucket!.Sources.Append(entry).ToArray() };
            if (!EventBucketPacking.CanAppend([], entry)) bucket = bucket with { Flushed = true, CompletionCode = "evidence-budget" };
            else if (fresh && open.Count() >= 32) bucket = bucket with { Flushed = true, CompletionCode = "bucket-capacity" };
            sessionBacking.CoreEvents.PutTenantBucket(bucket); PutTenantEvent(current with { Status = bucket.Flushed ? EventMatchStatus.BudgetSkipped : EventMatchStatus.Coalesced, Code = bucket.CompletionCode }); return true;
        }));
    }
    public ValueTask<IReadOnlyList<TenantEventBucket>> ListDueTenantEventBucketsAsync(DateTimeOffset at, int limit, EventRecoveryCursor? after = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(WithTenantEvents<IReadOnlyList<TenantEventBucket>>(() => sessionBacking.CoreEvents.TenantBuckets()
            .Where(b => !b.Flushed && b.ReservedOccurrenceId is null && b.DueAtUtc <= at && (after is null || b.DueAtUtc > after.AtUtc
                || b.DueAtUtc == after.AtUtc && string.CompareOrdinal(b.BucketId.ToString("D"), after.Id.ToString("D")) > 0))
            .OrderBy(b => b.DueAtUtc).ThenBy(b => b.BucketId.ToString("D"), StringComparer.Ordinal).Take(Math.Clamp(limit, 1, 64))
            .Select(b => new TenantEventBucket(b.Subscription.Owner.TenantId!.Value, b)).ToArray()));
    }
    public ValueTask<TenantEventBucket?> FindTenantEventBucketAsync(Guid tenant, Guid bucket, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(WithTenantEvents(() => sessionBacking.CoreEvents.TenantBucket(tenant, bucket) is { } row ? new TenantEventBucket(tenant, row) : null)); }
    public ValueTask<bool> TryFinishTenantEventBucketAsync(TenantEventBucket source, string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 128) throw new ArgumentException("A bounded factual reason is required.");
        ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(WithTenantEvents(() => {
            var current = sessionBacking.CoreEvents.TenantBucket(source.TenantId, source.Bucket.BucketId);
            if (current is null || current.Flushed || current.ReservedOccurrenceId is not null || JsonSerializer.Serialize(current) != JsonSerializer.Serialize(source.Bucket)) return false;
            sessionBacking.CoreEvents.PutTenantBucket(current with { Flushed = true, CompletionCode = code }); return true;
        }));
    }
    public ValueTask<CoreEventReceipt?> FindTenantCoreEventAsync(Guid tenant, Guid eventId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(WithTenantEvents(() => sessionBacking.CoreEvents.TenantReceipt(tenant, eventId))); }
    public ValueTask<TenantCoreEventPage> ListTenantCoreEventsAsync(Guid tenant, Guid instance, string key, int page, int pageSize, CancellationToken ct = default, IReadOnlyList<Guid>? allowedIds = null, IReadOnlyList<Guid>? emailAccounts = null)
    {
        if (!CoreEventCatalog.Keys.Contains(key)) throw new ArgumentException("Unknown built-in event source.");
        TenantWebhookRules.Page(page, pageSize); ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(WithTenantEvents(() => {
            var rows = sessionBacking.CoreEvents.TenantReceipts(tenant, instance, key).Where(r => allowedIds is null || allowedIds.Contains(r.Event.EventId))
                .Where(r => r.Event.Key != "email.message.received" || CredentialBacking().ReadTenantCredential(tenant, AgentCore.Domain.Email.EmailEventReferences.AccountId(r.Event.DataJson)) is { Value.Purpose: AgentCore.Domain.Credentials.CredentialPurpose.EmailAccount }
                    && (emailAccounts is null || emailAccounts.Contains(AgentCore.Domain.Email.EmailEventReferences.AccountId(r.Event.DataJson)))).OrderByDescending(r => r.ReceivedAtUtc).ThenBy(r => r.Event.EventId.ToString("D"), StringComparer.Ordinal).ToArray();
            return new TenantCoreEventPage(rows.Skip(page * pageSize).Take(pageSize).ToArray(), rows.Length, page, pageSize);
        }));
    }
}
