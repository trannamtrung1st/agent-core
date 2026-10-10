using System.Text.Json;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Events;
using AgentCore.Domain.Triggers;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Events;

public sealed class CoreEventDispatcher(ICoreEventStore events, ITriggerStore triggers, IEventFilterEvaluator filters,
    ITriggerAdmissionGuard guard, IAgentInstanceStore instances, IAgentDefinitionStore definitions, IModelCatalog catalog,
    IIdGenerator ids, TimeProvider time, IExternalEventStore externalEvents, ILogger<CoreEventDispatcher>? logger = null)
{
    private EventRecoveryCursor? receiptCursor;
    private EventRecoveryCursor? bucketCursor;

    public async ValueTask<int> RunOnceAsync(CancellationToken ct = default)
    {
        var admittedCount = 0;
        var pending = await events.PendingAsync(ct, receiptCursor);
        if (pending.Count == 0 && receiptCursor is not null) pending = await events.PendingAsync(ct);
        receiptCursor = pending.Count == 0 ? null : new(pending[^1].ReceivedAtUtc, pending[^1].Event.EventId);
        Observability.RuntimeTelemetry.RecordEventRecoveryPage("core", pending.Count,
            pending.Count == 0 ? 0 : (time.GetUtcNow() - pending.Min(r => r.ReceivedAtUtc)).TotalMilliseconds);
        foreach (var receipt in pending)
        {
            try
            {
                var e = receipt.Event;
                if (!receipt.Snapshotted)
                {
                    var rows = await triggers.ListAsync(e.Owner, null, ct);
                    await events.SnapshotAsync(e.EventId, rows.Where(a => a.Status == AutomationStatus.Active)
                        .SelectMany(a => a.Triggers.Where(t => t.Enabled && t.Configuration is CoreEventTrigger c && c.CoreEventKey == e.Key)
                            .Select(t => new EventSubscriptionSnapshot(a.AutomationId, a.Owner, t.Revision,
                                ((CoreEventTrigger)t.Configuration).FilterExpression, ((CoreEventTrigger)t.Configuration).Dispatch) { TriggerId = t.TriggerId })).ToArray(), ct);
                }
                AgentCore.Application.Observability.RuntimeTelemetry.RecordEventDelivery("core", receipt.Snapshotted ? "recovered" : "emitted");
                foreach (var delivery in await events.DeliveriesAsync(e.EventId, ct))
                {
                    try
                    {
                        if (delivery.Status is not (EventMatchStatus.Pending or EventMatchStatus.Matched) && !(delivery.Status == EventMatchStatus.FilterError && delivery.Decision?.Retryable == true)) continue;
                        var s = delivery.Subscription;
                        var current = await triggers.GetAsync(e.Owner, s.AutomationId, ct);
                        var policy = await guard.EvaluateAsync(e.Owner, TriggerSourceKind.CoreEvent, ct);
                        if (s.Owner != e.Owner || current?.Status != AutomationStatus.Active || current.Triggers.All(t => t.TriggerId != s.TriggerId || !t.Enabled) || policy.Kind != TriggerAdmissionDecisionKind.Allow)
                        { await FinishDeliveryAsync(e.EventId, s.TriggerId, EventMatchStatus.PolicySkipped, "policy-denied", ct); continue; }
                        if (e.TriggerDepth >= 4 || (e.VisitedAutomationIds ?? []).Contains(s.AutomationId))
                        { await FinishDeliveryAsync(e.EventId, s.TriggerId, EventMatchStatus.LoopSkipped, "causal-loop", ct); continue; }
                        var decision = EventFilterRecovery.Evaluate(filters, s, delivery.Decision, Project(receipt), time.GetUtcNow(), ct);
                        decision = await events.DecideAsync(e.EventId, s.TriggerId, decision, ct, delivery.Decision);
                        AgentCore.Application.Observability.RuntimeTelemetry.RecordEventFilter("core", decision.Status);
                        AgentCore.Application.Observability.RuntimeTelemetry.RecordEventDelivery("core", decision.Retryable ? "retry_pending" : decision.Matched == true ? "matched" : decision.Matched == false ? "filtered" : "filter_error");
                        if (decision.Matched != true) continue;
                        if (s.Dispatch.Mode == EventDispatchMode.CoalesceLatest)
                        { await events.CoalesceAsync(new(e.EventId, e.Owner, e.Key, receipt.ReceivedAtUtc, e.DataJson, e.RootAgentRunId, e.TriggerDepth, e.VisitedAutomationIds), s, ct); AgentCore.Application.Observability.RuntimeTelemetry.RecordEventDelivery("core", "coalesced"); continue; }
                        var pin = current.ExecutionTarget.Kind == AutomationExecutionTargetKind.ExistingSession ? null : await ExecutionModelAdmission.ResolveAsync(catalog, instances, definitions, triggers, e.Owner, current.AutomationId, ct);
                        if (pin is { FailureCode: not null }) { await FinishDeliveryAsync(e.EventId, s.TriggerId, EventMatchStatus.PolicySkipped, "model-unavailable", ct); continue; }
                        var now = TriggerScheduleCalculator.Truncate(time.GetUtcNow());
                        string evidence;
                        try
                        {
                            evidence = AutomationRules.Evidence(current, new
                            {
                                eventId = e.EventId,
                                source = JsonSerializer.Deserialize<JsonElement>(e.DataJson),
                                causation = new { rootAgentRunId = e.RootAgentRunId, triggerDepth = e.TriggerDepth + 1, visitedAutomationIds = (e.VisitedAutomationIds ?? []).Append(s.AutomationId).ToArray() }
                            }, current.Triggers.Single(t => t.TriggerId == s.TriggerId));
                        }
                        catch (ArgumentException) { await FinishDeliveryAsync(e.EventId, s.TriggerId, EventMatchStatus.BudgetSkipped, "evidence-budget", ct); continue; }
                        var key = $"core:{e.EventId:D}:{s.TriggerId:D}";
                        var occurrence = new TriggerOccurrence(ids.NewId(), key, s.AutomationId, e.Owner, TriggerSourceKind.CoreEvent, null, e.OccurredAtUtc, now,
                            evidence, e.EventId, s.TriggerRevision, OccurrenceRoutingDisposition.Pending, null, 0, null, null, null,
                            modelPin: pin?.Pin, executionTarget: current.ExecutionTarget, completionDelivery: current.CompletionDelivery, triggerId: s.TriggerId);
                        var admitted = await triggers.AdmitOccurrenceAsync(occurrence, ct);
                        await FinishDeliveryAsync(e.EventId, s.TriggerId, EventMatchStatus.Admitted, null, ct);
                        if (admitted.Kind == TriggerOccurrenceAdmitKind.Admitted) admittedCount++;
                        else AgentCore.Application.Observability.RuntimeTelemetry.RecordEventDelivery("core", "duplicate");
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OutOfMemoryException and not StackOverflowException)
                    {
                        logger?.LogWarning(ex, "Core event {EventId} subscriber {AutomationId} will retry.", receipt.Event.EventId, delivery.Subscription.AutomationId);
                    }
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OutOfMemoryException and not StackOverflowException)
            {
                logger?.LogWarning(ex, "Core event {EventId} fan-out will retry.", receipt.Event.EventId);
            }
        }
        var recoveryNow = time.GetUtcNow();
        var due = await events.DueBucketsAsync(recoveryNow, ct, bucketCursor);
        if (due.Count == 0 && bucketCursor is not null) due = await events.DueBucketsAsync(recoveryNow, ct);
        bucketCursor = due.Count == 0 ? null : new(due[^1].DueAtUtc, due[^1].BucketId);
        Observability.RuntimeTelemetry.RecordEventRecoveryPage("bucket", due.Count,
            due.Count == 0 ? 0 : (recoveryNow - due.Min(b => b.DueAtUtc)).TotalMilliseconds);
        foreach (var bucket in due)
        {
            try
            {
                var s = bucket.Subscription;
                var current = await triggers.GetAsync(s.Owner, s.AutomationId, ct);
                var policy = await guard.EvaluateAsync(s.Owner, s.SourceKind, ct);
                if (s.SourceKind == TriggerSourceKind.ApplicationEvent && (s.ResourceId is not Guid resource || (await externalEvents.GetAsync(resource, ct))?.Status != WebhookEventStatus.Active))
                { await events.CompleteBucketAsync(bucket.BucketId, ct, "policy-denied"); continue; }
                if (current?.Status != AutomationStatus.Active || current.Triggers.All(t => t.TriggerId != s.TriggerId || !t.Enabled) || policy.Kind != TriggerAdmissionDecisionKind.Allow)
                { await events.CompleteBucketAsync(bucket.BucketId, ct, "policy-denied"); continue; }
                var pin = current.ExecutionTarget.Kind == AutomationExecutionTargetKind.ExistingSession ? null : await ExecutionModelAdmission.ResolveAsync(catalog, instances, definitions, triggers, s.Owner, s.AutomationId, ct);
                if (pin is { FailureCode: not null }) { await events.CompleteBucketAsync(bucket.BucketId, ct, "model-unavailable"); continue; }
                var now = TriggerScheduleCalculator.Truncate(time.GetUtcNow());
                var sources = bucket.Sources;
                string evidence;
                try
                {
                    evidence = AutomationRules.Evidence(current, new
                    {
                        bucketId = bucket.BucketId,
                        count = bucket.Sources.Count,
                        sources = sources.Select(e => new { eventId = e.EventId, data = JsonSerializer.Deserialize<JsonElement>(e.DataJson) }),
                        causation = new
                        {
                            rootAgentRunId = bucket.Sources[0].RootAgentRunId,
                            triggerDepth = bucket.Sources.Max(e => e.TriggerDepth) + 1,
                            visitedAutomationIds = bucket.Sources.SelectMany(e => e.VisitedAutomationIds ?? []).Append(s.AutomationId).Distinct().ToArray()
                        }
                    }, current.Triggers.Single(t => t.TriggerId == s.TriggerId));
                }
                catch (ArgumentException) { await events.CompleteBucketAsync(bucket.BucketId, ct, "evidence-budget"); continue; }
                var occurrence = new TriggerOccurrence(ids.NewId(), $"bucket:{bucket.BucketId:D}", s.AutomationId, s.Owner, s.SourceKind, null, bucket.DueAtUtc, now,
                    evidence, bucket.BucketId, s.TriggerRevision, OccurrenceRoutingDisposition.Pending, null, 0, null, null, null,
                    modelPin: pin?.Pin, executionTarget: current.ExecutionTarget, completionDelivery: current.CompletionDelivery, triggerId: s.TriggerId);
                if ((await triggers.AdmitOccurrenceAsync(occurrence, ct)).Kind == TriggerOccurrenceAdmitKind.Admitted) admittedCount++;
                await events.CompleteBucketAsync(bucket.BucketId, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OutOfMemoryException and not StackOverflowException)
            {
                logger?.LogWarning(ex, "Event bucket {BucketId} will retry.", bucket.BucketId);
            }
        }
        return admittedCount;
    }
    private async ValueTask FinishDeliveryAsync(Guid eventId, Guid automationId, EventMatchStatus status, string? code, CancellationToken ct)
    {
        await events.FinishAsync(eventId, automationId, status, code, ct);
        AgentCore.Application.Observability.RuntimeTelemetry.RecordEventDelivery("core", status switch
        { EventMatchStatus.PolicySkipped => "policy_denied", EventMatchStatus.LoopSkipped => "loop_skipped", EventMatchStatus.BudgetSkipped => "budget_skipped", EventMatchStatus.Admitted => "admitted", _ => "other" });
    }

    public static JsonElement Project(CoreEventReceipt receipt) => JsonSerializer.SerializeToElement(new
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
