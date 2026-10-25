using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Triggers;

public static partial class TriggerScheduleCommands
{
    // Parsing only: this adapter neither loads legacy owners nor authorizes a write.
    public static TenantAutomationChange NormalizeTenant(AgentDefinition definition, Automation? current, TenantAutomationMutationArguments input,
        Guid session, DateTimeOffset referenceTime, IIdGenerator ids)
    {
        using var document = JsonDocument.Parse(input.Json); var args = document.RootElement;
        if (session == Guid.Empty || (current?.AutomationId) != input.AutomationId || (current?.Revision ?? 0) != input.ExpectedRevision)
            throw new SecurityConflictException("automation_source_changed");
        var policy = definition.TriggerPolicy;
        var preset = current is null ? TenantAutomationPresetRules.Template(Text("presetId"), Number("presetVersion")) : null;
        if (current is not null && (args.TryGetProperty("presetId", out _) || args.TryGetProperty("presetVersion", out _)))
            if (Text("presetId") != current.Provenance.PresetId || Number("presetVersion") != current.Provenance.PresetVersion) throw new SecurityConflictException("automation_provenance_changed");
        string? Text(string key) => args.TryGetProperty(key, out var value) ? value.ValueKind == JsonValueKind.Null ? null : value.GetString() : null;
        int? Number(string key) => args.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetInt32() : null;
        string? Setting(string key, string? prior) => args.TryGetProperty(key, out _) ? Text(key) : prior;
        var instructions = args.TryGetProperty("instructions", out _) ? RequireInstructions(args) : current?.Instructions ?? preset?.Instructions ?? throw new ArgumentException("Instructions are required.");
        var name = args.TryGetProperty("name", out _) ? RequireString(args, "name") : current?.Name ?? preset?.Name ?? instructions[..Math.Min(80, instructions.Length)];
        IReadOnlyList<TenantAutomationTriggerChange>? events = null; TriggerSchedule? schedule = null;
        if (args.TryGetProperty("triggers", out var collection))
        {
            var rows = ReadTriggerCollection(args, ids);
            if (rows.Count == 1 && rows[0].Configuration is ScheduleTrigger timing)
            {
                if (!rows[0].Enabled || collection[0].TryGetProperty("triggerId", out _) && (current?.IsSchedule != true || current.Triggers[0].TriggerId != rows[0].TriggerId || !collection[0].TryGetProperty("revision", out _) || current.Triggers[0].Revision != rows[0].Revision))
                    throw new SecurityConflictException("schedule_subscription_changed");
                schedule = timing.Schedule;
            }
            else
            {
                events = rows.Select((row, index) => {
                    var supplied = collection[index].TryGetProperty("triggerId", out _);
                    if (supplied && current?.Triggers.All(t => t.TriggerId != row.TriggerId) != false) throw new SecurityConflictException("event_subscription_changed");
                    if (supplied && !collection[index].TryGetProperty("revision", out _)) throw new ArgumentException("Use the inspected subscription revision.");
                    return new TenantAutomationTriggerChange(supplied ? row.TriggerId : null, row.Configuration, row.Enabled, supplied ? row.Revision : 0);
                }).ToArray();
            }
        }
        else if (args.TryGetProperty("coreEventKey", out _) || args.TryGetProperty("eventId", out _))
        {
            if (current is { IsSchedule: false, Triggers.Count: > 1 }) throw new ArgumentException("Use the exact trigger collection when editing multiple subscriptions.");
            AutomationTrigger selected = args.TryGetProperty("coreEventKey", out var key)
                ? new CoreEventTrigger(key.GetString()!, Setting("filterExpression", null), ReadDispatch(args, null))
                : new EventTrigger(Guid.Parse(args.GetProperty("eventId").GetString()!), Setting("filterExpression", null), ReadDispatch(args, null));
            var old = current?.Triggers.SingleOrDefault(t => t.Source == new AutomationTriggerRecord(ids.NewId(), selected).Source);
            if (old?.Configuration is FilteredEventTrigger prior)
            {
                var filter = Setting("filterExpression", prior.FilterExpression); var dispatch = ReadDispatch(args, prior.Dispatch);
                selected = selected is CoreEventTrigger c ? new CoreEventTrigger(c.CoreEventKey, filter, dispatch) : new EventTrigger(((EventTrigger)selected).EventId, filter, dispatch);
            }
            events = [new(old?.TriggerId, selected, old?.Enabled ?? true, old?.Revision ?? 0)];
        }
        else if (HasScheduleFields(args) || args.TryGetProperty("timeZone", out _))
        {
            var scheduling = policy ?? throw new AccessDeniedException("scheduling_unavailable");
            var merged = current?.IsSchedule == true ? ScheduleArguments(current.Schedule) : new JsonObject();
            foreach (var field in args.EnumerateObject()) merged[field.Name] = JsonNode.Parse(field.Value.GetRawText());
            // An explicit new one-shot form replaces rather than combines with the saved absolute form.
            if (HasOneShotFields(args))
            {
                foreach (var field in new[] { "relativeDelaySeconds", "relativeDayOffset", "localDate", "atUtc" })
                    if (!args.TryGetProperty(field, out _)) merged.Remove(field);
                merged["kind"] = "once";
            }
            using var normalized = JsonDocument.Parse(merged.ToJsonString()); var timing = normalized.RootElement;
            schedule = current?.ScheduleOrNull() is FixedIntervalSchedule fixedCurrent && !IsExplicitCalendarKindChange(args)
                ? ApplyFixedIntervalUpdate(fixedCurrent, timing, referenceTime, scheduling).Schedule
                : TryString(timing, "kind", out var kind) && kind is "daily" or "weekly" or "fixed_interval" or "fixedInterval"
                    ? ResolveRecurring(timing, referenceTime, scheduling) : ResolveOneShot(timing, referenceTime, scheduling).Schedule;
        }
        else if (current is not null)
        {
            schedule = current.ScheduleOrNull();
            events = current.IsSchedule ? null : current.Triggers.Select(t => new TenantAutomationTriggerChange(t.TriggerId, t.Configuration, t.Enabled, t.Revision)).ToArray();
            if (args.TryGetProperty("filterExpression", out _) || args.TryGetProperty("dispatchMode", out _) || args.TryGetProperty("windowSeconds", out _))
            {
                if (events?.Count != 1 || events[0].Configuration is not FilteredEventTrigger e) throw new ArgumentException("Use an exact trigger collection to edit Event subscriptions.");
                var filter = Setting("filterExpression", e.FilterExpression); var dispatch = ReadDispatch(args, e.Dispatch);
                events = [events[0] with { Configuration = e is CoreEventTrigger c ? new CoreEventTrigger(c.CoreEventKey, filter, dispatch) : new EventTrigger(((EventTrigger)e).EventId, filter, dispatch) }];
            }
        }
        else if (preset is not null)
        {
            if (preset.TriggerKind == "schedule") schedule = new WeeklySchedule(1, [DayOfWeek.Monday], new TimeOnly(9, 0), "UTC");
            else events = [new(null, new CoreEventTrigger(preset.CoreEventKey!, preset.FilterExpression, preset.Dispatch))];
        }
        else throw new ArgumentException("Select one explicit Schedule or typed Event collection.");
        var enabled = input.Tool == ToolCatalog.AutomationEnable || input.Tool != ToolCatalog.AutomationDisable &&
            (args.TryGetProperty("enabled", out var enable) ? enable.GetBoolean() : current?.Status == AutomationStatus.Active || current is null && preset is null && schedule is not null);
        var target = ChatTarget(args, session, current); var delivery = ChatDelivery(args, session, current);
        return new(enabled, name, instructions, schedule, Setting("offeringId", current?.ModelOverrideOfferingId), Setting("reasoningEffort", current?.ModelOverrideReasoningEffort),
            OptionalRequirement(args, "requiresTools") ?? current?.RequiresTools ?? true, OptionalRequirement(args, "requiresVision") ?? current?.RequiresVision ?? false,
            events, delivery.SessionId, target.SessionId, preset?.PresetId, preset?.PresetVersion);
    }
    private static TriggerSchedule? ScheduleOrNull(this Automation value) => value.IsSchedule ? value.Schedule : null;
    private static JsonObject ScheduleArguments(TriggerSchedule schedule)
    {
        var result = new JsonObject();
        switch (schedule)
        {
            case OneShotSchedule t: result["kind"] = "once"; result["atUtc"] = t.AtUtc.ToString("O"); result["timeZone"] = t.TimeZoneId; break;
            case FixedIntervalSchedule t: result["kind"] = "fixed_interval"; result["intervalSeconds"] = t.IntervalSeconds; result["endAtUtc"] = t.EndAtUtc?.ToString("O"); result["maxOccurrences"] = t.MaxOccurrences; break;
            case DailySchedule t: result["kind"] = "daily"; result["interval"] = t.IntervalDays; result["localTime"] = t.LocalTime.ToString("HH:mm"); result["timeZone"] = t.TimeZoneId; result["startDate"] = t.StartDate?.ToString("yyyy-MM-dd"); result["endDate"] = t.EndDate?.ToString("yyyy-MM-dd"); result["maxOccurrences"] = t.MaxOccurrences; break;
            case WeeklySchedule t: result["kind"] = "weekly"; result["interval"] = t.IntervalWeeks; result["localTime"] = t.LocalTime.ToString("HH:mm"); result["timeZone"] = t.TimeZoneId; result["startDate"] = t.StartDate?.ToString("yyyy-MM-dd"); result["endDate"] = t.EndDate?.ToString("yyyy-MM-dd"); result["maxOccurrences"] = t.MaxOccurrences; result["weekdays"] = new JsonArray(t.Weekdays.Select(d => (JsonNode)JsonValue.Create(d.ToString())!).ToArray()); break;
        }
        return result;
    }
}
