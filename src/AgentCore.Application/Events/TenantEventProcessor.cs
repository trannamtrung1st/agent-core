using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Events;
using AgentCore.Domain.Triggers;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Events;

public sealed record TenantEventProcessingResult(int Matched, int Coalesced, int AwaitingReview, int Admitted);

// Resumes the existing receipt/delivery/bucket facts. Only the Automation owner
// can atomically reserve consent or create execution; this worker has no grants.
public sealed class TenantEventProcessor(ITenantEventStore events, IIamStore security, ITenantAutomationStore registrations,
    ITenantWebhookStore webhooks, ITenantEmailWatchStore emailWatches, TenantAutomationService automations, TenantRunConfigurationResolver configurations,
    IamAuthorizer iam, IEventFilterEvaluator filters, TimeProvider time, ILogger<TenantEventProcessor>? logger = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<TriggerSourceKind, TenantEventCursor?> cursors = [];
    private EventRecoveryCursor? bucketCursor;
    public async ValueTask<TenantEventProcessingResult> RunOnceAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var matched = 0; var coalesced = 0; var review = 0; var admitted = 0;
            foreach (var kind in new[] { TriggerSourceKind.CoreEvent, TriggerSourceKind.ApplicationEvent })
            {
                var cursor = cursors.GetValueOrDefault(kind);
                var page = await events.ListPendingTenantEventDeliveriesAsync(kind, 64, cursor, ct);
                if (page.Count == 0 && cursor is not null) page = await events.ListPendingTenantEventDeliveriesAsync(kind, 64, null, ct);
                cursors[kind] = page.Count == 0 ? null : new(page[^1].ReceivedAt, page[^1].EventId, page[^1].Subscription.TriggerId);
                foreach (var source in page)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        // Core technical matching is isolated from the Agent. No
                        // source data reaches a model before admission consent.
                        var envelope = await Envelope(source, ct);
                        var decision = EventFilterRecovery.Evaluate(filters, source.Subscription, source.Decision, envelope, time.GetUtcNow(), ct);
                        decision = await events.TryDecideTenantEventAsync(source, source.Decision, decision, ct);
                        if (decision.Matched != true) continue;
                        var current = await events.FindTenantEventDeliveryAsync(source.TenantId, kind, source.EventId, source.Subscription.TriggerId, ct);
                        if (current?.Status != EventMatchStatus.Matched || current.ReservedOccurrenceId is not null) continue;
                        matched++;
                        if (source.Subscription.Dispatch.Mode == EventDispatchMode.CoalesceLatest)
                        { if (await events.TryCoalesceTenantEventAsync(current, ct)) coalesced++; continue; }
                        var result = await automations.AdmitEventAsync(current, ct);
                        if (result is { Created: true, ApprovalRequestId: not null }) review++;
                        else if (result is { Created: true, RunId: not null }) admitted++;
                    }
                    catch (AccessDeniedException error)
                    { await events.TryFinishTenantEventAsync(source, error.ReasonCode == "event_causal_loop" ? EventMatchStatus.LoopSkipped : EventMatchStatus.PolicySkipped, error.ReasonCode, ct); }
                    catch (ArgumentException)
                    { await events.TryFinishTenantEventAsync(source, EventMatchStatus.BudgetSkipped, "event_evidence_invalid", ct); }
                    catch (SecurityConflictException error)
                    { Log(source.TenantId, source.EventId, source.Subscription.TriggerId, error.ReasonCode); }
                    catch (Exception error) when (!ct.IsCancellationRequested && error is not OutOfMemoryException and not StackOverflowException)
                    { Log(source.TenantId, source.EventId, source.Subscription.TriggerId, "event_processing_failed"); }
                }
            }
            var due = await events.ListDueTenantEventBucketsAsync(time.GetUtcNow(), 64, bucketCursor, ct);
            if (due.Count == 0 && bucketCursor is not null) due = await events.ListDueTenantEventBucketsAsync(time.GetUtcNow(), 64, null, ct);
            bucketCursor = due.Count == 0 ? null : new(due[^1].Bucket.DueAtUtc, due[^1].Bucket.BucketId);
            foreach (var source in due)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var result = await automations.AdmitEventAsync(source, ct);
                    if (result is { Created: true, ApprovalRequestId: not null }) review++;
                    else if (result is { Created: true, RunId: not null }) admitted++;
                }
                catch (AccessDeniedException error) { await events.TryFinishTenantEventBucketAsync(source, error.ReasonCode, ct); }
                catch (ArgumentException) { await events.TryFinishTenantEventBucketAsync(source, "event_evidence_invalid", ct); }
                catch (SecurityConflictException error) { Log(source.TenantId, source.Bucket.BucketId, source.Bucket.Subscription.TriggerId, error.ReasonCode); }
                catch (Exception error) when (!ct.IsCancellationRequested && error is not OutOfMemoryException and not StackOverflowException)
                { Log(source.TenantId, source.Bucket.BucketId, source.Bucket.Subscription.TriggerId, "event_processing_failed"); }
            }
            return new(matched, coalesced, review, admitted);
        }
        finally { gate.Release(); }
    }
    private async ValueTask<JsonElement> Envelope(TenantEventDelivery source, CancellationToken ct)
    {
        var state = await security.ReadTenantAsync(source.TenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        var s = source.Subscription; var current = await registrations.FindTenantAutomationAsync(source.TenantId, s.AutomationId, ct) ?? throw new AccessDeniedException("event_subscription_changed");
        _ = TenantEventAdmissionRules.Child(current, s);
        if (current.ExpiresAtUtc <= time.GetUtcNow()) throw new AccessDeniedException("automation_expired");
        var (agent, policy) = await configurations.CurrentPolicyAsync(source.TenantId, s.Owner.AgentInstanceId, ct);
        if (agent.PrincipalId != s.Owner.ExecutorPrincipalId || agent.Instance.Lifecycle != AgentInstanceLifecycle.Active) throw new AccessDeniedException("event_executor_unavailable");
        await TenantEventAdmissionRules.RawReadAsync(state, agent.PrincipalId, s, [source.EventId], iam, ct, TenantEventAdmissionRules.EmailAccounts(source));
        if (!OccurrenceCompatibility.Allows(policy, s.SourceKind)) throw new AccessDeniedException("event_policy_unavailable");
        if (source.Core is { } core)
        {
            if (core.Event.Key == "email.message.received" && !await emailWatches.CanDeliverTenantEmailEventAsync(source.TenantId, source.EventId, ct))
                throw new AccessDeniedException("email_event_source_unavailable");
            return ProjectCore(core);
        }
        var receipt = source.Webhook ?? throw new AccessDeniedException("event_source_unavailable");
        var webhook = await webhooks.FindTenantWebhookAsync(source.TenantId, receipt.ResourceId, ct);
        if (webhook?.Status != WebhookEventStatus.Active || webhook.ResourceId != s.ResourceId) throw new AccessDeniedException("event_source_unavailable");
        return JsonSerializer.SerializeToElement(new { schemaVersion = 1, id = receipt.EventId, type = "webhook." + webhook.EventKey,
            occurredAtUtc = receipt.OccurredAtUtc, receivedAtUtc = receipt.AdmittedAtUtc, source = new { kind = "webhook", key = webhook.EventKey },
            scope = new { agentInstanceId = s.Owner.AgentInstanceId }, causation = new { rootAgentRunId = (Guid?)null, triggerDepth = 0 },
            data = JsonSerializer.Deserialize<JsonElement>(receipt.EvidenceJson).GetProperty("data") });
    }
    private void Log(Guid tenant, Guid source, Guid trigger, string code) => logger?.LogWarning("Tenant event processing deferred: tenant {TenantId}, source {EventId}, trigger {TriggerId}, code {Code}.", tenant, source, trigger, code);
    private static JsonElement ProjectCore(CoreEventReceipt receipt) => JsonSerializer.SerializeToElement(new
    {
        schemaVersion = 1,
        id = receipt.Event.EventId,
        type = "core." + receipt.Event.Key,
        occurredAtUtc = receipt.Event.OccurredAtUtc,
        receivedAtUtc = receipt.ReceivedAtUtc,
        source = new { kind = "core", key = receipt.Event.Key },
        scope = new { agentInstanceId = receipt.Event.Owner.AgentInstanceId },
        causation = new { rootAgentRunId = receipt.Event.RootAgentRunId, triggerDepth = receipt.Event.TriggerDepth },
        data = JsonSerializer.Deserialize<JsonElement>(receipt.Event.DataJson)
    });
}
