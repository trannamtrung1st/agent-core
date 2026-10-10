using System.Globalization;
using AgentCore.Api.Http;
using AgentCore.Api.Mapping;
using AgentCore.Application.Experience;
using AgentCore.Application.Events;
using AgentCore.Domain.Events;
using System.Text.Json;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Triggers;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Triggers;

namespace AgentCore.Api;

internal static class AdminAutomationEndpoints
{
    public static void Map(RouteGroupBuilder admin)
    {
        var group = admin.MapGroup("/agent-instances/{instanceId:guid}/automations");
        group.MapGet("/core-event-types", (Guid instanceId, ExperienceService owners, ITriggerAdmissionGuard guard, ILocalUserProfileService profiles, CancellationToken ct) => Respond(async () =>
        {
            await owners.RequireInstanceAsync(instanceId, ct);
            await profiles.GetLocalProfileAsync(ct);
            var policy = await guard.EvaluateAsync(new(instanceId, LocalUserProfile.Id), TriggerSourceKind.CoreEvent, ct);
            return CoreEventCatalog.Keys.Select(key => new CoreEventTypeResponse(key, policy.Kind == TriggerAdmissionDecisionKind.Allow, policy.Reason,
                CoreEventDispatcher.Project(new(new(Guid.Empty, "sample", new(instanceId, LocalUserProfile.Id), key, DateTimeOffset.UnixEpoch,
                    BuiltInEventDefinitions.ExampleData(key, instanceId)), DateTimeOffset.UnixEpoch, false)),
                JsonSerializer.SerializeToElement(new { schemaVersion = 1, fields = JsonSerializer.Deserialize<JsonElement>(BuiltInEventDefinitions.ExampleData(key, instanceId)).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind.ToString()), sourceKind = "core", type = "core." + key }))).ToArray();
        }));
        group.MapPost("/filter-test", (Guid instanceId, AutomationFilterTestRequest request, ExperienceService owners, IEventFilterEvaluator filters, CancellationToken ct) => Respond(async () =>
        {
            await owners.RequireInstanceAsync(instanceId, ct);
            if (request.Event.ValueKind != JsonValueKind.Object || !request.Event.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out var version) || version != 1)
                return new EventFilterResult(null, "error", "filter-envelope-schema");
            return filters.Evaluate(request.Expression, request.Event, ct);
        }));
        group.MapGet("/{automationId:guid}/unreviewed-sources", (Guid instanceId, Guid automationId, string? cursor, ExperienceService owners, ICoreEventStore events, ITriggerStore triggers, CancellationToken ct) => Respond(async () =>
        {
            await owners.RequireInstanceAsync(instanceId, ct); var owner = new TriggerOwner(instanceId, LocalUserProfile.Id);
            if (await triggers.GetAsync(owner, automationId, ct) is null) throw AgentCoreErrors.NotFound("Automation was not found.");
            return await events.CoveragePageAsync(owner, automationId, cursor, 24, ct);
        }));
        group.MapGet("/deliveries", (Guid instanceId, ExperienceService owners, ICoreEventStore events, CancellationToken ct) => Respond(async () =>
        { await owners.RequireInstanceAsync(instanceId, ct); return (await events.ActivityAsync(new(instanceId, LocalUserProfile.Id), ct)).Select(d => new { d.EventId, d.Subscription.AutomationId, d.Subscription.TriggerId, d.Subscription.TriggerRevision, status = d.Status.ToString(), d.Code, decision = d.Decision?.Status }); }));
        group.MapGet("/presets", (Guid instanceId, AutomationPresetCatalog presets, CancellationToken ct) => Respond(async () =>
            (await presets.OptionsAsync(instanceId, ct)).Select(p => new AutomationPresetResponse(p.Template.PresetId, p.Template.PresetVersion, p.Template.Name, p.Template.Description, p.Template.Instructions,
                p.Template.TriggerKind == "coreEvent" ? new("coreEvent", CoreEventKey: p.Template.CoreEventKey, FilterExpression: p.Template.FilterExpression, Dispatch: new("coalesceLatest", 900))
                    : new("schedule", new("weekly", "UTC", LocalTime: "09:00", Weekdays: [1])), p.Eligible, p.Prerequisites)).ToArray()));
        group.MapGet("", (Guid instanceId, ExperienceService instances, ITriggerStore store, IAgentRunStore runs,
            IAgentDefinitionStore definitions, IModelCatalog catalog, IMemoryStore memory, ITriggerAdmissionGuard guard, IExternalEventStore events, CancellationToken ct) => Respond(async () =>
        {
            var instance = await instances.RequireInstanceAsync(instanceId, ct);
            var definition = await definitions.GetAsync(instance.DefinitionId, instance.ActiveVersion, ct);
            var rows = await store.ListAsync(new(instanceId, LocalUserProfile.Id), null, ct);
            var items = new List<AutomationResponse>();
            foreach (var registration in rows.Where(r => r.Status != AutomationStatus.Cancelled))
            {
                var last = await runs.GetLatestForAutomationAsync(new(instanceId, LocalUserProfile.Id), registration.AutomationId, ct);
                var effective = registration.ExecutionTarget.SessionId is { } targetId
                    ? (await memory.LoadMetadataAsync(targetId, ct))?.ModelSelection?.CatalogKey
                    : definition is null ? null : ExecutionModelPolicy.Resolve(catalog, definition, instance, registration).Pin?.CatalogKey;
                var projected = Project(registration, effective, last);
                var children = new List<AutomationTriggerDto>();
                foreach (var child in registration.Triggers)
                {
                    var decision = await guard.EvaluateAsync(registration.Owner, AutomationRules.Source(child.Configuration), ct);
                    var canExecute = decision.Kind == TriggerAdmissionDecisionKind.Allow;
                    var reason = canExecute ? null : decision.Reason;
                    if (child.Configuration is EventTrigger webhook && (await events.GetAsync(webhook.EventId, ct))?.Status != AgentCore.Domain.Events.WebhookEventStatus.Active)
                    { canExecute = false; reason = "Webhook Event is revoked or unavailable."; }
                    if (effective is null) { canExecute = false; reason = "Execution model or destination is unavailable."; }
                    children.Add(TriggerRecord(child) with { Eligible = canExecute, EligibilityReason = reason });
                }
                items.Add(projected with { Triggers = children });
            }
            var schedulesAllowed = definition is not null && OccurrenceCompatibility.Allows(definition, TriggerSourceKind.Schedule);
            return new AutomationReview(items,
                definition?.TriggerPolicy is { } p ? new AutomationPolicy(schedulesAllowed && p.AllowOneShot, schedulesAllowed && p.AllowDaily, schedulesAllowed && p.AllowWeekly, schedulesAllowed && p.AllowFixedInterval,
                    p.AllowIndefiniteRecurrence, p.OneShotHorizonDays, p.MinRecurrenceDays, p.MinFixedIntervalSeconds, p.MaxActiveRegistrations, p.Enabled && p.AllowedSourceKinds.Contains("applicationEvent", StringComparer.Ordinal), p.Enabled && p.AllowedSourceKinds.Contains("coreEvent", StringComparer.Ordinal)) : null);
        }));
        group.MapPost("", (Guid instanceId, AutomationRequest request, AdminAutomationAuthoringService service, CancellationToken ct) => Respond(async () =>
            Project(await service.SaveAsync(instanceId, null, request.ExpectedRevision, request.Enabled, request.Name, request.Instructions, ParseTriggers(request), request.ModelKey, request.ReasoningEffort, ct, executionTarget: ParseTarget(request.ExecutionTarget), completionDelivery: ParseDelivery(request.CompletionDelivery), requiresTools: request.RequiresTools, requiresVision: request.RequiresVision, presetId: request.PresetId, presetVersion: request.PresetVersion))));
        group.MapPut("/{automationId:guid}", (Guid instanceId, Guid automationId, AutomationRequest request, AdminAutomationAuthoringService service, CancellationToken ct) => Respond(async () =>
            Project(await service.SaveAsync(instanceId, automationId, request.ExpectedRevision, request.Enabled, request.Name, request.Instructions, ParseTriggers(request), request.ModelKey, request.ReasoningEffort, ct, executionTarget: ParseTarget(request.ExecutionTarget), completionDelivery: ParseDelivery(request.CompletionDelivery), requiresTools: request.RequiresTools, requiresVision: request.RequiresVision, presetId: request.PresetId, presetVersion: request.PresetVersion))));
        group.MapDelete("/{automationId:guid}", (Guid instanceId, Guid automationId, [Microsoft.AspNetCore.Mvc.FromBody] ContinuityRevisionRequest request, AdminAutomationAuthoringService service, CancellationToken ct) => Respond(async () =>
        { await service.DeleteAsync(instanceId, automationId, request.ExpectedRevision, ct); return new { cancelled = true }; }));
        group.MapPost("/{automationId:guid}/run", (Guid instanceId, Guid automationId, ContinuityRevisionRequest request, AdminAutomationAuthoringService service, CancellationToken ct) => Respond(async () =>
        { var occurrence = await service.RunNowAsync(instanceId, automationId, request.ExpectedRevision, ct); return new { occurrenceId = occurrence.OccurrenceId }; }));
    }
    private static AutomationExecutionTarget ParseTarget(AutomationExecutionTargetDto? target) => target?.Kind switch
    {
        "backgroundSession" when target.SessionId is null => AutomationExecutionTarget.Background,
        "existingSession" when Guid.TryParse(target.SessionId, out var id) => AutomationExecutionTarget.Existing(id),
        _ => throw AgentCoreErrors.Validation("An explicit executionTarget is required.")
    };
    private static AutomationCompletionDelivery ParseDelivery(AutomationCompletionDeliveryDto? delivery) => delivery?.Kind switch
    {
        "none" when delivery.SessionId is null => AutomationCompletionDelivery.None,
        "toSession" when Guid.TryParse(delivery.SessionId, out var id) => AutomationCompletionDelivery.ToSession(id),
        _ => throw AgentCoreErrors.Validation("An explicit completionDelivery is required.")
    };
    internal static IReadOnlyList<AutomationTriggerRecord> ParseTriggers(AutomationRequest request)
    {
        if (request.Triggers is null) throw AgentCoreErrors.Validation("Use the canonical triggers collection.");
        return request.Triggers.Select(t => new AutomationTriggerRecord(Guid.Parse(t.TriggerId ?? ""), ParseTrigger(t), t.Enabled, t.Revision)).ToArray();
    }
    private static AutomationTrigger ParseTrigger(AutomationTriggerDto? trigger)
    {
        if (trigger is null) throw AgentCoreErrors.Validation("Trigger is required.");
        if (trigger.Source is { } sourceRef)
        {
            var reference = EventSourceReference.Parse(sourceRef.Kind, sourceRef.Key, sourceRef.EventId is null ? null : Guid.Parse(sourceRef.EventId));
            if (trigger.Kind != "event" || trigger.Schedule is not null || trigger.EventId is not null || trigger.CoreEventKey is not null)
                throw AgentCoreErrors.Validation("An Event trigger requires only its typed source.");
            return reference.Kind == "builtin" ? new CoreEventTrigger(reference.Key!, trigger.FilterExpression, Dispatch(trigger.Dispatch))
                : new EventTrigger(reference.EventId!.Value, trigger.FilterExpression, Dispatch(trigger.Dispatch));
        }
        return trigger.Kind switch
        {
            "schedule" when trigger.EventId is null && trigger.CoreEventKey is null && trigger.FilterExpression is null && trigger.Dispatch is null => new ScheduleTrigger(Parse(trigger.Schedule)),
            _ => throw AgentCoreErrors.Validation("Trigger must contain either Schedule timing or an Event ID.")
        };
    }
    private static EventDispatch Dispatch(AutomationDispatchDto? d) => d?.Mode switch
    { null => new(), "everyMatch" => new(EventDispatchMode.EveryMatch, d.WindowSeconds), "coalesceLatest" => new(EventDispatchMode.CoalesceLatest, d.WindowSeconds), _ => throw AgentCoreErrors.Validation("Event dispatch is invalid.") };
    private static AutomationDispatchDto DispatchDto(EventDispatch d) => new(d.Mode == EventDispatchMode.EveryMatch ? "everyMatch" : "coalesceLatest", d.WindowSeconds);
    private static AutomationTriggerDto Trigger(AutomationTrigger trigger) => trigger switch
    {
        ScheduleTrigger s => new("schedule", Timing(s.Schedule)),
        EventTrigger e => new("event", EventId: e.EventId.ToString("D"), FilterExpression: e.FilterExpression, Dispatch: DispatchDto(e.Dispatch)),
        CoreEventTrigger e => new("coreEvent", CoreEventKey: e.CoreEventKey, FilterExpression: e.FilterExpression, Dispatch: DispatchDto(e.Dispatch)),
        _ => throw AgentCoreErrors.Validation("Trigger is unavailable.")
    };
    internal static AutomationTriggerDto TriggerRecord(AutomationTriggerRecord t) => (t.Configuration switch
    {
        ScheduleTrigger s => new AutomationTriggerDto("schedule", Timing(s.Schedule)),
        FilteredEventTrigger e => new AutomationTriggerDto("event", FilterExpression: e.FilterExpression, Dispatch: DispatchDto(e.Dispatch),
            Source: new(t.Source!.Kind, t.Source.Key, t.Source.EventId?.ToString("D"))),
        _ => throw AgentCoreErrors.Validation("Trigger is unavailable.")
    }) with { TriggerId = t.TriggerId.ToString("D"), Enabled = t.Enabled, Revision = t.Revision };
    private static TriggerSchedule Parse(AutomationTiming? t)
    {
        if (t is null) throw AgentCoreErrors.Validation("Schedule timing is required.");
        DateTimeOffset Utc(string? value) => DateTimeOffset.Parse(value ?? "", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
        DateOnly? Date(string? value) => value is null ? null : DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        TimeOnly Time() => TimeOnly.ParseExact(t.LocalTime ?? "", "HH:mm", CultureInfo.InvariantCulture);
        return t.Kind switch
        {
            "oneShot" => new OneShotSchedule(Utc(t.AtUtc), t.TimeZone),
            "fixedInterval" => new FixedIntervalSchedule(t.Interval, Utc(t.AnchorAtUtc), t.EndAtUtc is null ? null : Utc(t.EndAtUtc), t.MaxOccurrences),
            "daily" => new DailySchedule(t.Interval, Time(), t.TimeZone, Date(t.StartDate), Date(t.EndDate), t.MaxOccurrences),
            "weekly" => new WeeklySchedule(t.Interval, (t.Weekdays ?? []).Select(d => (DayOfWeek)d).ToArray(), Time(), t.TimeZone, Date(t.StartDate), Date(t.EndDate), t.MaxOccurrences),
            _ => throw AgentCoreErrors.Validation("Schedule kind is invalid.")
        };
    }
    private static AutomationResponse Project(Automation r, string? effective = null, AgentRun? run = null) =>
        new(r.AutomationId.ToString("D"), r.Revision, r.Name, r.Instructions, r.Status == AutomationStatus.Active, r.Status.ToString(),
            r.Provenance.AuthorizationOrigin.ToString(), r.Provenance.SourceSessionId?.ToString("D"), r.Provenance.SourceEventId?.ToString("D"),
            HttpMapping.Format(r.Provenance.CreatedAt), r.NextOccurrenceAtUtc is { } next ? HttpMapping.Format(next) : null,
            r.ModelOverrideCatalogKey, r.ModelOverrideReasoningEffort, effective, run?.AgentRunId.ToString("D"), run?.Status.ToString(), run?.Result is { } result ? result.OutcomeKind.ToString() : null,
            new(r.ExecutionTarget.SessionId is null ? "backgroundSession" : "existingSession", r.ExecutionTarget.SessionId?.ToString("D")),
            new(r.CompletionDelivery.SessionId is null ? "none" : "toSession", r.CompletionDelivery.SessionId?.ToString("D")), r.SuspensionReason, r.RequiresTools, r.RequiresVision, r.Provenance.PresetId, r.Provenance.PresetVersion, r.Triggers.Select(TriggerRecord).ToArray());
    private static AutomationTiming Timing(TriggerSchedule s) => s switch
    {
        OneShotSchedule t => new("oneShot", t.TimeZoneId, HttpMapping.Format(t.AtUtc)),
        FixedIntervalSchedule t => new("fixedInterval", Interval: t.IntervalSeconds, AnchorAtUtc: HttpMapping.Format(t.AnchorAtUtc), EndAtUtc: t.EndAtUtc is { } e ? HttpMapping.Format(e) : null, MaxOccurrences: t.MaxOccurrences),
        DailySchedule t => new("daily", t.TimeZoneId, Interval: t.IntervalDays, LocalTime: t.LocalTime.ToString("HH:mm"), StartDate: t.StartDate?.ToString("yyyy-MM-dd"), EndDate: t.EndDate?.ToString("yyyy-MM-dd"), MaxOccurrences: t.MaxOccurrences),
        WeeklySchedule t => new("weekly", t.TimeZoneId, Interval: t.IntervalWeeks, LocalTime: t.LocalTime.ToString("HH:mm"), Weekdays: t.Weekdays.Select(d => (int)d).ToArray(), StartDate: t.StartDate?.ToString("yyyy-MM-dd"), EndDate: t.EndDate?.ToString("yyyy-MM-dd"), MaxOccurrences: t.MaxOccurrences),
        _ => throw AgentCoreErrors.Validation("Schedule kind is unavailable.")
    };
    private static async Task<IResult> Respond<T>(Func<Task<T>> action)
    {
        try { return Results.Json(await action()); }
        catch (AgentCoreException ex) { return ProblemResults.From(ex); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or TriggerTimeZoneUnavailableException)
        { return ProblemResults.From(AgentCoreErrors.Validation("Schedule timing or configuration is invalid.")); }
    }
}
