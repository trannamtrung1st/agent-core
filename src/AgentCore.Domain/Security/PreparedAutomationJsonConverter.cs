using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCore.Domain.Triggers;

namespace AgentCore.Domain.Security;

// Closed consent serialization only. Ordinary Automation/trigger persistence and
// public HTTP contracts keep their incumbent mappings and historical JSON.
public sealed class PreparedAutomationJsonConverter : JsonConverter<Automation>
{
    private sealed record Timing(TriggerScheduleKind Kind, string TimeZoneId, DateTimeOffset? AtUtc = null, int Interval = 1,
        TimeOnly? LocalTime = null, IReadOnlyList<DayOfWeek>? Weekdays = null, DateOnly? StartDate = null, DateOnly? EndDate = null,
        int? MaxOccurrences = null, DateTimeOffset? AnchorAtUtc = null, DateTimeOffset? EndAtUtc = null);
    private sealed record Saved(Guid AutomationId, TriggerOwner Owner, AutomationStatus Status, string Name, string Instructions,
        Guid TriggerId, bool TriggerEnabled, long ChildRevision, Timing Timing, DateTimeOffset? NextOccurrenceAtUtc, DateTimeOffset? ExpiresAtUtc,
        int OccurrenceCount, long Revision, long TriggerRevision, TriggerProvenance Provenance, string? SuspensionReason,
        string? ModelOverrideCatalogKey, string? ModelOverrideReasoningEffort, bool RequiresTools, bool RequiresVision);

    public override Automation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var p = JsonSerializer.Deserialize<Saved>(ref reader, options) ?? throw new JsonException("Automation consent source is missing.");
        var t = p.Timing;
        TriggerSchedule schedule = t.Kind switch {
            TriggerScheduleKind.OneShot when t.AtUtc is { } at => new OneShotSchedule(at, t.TimeZoneId),
            TriggerScheduleKind.Daily when t.LocalTime is { } local => new DailySchedule(t.Interval, local, t.TimeZoneId, t.StartDate, t.EndDate, t.MaxOccurrences),
            TriggerScheduleKind.Weekly when t.LocalTime is { } local && t.Weekdays is { } days => new WeeklySchedule(t.Interval, days, local, t.TimeZoneId, t.StartDate, t.EndDate, t.MaxOccurrences),
            TriggerScheduleKind.FixedInterval when t.AnchorAtUtc is { } anchor => new FixedIntervalSchedule(t.Interval, anchor, t.EndAtUtc, t.MaxOccurrences),
            _ => throw new JsonException("An exact supported consent schedule is required.") };
        var value = new Automation(p.AutomationId, p.Owner, p.Status, p.Instructions,
            new[] { new AutomationTriggerRecord(p.TriggerId, new ScheduleTrigger(schedule), p.TriggerEnabled, p.ChildRevision) },
            p.NextOccurrenceAtUtc, p.ExpiresAtUtc, p.OccurrenceCount, p.Revision, p.TriggerRevision, p.Provenance, p.SuspensionReason,
            p.ModelOverrideCatalogKey, p.ModelOverrideReasoningEffort, p.RequiresVision, p.Name, AutomationExecutionTarget.Background,
            AutomationCompletionDelivery.None, p.RequiresTools);
        if (value.TriggerRevision != p.TriggerRevision) throw new JsonException("Automation consent trigger revision differs.");
        return value;
    }

    public override void Write(Utf8JsonWriter writer, Automation value, JsonSerializerOptions options)
    {
        if (value.Triggers.Count != 1 || value.Triggers[0].Configuration is not ScheduleTrigger source
            || value.ExecutionTarget != AutomationExecutionTarget.Background || value.CompletionDelivery != AutomationCompletionDelivery.None)
            throw new JsonException("Only the exact converted Schedule destination may be prepared.");
        var t = source.Schedule;
        var timing = t switch {
            OneShotSchedule s => new Timing(t.Kind, s.TimeZoneId, s.AtUtc),
            DailySchedule s => new Timing(t.Kind, s.TimeZoneId, Interval: s.IntervalDays, LocalTime: s.LocalTime, StartDate: s.StartDate, EndDate: s.EndDate, MaxOccurrences: s.MaxOccurrences),
            WeeklySchedule s => new Timing(t.Kind, s.TimeZoneId, Interval: s.IntervalWeeks, LocalTime: s.LocalTime, Weekdays: s.Weekdays, StartDate: s.StartDate, EndDate: s.EndDate, MaxOccurrences: s.MaxOccurrences),
            FixedIntervalSchedule s => new Timing(t.Kind, "UTC", Interval: s.IntervalSeconds, MaxOccurrences: s.MaxOccurrences, AnchorAtUtc: s.AnchorAtUtc, EndAtUtc: s.EndAtUtc),
            _ => throw new JsonException("Unsupported consent schedule.") };
        var child = value.Triggers[0];
        JsonSerializer.Serialize(writer, new Saved(value.AutomationId, value.Owner, value.Status, value.Name, value.Instructions,
            child.TriggerId, child.Enabled, child.Revision, timing, value.NextOccurrenceAtUtc, value.ExpiresAtUtc, value.OccurrenceCount,
            value.Revision, value.TriggerRevision, value.Provenance, value.SuspensionReason, value.ModelOverrideCatalogKey,
            value.ModelOverrideReasoningEffort, value.RequiresTools, value.RequiresVision), options);
    }
}
