using System.Text.Json;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Events;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Events;

public sealed class CoreEventDispatcher(ICoreEventStore events, ITriggerStore triggers, IEventFilterEvaluator filters,
    ITriggerAdmissionGuard guard, IAgentInstanceStore instances, IAgentDefinitionStore definitions, IModelCatalog catalog,
    IIdGenerator ids, TimeProvider time, IExternalEventStore externalEvents, AutomationPresetCatalog presets)
{
    public async ValueTask<int> RunOnceAsync(CancellationToken ct = default)
    {
        var admittedCount = 0;
        foreach (var receipt in await events.PendingAsync(ct))
        {
            var e = receipt.Event;
            if (!receipt.Snapshotted)
            {
                var rows = await triggers.ListAsync(e.Owner, null, ct);
                await events.SnapshotAsync(e.EventId, rows.Where(a => a.Status == AutomationStatus.Active && a.Trigger is CoreEventTrigger t && t.CoreEventKey == e.Key)
                    .Select(a => new EventSubscriptionSnapshot(a.AutomationId, a.Owner, a.TriggerRevision, ((CoreEventTrigger)a.Trigger).FilterExpression, ((CoreEventTrigger)a.Trigger).Dispatch)).ToArray(), ct);
            }
            AgentCore.Application.Observability.RuntimeTelemetry.RecordEventDelivery("core", receipt.Snapshotted ? "recovered" : "emitted");
            foreach (var delivery in await events.DeliveriesAsync(e.EventId, ct))
            {
                if (delivery.Status is not (EventMatchStatus.Pending or EventMatchStatus.Matched)) continue;
                var s = delivery.Subscription;
                var current = await triggers.GetAsync(e.Owner, s.AutomationId, ct);
                var policy = await guard.EvaluateAsync(e.Owner, TriggerSourceKind.CoreEvent, ct);
                if (current?.Provenance.PresetId is { } recipe && !(await presets.OptionsAsync(e.Owner.AgentInstanceId, ct, current.ModelOverrideCatalogKey)).Single(p => p.Template.PresetId == recipe).Eligible)
                { await FinishDeliveryAsync(e.EventId, s.AutomationId, EventMatchStatus.PolicySkipped, "preset-prerequisite", ct); continue; }
                if (s.Owner != e.Owner || current?.Status != AutomationStatus.Active || policy.Kind != TriggerAdmissionDecisionKind.Allow)
                { await FinishDeliveryAsync(e.EventId, s.AutomationId, EventMatchStatus.PolicySkipped, "policy-denied", ct); continue; }
                if (e.TriggerDepth >= 4 || (e.VisitedAutomationIds ?? []).Contains(s.AutomationId))
                { await FinishDeliveryAsync(e.EventId, s.AutomationId, EventMatchStatus.LoopSkipped, "causal-loop", ct); continue; }
                var decision = delivery.Decision ?? (s.ExpressionVersion == "js-expression-v1" ? filters.Evaluate(s.FilterExpression, Project(receipt), ct) : new(null, "error", "filter-expression-version"));
                decision = await events.DecideAsync(e.EventId, s.AutomationId, decision, ct);
                AgentCore.Application.Observability.RuntimeTelemetry.RecordEventFilter("core", decision.Status);
                AgentCore.Application.Observability.RuntimeTelemetry.RecordEventDelivery("core", decision.Matched == true ? "matched" : decision.Matched == false ? "filtered" : "filter_error");
                if (decision.Matched != true) continue;
                if (s.Dispatch.Mode == EventDispatchMode.CoalesceLatest)
                { await events.CoalesceAsync(new(e.EventId, e.Owner, e.Key, receipt.ReceivedAtUtc, e.DataJson, e.RootAgentRunId, e.TriggerDepth, e.VisitedAutomationIds), s, ct); AgentCore.Application.Observability.RuntimeTelemetry.RecordEventDelivery("core", "coalesced"); continue; }
                var pin = current.ExecutionTarget.Kind == AutomationExecutionTargetKind.ExistingSession ? null : await ExecutionModelAdmission.ResolveAsync(catalog, instances, definitions, triggers, e.Owner, current.AutomationId, ct);
                if (pin is { FailureCode: not null }) { await FinishDeliveryAsync(e.EventId, s.AutomationId, EventMatchStatus.PolicySkipped, "model-unavailable", ct); continue; }
                var now = TriggerScheduleCalculator.Truncate(time.GetUtcNow());
                string evidence;
                try { evidence = AutomationRules.Evidence(current, new { eventId = e.EventId, source = JsonSerializer.Deserialize<JsonElement>(e.DataJson),
                    causation = new { rootAgentRunId = e.RootAgentRunId, triggerDepth = e.TriggerDepth + 1, visitedAutomationIds = (e.VisitedAutomationIds ?? []).Append(s.AutomationId).ToArray() } }); }
                catch (ArgumentException) { await FinishDeliveryAsync(e.EventId, s.AutomationId, EventMatchStatus.BudgetSkipped, "evidence-budget", ct); continue; }
                var key = $"core:{e.EventId:D}:{s.AutomationId:D}";
                var occurrence = new TriggerOccurrence(ids.NewId(), key, s.AutomationId, e.Owner, TriggerSourceKind.CoreEvent, null, e.OccurredAtUtc, now,
                    evidence, e.EventId, s.TriggerRevision, OccurrenceRoutingDisposition.Pending, null, 0, null, null, null,
                    modelPin: pin?.Pin, executionTarget: current.ExecutionTarget, completionDelivery: current.CompletionDelivery);
                var admitted = await triggers.AdmitOccurrenceAsync(occurrence, ct);
                await FinishDeliveryAsync(e.EventId, s.AutomationId, EventMatchStatus.Admitted, null, ct);
                if (admitted.Kind == TriggerOccurrenceAdmitKind.Admitted) admittedCount++;
                else AgentCore.Application.Observability.RuntimeTelemetry.RecordEventDelivery("core", "duplicate");
            }
        }
        foreach (var bucket in await events.DueBucketsAsync(time.GetUtcNow(), ct))
        {
            var s = bucket.Subscription;
            var current = await triggers.GetAsync(s.Owner, s.AutomationId, ct);
            var policy = await guard.EvaluateAsync(s.Owner, s.SourceKind, ct);
            if (s.SourceKind == TriggerSourceKind.ApplicationEvent && (s.ResourceId is not Guid resource || (await externalEvents.GetAsync(resource, ct))?.Status != WebhookEventStatus.Active))
            { await events.CompleteBucketAsync(bucket.BucketId, ct, "policy-denied"); continue; }
            if (current?.Provenance.PresetId is { } recipe && !(await presets.OptionsAsync(s.Owner.AgentInstanceId, ct, current.ModelOverrideCatalogKey)).Single(p => p.Template.PresetId == recipe).Eligible)
            { await events.CompleteBucketAsync(bucket.BucketId, ct, "policy-denied"); continue; }
            if (current?.Status != AutomationStatus.Active || policy.Kind != TriggerAdmissionDecisionKind.Allow)
            { await events.CompleteBucketAsync(bucket.BucketId, ct, "policy-denied"); continue; }
            var pin = current.ExecutionTarget.Kind == AutomationExecutionTargetKind.ExistingSession ? null : await ExecutionModelAdmission.ResolveAsync(catalog, instances, definitions, triggers, s.Owner, s.AutomationId, ct);
            if (pin is { FailureCode: not null }) { await events.CompleteBucketAsync(bucket.BucketId, ct, "model-unavailable"); continue; }
            var now = TriggerScheduleCalculator.Truncate(time.GetUtcNow());
            var sources = bucket.Sources;
            string evidence;
            try { evidence = AutomationRules.Evidence(current, new { bucketId = bucket.BucketId, count = bucket.Sources.Count,
                sources = sources.Select(e => new { eventId = e.EventId, data = JsonSerializer.Deserialize<JsonElement>(e.DataJson) }),
                causation = new { rootAgentRunId = bucket.Sources[0].RootAgentRunId, triggerDepth = bucket.Sources.Max(e => e.TriggerDepth) + 1,
                    visitedAutomationIds = bucket.Sources.SelectMany(e => e.VisitedAutomationIds ?? []).Append(s.AutomationId).Distinct().ToArray() } }); }
            catch (ArgumentException) { await events.CompleteBucketAsync(bucket.BucketId, ct, "evidence-budget"); continue; }
            var occurrence = new TriggerOccurrence(ids.NewId(), $"bucket:{bucket.BucketId:D}", s.AutomationId, s.Owner, s.SourceKind, null, bucket.DueAtUtc, now,
                evidence, bucket.BucketId, s.TriggerRevision, OccurrenceRoutingDisposition.Pending, null, 0, null, null, null,
                modelPin: pin?.Pin, executionTarget: current.ExecutionTarget, completionDelivery: current.CompletionDelivery);
            if ((await triggers.AdmitOccurrenceAsync(occurrence, ct)).Kind == TriggerOccurrenceAdmitKind.Admitted) admittedCount++;
            await events.CompleteBucketAsync(bucket.BucketId, ct);
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
        schemaVersion = 1, id = receipt.Event.EventId, type = "core." + receipt.Event.Key,
        occurredAtUtc = receipt.Event.OccurredAtUtc, receivedAtUtc = receipt.ReceivedAtUtc,
        source = new { kind = "core", key = receipt.Event.Key }, scope = new { agentInstanceId = receipt.Event.Owner.AgentInstanceId },
        causation = new { rootAgentRunId = receipt.Event.RootAgentRunId, triggerDepth = receipt.Event.TriggerDepth },
        data = JsonSerializer.Deserialize<JsonElement>(receipt.Event.DataJson)
    });
}
