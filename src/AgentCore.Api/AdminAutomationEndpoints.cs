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
                    ExampleData(key, instanceId)), DateTimeOffset.UnixEpoch, false)),
                JsonSerializer.SerializeToElement(new { schemaVersion = 1, fields = JsonSerializer.Deserialize<JsonElement>(ExampleData(key, instanceId)).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind.ToString()), sourceKind = "core", type = "core." + key }))).ToArray();
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
        { await owners.RequireInstanceAsync(instanceId, ct); return (await events.ActivityAsync(new(instanceId, LocalUserProfile.Id), ct)).Select(d => new { d.EventId, d.Subscription.AutomationId, d.Subscription.TriggerRevision, status = d.Status.ToString(), d.Code, decision = d.Decision?.Status }); }));
        group.MapGet("/presets", (Guid instanceId, AutomationPresetCatalog presets, CancellationToken ct) => Respond(async () =>
            (await presets.OptionsAsync(instanceId, ct)).Select(p => new AutomationPresetResponse(p.Template.PresetId, p.Template.PresetVersion, p.Template.Name, p.Template.Description, p.Template.Instructions,
                p.Template.TriggerKind == "coreEvent" ? new("coreEvent", CoreEventKey: p.Template.CoreEventKey, FilterExpression: p.Template.FilterExpression, Dispatch: new("coalesceLatest", 900))
                    : new("schedule", new("weekly", "UTC", LocalTime: "09:00", Weekdays: [1])), p.Eligible, p.Prerequisites)).ToArray()));
        group.MapGet("", (Guid instanceId, ExperienceService instances, ITriggerStore store, IAgentRunStore runs,
            IAgentDefinitionStore definitions, IModelCatalog catalog, IMemoryStore memory, CancellationToken ct) => Respond(async () =>
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
                items.Add(Project(registration, effective, last));
            }
            var schedulesAllowed = definition is not null && OccurrenceCompatibility.Allows(definition, TriggerSourceKind.Schedule);
            return new AutomationReview(items,
                definition?.TriggerPolicy is { } p ? new AutomationPolicy(schedulesAllowed && p.AllowOneShot, schedulesAllowed && p.AllowDaily, schedulesAllowed && p.AllowWeekly, schedulesAllowed && p.AllowFixedInterval,
                    p.AllowIndefiniteRecurrence, p.OneShotHorizonDays, p.MinRecurrenceDays, p.MinFixedIntervalSeconds, p.MaxActiveRegistrations, p.Enabled && p.AllowedSourceKinds.Contains("applicationEvent", StringComparer.Ordinal), p.Enabled && p.AllowedSourceKinds.Contains("coreEvent", StringComparer.Ordinal)) : null);
        }));
        group.MapPost("", (Guid instanceId, AutomationRequest request, AdminAutomationAuthoringService service, CancellationToken ct) => Respond(async () =>
            Project(await service.SaveAsync(instanceId, null, request.ExpectedRevision, request.Enabled, request.Name, request.Instructions, ParseTrigger(request.Trigger), request.ModelKey, request.ReasoningEffort, ct, executionTarget: ParseTarget(request.ExecutionTarget), completionDelivery: ParseDelivery(request.CompletionDelivery), requiresTools: request.RequiresTools, requiresVision: request.RequiresVision, presetId: request.PresetId, presetVersion: request.PresetVersion))));
        group.MapPut("/{automationId:guid}", (Guid instanceId, Guid automationId, AutomationRequest request, AdminAutomationAuthoringService service, CancellationToken ct) => Respond(async () =>
            Project(await service.SaveAsync(instanceId, automationId, request.ExpectedRevision, request.Enabled, request.Name, request.Instructions, ParseTrigger(request.Trigger), request.ModelKey, request.ReasoningEffort, ct, executionTarget: ParseTarget(request.ExecutionTarget), completionDelivery: ParseDelivery(request.CompletionDelivery), requiresTools: request.RequiresTools, requiresVision: request.RequiresVision, presetId: request.PresetId, presetVersion: request.PresetVersion))));
        group.MapDelete("/{automationId:guid}", (Guid instanceId, Guid automationId, [Microsoft.AspNetCore.Mvc.FromBody] ContinuityRevisionRequest request, AdminAutomationAuthoringService service, CancellationToken ct) => Respond(async () =>
        { await service.DeleteAsync(instanceId, automationId, request.ExpectedRevision, ct); return new { cancelled = true }; }));
        group.MapPost("/{automationId:guid}/run", (Guid instanceId, Guid automationId, ContinuityRevisionRequest request, AdminAutomationAuthoringService service, CancellationToken ct) => Respond(async () =>
        { var occurrence = await service.RunNowAsync(instanceId, automationId, request.ExpectedRevision, ct); return new { occurrenceId = occurrence.OccurrenceId }; }));
    }
    private static string ExampleData(string key, Guid instanceId) => JsonSerializer.Serialize(key switch
    {
        "run.completed" => (object)new { agentRunId = Guid.Parse("00000000-0000-4000-8000-000000000001"), sessionId = Guid.Parse("00000000-0000-4000-8000-000000000002"), activationKind = "UserTurn", outcomeKind = "Response" },
        "run.failed" => new { agentRunId = Guid.Parse("00000000-0000-4000-8000-000000000001"), sessionId = Guid.Parse("00000000-0000-4000-8000-000000000002"), activationKind = "UserTurn", failureCode = "provider-unavailable" },
        "session.completed" => new { sessionId = Guid.Parse("00000000-0000-4000-8000-000000000002"), previousLifecycle = "Active", lifecycle = "Completed" },
        "session.ended" => new { sessionId = Guid.Parse("00000000-0000-4000-8000-000000000002"), previousLifecycle = "Active", lifecycle = "Ended" },
        "instance.config_changed" => new { agentInstanceId = instanceId, revision = 2, changedSections = new[] { "persona" }, actor = "guardedWrite" },
        _ => new { agentInstanceId = instanceId, definitionId = "secretary", previousVersion = 8, activeVersion = 9 }
    });
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
    private static AutomationTrigger ParseTrigger(AutomationTriggerDto? trigger)
    {
        if (trigger is null) throw AgentCoreErrors.Validation("Trigger is required.");
        return trigger.Kind switch
        {
            "schedule" when trigger.EventId is null && trigger.CoreEventKey is null && trigger.FilterExpression is null && trigger.Dispatch is null => new ScheduleTrigger(Parse(trigger.Schedule)),
            "event" when trigger.Schedule is null && trigger.CoreEventKey is null && Guid.TryParse(trigger.EventId, out var source) => new EventTrigger(source, trigger.FilterExpression, Dispatch(trigger.Dispatch)),
            "coreEvent" when trigger.Schedule is null && trigger.EventId is null && trigger.CoreEventKey is not null => new CoreEventTrigger(trigger.CoreEventKey, trigger.FilterExpression, Dispatch(trigger.Dispatch)),
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
        new(r.AutomationId.ToString("D"), r.Revision, r.Name, r.Instructions, r.Status == AutomationStatus.Active, r.Status.ToString(), Trigger(r.Trigger),
            r.Provenance.AuthorizationOrigin.ToString(), r.Provenance.SourceSessionId?.ToString("D"), r.Provenance.SourceEventId?.ToString("D"),
            HttpMapping.Format(r.Provenance.CreatedAt), r.NextOccurrenceAtUtc is { } next ? HttpMapping.Format(next) : null,
            r.ModelOverrideCatalogKey, r.ModelOverrideReasoningEffort, effective, run?.AgentRunId.ToString("D"), run?.Status.ToString(), run?.Result is { } result ? result.OutcomeKind.ToString() : null,
            new(r.ExecutionTarget.SessionId is null ? "backgroundSession" : "existingSession", r.ExecutionTarget.SessionId?.ToString("D")),
            new(r.CompletionDelivery.SessionId is null ? "none" : "toSession", r.CompletionDelivery.SessionId?.ToString("D")), r.SuspensionReason, r.RequiresTools, r.RequiresVision, r.Provenance.PresetId, r.Provenance.PresetVersion);
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
