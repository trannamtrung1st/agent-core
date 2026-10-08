using System.Globalization;
using AgentCore.Api.Http;
using AgentCore.Api.Mapping;
using AgentCore.Application.Experience;
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
        group.MapGet("", (Guid instanceId, ExperienceService instances, ITriggerStore store, IAgentRunStore runs,
            IAgentDefinitionStore definitions, IModelCatalog catalog, CancellationToken ct) => Respond(async () =>
        {
            var instance = await instances.RequireInstanceAsync(instanceId, ct);
            var definition = await definitions.GetAsync(instance.DefinitionId, instance.ActiveVersion, ct);
            var rows = await store.ListAsync(new(instanceId, LocalUserProfile.Id), null, ct);
            var items = new List<AutomationResponse>();
            foreach (var registration in rows.Where(r => r.Status != AutomationStatus.Cancelled))
            {
                var last = await runs.GetLatestForAutomationAsync(new(instanceId, LocalUserProfile.Id), registration.AutomationId, ct);
                items.Add(Project(registration, definition is null ? null : ExecutionModelPolicy.Resolve(catalog, definition, instance, registration).Pin?.CatalogKey, last));
            }
            return new AutomationReview(items,
                definition?.TriggerPolicy is { } p ? new AutomationPolicy(p.AllowOneShot, p.AllowDaily, p.AllowWeekly, p.AllowFixedInterval,
                    p.AllowIndefiniteRecurrence, p.OneShotHorizonDays, p.MinRecurrenceDays, p.MinFixedIntervalSeconds, p.MaxActiveRegistrations) : null);
        }));
        group.MapPost("", (Guid instanceId, AutomationRequest request, AdminAutomationAuthoringService service, CancellationToken ct) => Respond(async () =>
            Project(await service.SaveAsync(instanceId, null, request.ExpectedRevision, request.Enabled, request.Name, request.Instructions, ParseTrigger(request.Trigger), request.ModelKey, request.ReasoningEffort, ct))));
        group.MapPut("/{automationId:guid}", (Guid instanceId, Guid automationId, AutomationRequest request, AdminAutomationAuthoringService service, CancellationToken ct) => Respond(async () =>
            Project(await service.SaveAsync(instanceId, automationId, request.ExpectedRevision, request.Enabled, request.Name, request.Instructions, ParseTrigger(request.Trigger), request.ModelKey, request.ReasoningEffort, ct))));
        group.MapDelete("/{automationId:guid}", (Guid instanceId, Guid automationId, [Microsoft.AspNetCore.Mvc.FromBody] ContinuityRevisionRequest request, AdminAutomationAuthoringService service, CancellationToken ct) => Respond(async () =>
        { await service.DeleteAsync(instanceId, automationId, request.ExpectedRevision, ct); return new { cancelled = true }; }));
        group.MapPost("/{automationId:guid}/run", (Guid instanceId, Guid automationId, ContinuityRevisionRequest request, AdminAutomationAuthoringService service, CancellationToken ct) => Respond(async () =>
        { var occurrence = await service.RunNowAsync(instanceId, automationId, request.ExpectedRevision, ct); return new { occurrenceId = occurrence.OccurrenceId }; }));
    }
    private static AutomationTrigger ParseTrigger(AutomationTriggerDto? trigger)
    {
        if (trigger is null) throw AgentCoreErrors.Validation("Trigger is required.");
        return trigger.Kind switch
        {
            "schedule" when trigger.EventSourceId is null && trigger.EventType is null => new ScheduleTrigger(Parse(trigger.Schedule)),
            "event" when trigger.Schedule is null && Guid.TryParse(trigger.EventSourceId, out var source) => new EventTrigger(source, trigger.EventType ?? ""),
            _ => throw AgentCoreErrors.Validation("Trigger must contain either Schedule timing or Event Source and type.")
        };
    }
    private static AutomationTriggerDto Trigger(AutomationTrigger trigger) => trigger switch
    {
        ScheduleTrigger s => new("schedule", Timing(s.Schedule)),
        EventTrigger e => new("event", EventSourceId: e.EventSourceId.ToString("D"), EventType: e.EventType),
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
            r.ModelOverrideCatalogKey, r.ModelOverrideReasoningEffort, effective, run?.AgentRunId.ToString("D"), run?.Status.ToString(), run?.Result is { } result ? result.OutcomeKind.ToString() : null);
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
