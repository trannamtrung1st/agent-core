using System.Globalization;
using AgentCore.Application.Events;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Events;
using AgentCore.Domain.Triggers;

namespace AgentCore.Api.Mapping;

internal static class AutomationHttpMapping
{
    internal static AutomationTrigger ParseTrigger(AutomationTriggerDto? trigger)
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
    internal static AutomationTriggerDto TriggerRecord(AutomationTriggerRecord t) => (t.Configuration switch
    {
        ScheduleTrigger s => new AutomationTriggerDto("schedule", Timing(s.Schedule)),
        FilteredEventTrigger e => new AutomationTriggerDto("event", FilterExpression: e.FilterExpression, Dispatch: DispatchDto(e.Dispatch),
            Source: new(t.Source!.Kind, t.Source.Key, t.Source.EventId?.ToString("D"))),
        _ => throw AgentCoreErrors.Validation("Trigger is unavailable.")
    }) with { TriggerId = t.TriggerId.ToString("D"), Enabled = t.Enabled, Revision = t.Revision };
    internal static TriggerSchedule Parse(AutomationTiming? t)
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
    private static AutomationTiming Timing(TriggerSchedule s) => s switch
    {
        OneShotSchedule t => new("oneShot", t.TimeZoneId, HttpMapping.Format(t.AtUtc)),
        FixedIntervalSchedule t => new("fixedInterval", Interval: t.IntervalSeconds, AnchorAtUtc: HttpMapping.Format(t.AnchorAtUtc), EndAtUtc: t.EndAtUtc is { } e ? HttpMapping.Format(e) : null, MaxOccurrences: t.MaxOccurrences),
        DailySchedule t => new("daily", t.TimeZoneId, Interval: t.IntervalDays, LocalTime: t.LocalTime.ToString("HH:mm"), StartDate: t.StartDate?.ToString("yyyy-MM-dd"), EndDate: t.EndDate?.ToString("yyyy-MM-dd"), MaxOccurrences: t.MaxOccurrences),
        WeeklySchedule t => new("weekly", t.TimeZoneId, Interval: t.IntervalWeeks, LocalTime: t.LocalTime.ToString("HH:mm"), Weekdays: t.Weekdays.Select(d => (int)d).ToArray(), StartDate: t.StartDate?.ToString("yyyy-MM-dd"), EndDate: t.EndDate?.ToString("yyyy-MM-dd"), MaxOccurrences: t.MaxOccurrences),
        _ => throw AgentCoreErrors.Validation("Schedule kind is unavailable.")
    };
}
