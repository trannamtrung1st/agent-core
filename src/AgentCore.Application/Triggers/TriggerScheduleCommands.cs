using AgentCore.Domain.Events;
using System.Globalization;
using System.Text.Json;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Triggers;

[Flags]
public enum TriggerCommandAction
{
    None = 0,
    Create = 1,
    List = 2,
    Update = 4,
    Cancel = 8,
    Enable = 16,
    Disable = 32,
    Run = 64
}

public static class TriggerAuthorization
{

    public static TriggerCommandAction ActionForTool(string toolName) => toolName switch
    {
        ToolCatalog.AutomationEnable => TriggerCommandAction.Enable,
        ToolCatalog.AutomationDisable => TriggerCommandAction.Disable,
        ToolCatalog.AutomationList => TriggerCommandAction.List,
        ToolCatalog.AutomationUpdate => TriggerCommandAction.Update,
        ToolCatalog.AutomationDelete => TriggerCommandAction.Cancel,
        ToolCatalog.AutomationRun => TriggerCommandAction.Run,
        ToolCatalog.AutomationCreate => TriggerCommandAction.Create,
        _ => TriggerCommandAction.None
    };

}

// Pure normalization shared by canonical tenant authoring; no local owner or execution facade.
public static partial class TriggerScheduleCommands
{

    private static IReadOnlyList<AutomationTriggerRecord> ReadTriggerCollection(JsonElement args, IIdGenerator? ids = null)
    {
        var rows = args.GetProperty("triggers");
        if (rows.ValueKind != JsonValueKind.Array) throw new ArgumentException("Triggers must be a collection.");
        return rows.EnumerateArray().Select(t =>
        {
            AutomationTrigger config;
            if (t.TryGetProperty("kind", out var kind) && kind.GetString() == "schedule")
            {
                if (t.TryGetProperty("source", out _) || t.TryGetProperty("filterExpression", out _) || t.TryGetProperty("dispatch", out _)) throw new ArgumentException("A Schedule has timing only.");
                var timing = t.GetProperty("schedule");
                string? Text(string key) => timing.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
                int? Number(string key) => timing.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetInt32() : null;
                DateOnly? Date(string key) => Text(key) is { } date ? DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
                var zone = Text("timeZone") ?? "UTC";
                var interval = Number("interval") ?? 1;
                var limit = Number("maxOccurrences");
                config = new ScheduleTrigger(Text("kind") switch
                {
                    "oneShot" => new OneShotSchedule(DateTimeOffset.Parse(Text("atUtc") ?? "", CultureInfo.InvariantCulture).ToUniversalTime(), zone),
                    "fixedInterval" => new FixedIntervalSchedule(interval, DateTimeOffset.Parse(Text("anchorAtUtc") ?? "", CultureInfo.InvariantCulture).ToUniversalTime(), Text("endAtUtc") is { } end ? DateTimeOffset.Parse(end, CultureInfo.InvariantCulture).ToUniversalTime() : null, limit),
                    "daily" => new DailySchedule(interval, TimeOnly.ParseExact(Text("localTime") ?? "", "HH:mm", CultureInfo.InvariantCulture), zone, Date("startDate"), Date("endDate"), limit),
                    "weekly" => new WeeklySchedule(interval, timing.GetProperty("weekdays").EnumerateArray().Select(d => (DayOfWeek)d.GetInt32()).ToArray(), TimeOnly.ParseExact(Text("localTime") ?? "", "HH:mm", CultureInfo.InvariantCulture), zone, Date("startDate"), Date("endDate"), limit),
                    _ => throw new ArgumentException("Schedule kind is invalid.")
                });
            }
            else
            {
                if (kind.ValueKind != JsonValueKind.String || kind.GetString() != "event" || t.TryGetProperty("schedule", out _)) throw new ArgumentException("An Event requires a typed source.");
            var s = t.GetProperty("source");
            var source = EventSourceReference.Parse(s.GetProperty("kind").GetString() ?? "", s.TryGetProperty("key", out var k) ? k.GetString() : null,
                s.TryGetProperty("eventId", out var e) ? Guid.Parse(e.GetString() ?? "") : null);
            var filter = t.TryGetProperty("filterExpression", out var f) ? f.GetString() : null;
            var dispatch = t.TryGetProperty("dispatch", out var d) ? d.GetProperty("mode").GetString() switch
            { "everyMatch" => new EventDispatch(), "coalesceLatest" => new EventDispatch(EventDispatchMode.CoalesceLatest, d.GetProperty("windowSeconds").GetInt32()), _ => throw new ArgumentException("Dispatch is invalid.") } : new EventDispatch();
                config = source.Kind == "builtin" ? new CoreEventTrigger(source.Key!, filter, dispatch) : new EventTrigger(source.EventId!.Value, filter, dispatch);
            }
            return new AutomationTriggerRecord(t.TryGetProperty("triggerId", out var id) ? Guid.Parse(id.GetString() ?? "") : ids?.NewId() ?? Guid.NewGuid(), config,
                !t.TryGetProperty("enabled", out var enabled) || enabled.GetBoolean(), t.TryGetProperty("revision", out var rev) ? rev.GetInt64() : 1);
        }).ToArray();
    }

    private static EventDispatch ReadDispatch(JsonElement args, EventDispatch? current) => args.TryGetProperty("dispatchMode", out var mode)
        ? mode.GetString() switch { "everyMatch" => new(), "coalesceLatest" => new(EventDispatchMode.CoalesceLatest, args.GetProperty("windowSeconds").GetInt32()), _ => throw new ArgumentException("Dispatch mode is invalid.") } : current ?? new();

    private static (TriggerSchedule Schedule, DateTimeOffset Next) ResolveOneShot(
        JsonElement arguments,
        DateTimeOffset referenceTime,
        TriggerPolicy policy)
    {
        var now = TriggerScheduleCalculator.Truncate(referenceTime);
        var hasOffset = TryPresent(arguments, "relativeDayOffset", out var offsetElement);
        var hasDate = TryTimeString(arguments, "localDate", out var localDateText);
        var hasAt = TryTimeString(arguments, "atUtc", out var atText);
        var hasDelay = TryPresent(arguments, "relativeDelaySeconds", out var delayElement);
        if (hasDelay
            && hasOffset
            && !hasDate
            && !hasAt
            && TryWholeNumber(offsetElement, out var unusedOffset)
            && unusedOffset == 0
            && !TryTimeString(arguments, "localTime", out _))
        {
            hasOffset = false;
        }

        var forms = (hasOffset ? 1 : 0) + (hasDate ? 1 : 0) + (hasAt ? 1 : 0) + (hasDelay ? 1 : 0);
        if (forms != 1)
        {
            var present = new List<string>(4);
            if (hasDelay)
            {
                present.Add("relativeDelaySeconds");
            }

            if (hasOffset)
            {
                present.Add("relativeDayOffset");
            }

            if (hasDate)
            {
                present.Add("localDate");
            }

            if (hasAt)
            {
                present.Add("atUtc");
            }

            throw new ArgumentException(
                "Schedule time is missing or ambiguous. Present: "
                + (present.Count == 0 ? "none" : string.Join(',', present))
                + ". Use exactly one of relativeDelaySeconds, relativeDayOffset with localTime, localDate with localTime, or atUtc in a complete structured proposal. Ask only for missing or ambiguous timing details; retain the user's clear intent. Do not ask for a bare yes/no confirmation.");
        }

        DateTimeOffset instant;
        DateOnly? localDate = null;
        TimeOnly? localTime = null;
        string zone;
        if (hasOffset)
        {
            zone = RequireTimeZone(arguments);
            if (!TryWholeNumber(offsetElement, out var offset))
            {
                throw new ArgumentException("relativeDayOffset must be a whole number of days.");
            }

            if (offset < 0 || offset > policy.OneShotHorizonDays)
            {
                throw new ArgumentException("One-shot day offset is outside the scheduling horizon.");
            }

            localTime = RequireLocalTime(arguments);
            var createdLocal = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TriggerScheduleCalculator.RequireZone(zone)).DateTime);
            localDate = createdLocal.AddDays(offset);
            instant = TriggerScheduleCalculator.ResolveWallClock(zone, localDate.Value, localTime.Value);
        }
        else if (hasDate)
        {
            zone = RequireTimeZone(arguments);
            localDate = ParseDate(localDateText, "localDate");
            localTime = RequireLocalTime(arguments);
            instant = TriggerScheduleCalculator.ResolveWallClock(zone, localDate.Value, localTime.Value);
        }
        else if (hasDelay)
        {
            if (!TryWholeNumber(delayElement, out var seconds))
            {
                throw new ArgumentException("relativeDelaySeconds must be a whole number of seconds.");
            }

            var maxSeconds = policy.OneShotHorizonDays * 86_400;
            if (seconds < 1 || seconds > maxSeconds)
            {
                throw new ArgumentException("relativeDelaySeconds is outside the scheduling horizon.");
            }

            instant = now.AddSeconds(seconds);
            zone = OptionalTimeZoneForDisplay(arguments);
            var zoned = TimeZoneInfo.ConvertTime(instant, TriggerScheduleCalculator.RequireZone(zone));
            localDate = DateOnly.FromDateTime(zoned.DateTime);
            localTime = TimeOnly.FromDateTime(zoned.DateTime);
        }
        else
        {
            instant = ParseUtc(atText, "atUtc");
            zone = OptionalTimeZoneForDisplay(arguments);
        }

        instant = TriggerScheduleCalculator.Truncate(instant);
        var schedule = new OneShotSchedule(instant, zone, localDate, localTime);
        ScheduleDefinitionPolicy.Validate(schedule, policy, now);
        return (schedule, instant);
    }

    private static TriggerSchedule ResolveRecurring(
        JsonElement arguments,
        DateTimeOffset referenceTime,
        TriggerPolicy policy)
    {
        if (arguments.TryGetProperty("intervalSeconds", out _))
        {
            var kindText = TryString(arguments, "kind", out var kindProbe) ? kindProbe : null;
            if (kindText is null
                || (!kindText.Equals("fixed_interval", StringComparison.OrdinalIgnoreCase)
                    && !kindText.Equals("fixedInterval", StringComparison.OrdinalIgnoreCase)))
            {
                throw new TriggerScheduleCommandException(
                    "unsupported_recurrence",
                    "Sub-day fixed intervals require kind fixed_interval with intervalSeconds. Daily and weekly schedules are calendar-based only.");
            }
        }

        var kind = RequireString(arguments, "kind");
        int? cap = arguments.TryGetProperty("maxOccurrences", out var capElement) && capElement.ValueKind != JsonValueKind.Null
            ? capElement.GetInt32()
            : null;

        if (kind.Equals("fixed_interval", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("fixedInterval", StringComparison.OrdinalIgnoreCase))
        {
            if (!arguments.TryGetProperty("intervalSeconds", out var secondsElement)
                || secondsElement.ValueKind != JsonValueKind.Number
                || !secondsElement.TryGetInt32(out var intervalSeconds))
            {
                throw new TriggerScheduleCommandException(
                    "schedule_validation_failed",
                    "Fixed-interval schedules require intervalSeconds.");
            }

            var intent = TryString(arguments, "instructions", out var intentText) ? intentText : string.Empty;
            ScheduleDefinitionPolicy.RequireInterval(policy, intervalSeconds, referenceTime, intent);

            if (intervalSeconds > TriggerLimits.MaxFixedIntervalSeconds)
            {
                throw new TriggerScheduleCommandException(
                    "unsupported_recurrence",
                    "Fixed interval is longer than the supported maximum.");
            }

            var endAt = ResolveOptionalEndAtUtc(arguments);

            var anchor = TriggerScheduleCalculator.Truncate(referenceTime);
            var schedule = new FixedIntervalSchedule(intervalSeconds, anchor, endAt, cap);
            ScheduleDefinitionPolicy.Validate(schedule, policy, referenceTime, intent);
            return schedule;
        }

        var zone = RequireTimeZone(arguments);
        var localTime = RequireLocalTime(arguments);
        var interval = arguments.TryGetProperty("interval", out var intervalElement) && intervalElement.TryGetInt32(out var parsed)
            ? parsed
            : 1;
        var start = OptionalDate(arguments, "startDate");
        var end = OptionalDate(arguments, "endDate");
        if (kind.Equals("daily", StringComparison.OrdinalIgnoreCase))
        {
            var schedule = new DailySchedule(interval, localTime, zone, start, end, cap);
            ScheduleDefinitionPolicy.Validate(schedule, policy, referenceTime);
            return schedule;
        }

        if (kind.Equals("weekly", StringComparison.OrdinalIgnoreCase))
        {
            var weekdays = ReadWeekdays(arguments);
            var schedule = new WeeklySchedule(interval, weekdays, localTime, zone, start, end, cap);
            ScheduleDefinitionPolicy.Validate(schedule, policy, referenceTime);
            return schedule;
        }

        throw new TriggerScheduleCommandException(
            "unsupported_recurrence",
            "Schedule kind must be fixed_interval, daily, or weekly.");
    }

    private static string RequireTimeZone(JsonElement arguments)
    {
        if (TryString(arguments, "timeZone", out var specified) && !string.IsNullOrWhiteSpace(specified))
        {
            return TriggerTimeZoneNormalization.Resolve(specified);
        }

        throw new ArgumentException(
            "Timezone is required. Ask which timezone to use, or pass a common label such as Vietnam time.");
    }

    private static string OptionalTimeZoneForDisplay(JsonElement arguments)
    {
        if (TryString(arguments, "timeZone", out var specified) && !string.IsNullOrWhiteSpace(specified))
        {
            return TriggerTimeZoneNormalization.Resolve(specified);
        }

        return "UTC";
    }

    private static string RequireInstructions(JsonElement arguments) => TriggerText.RequireInstructions(RequireString(arguments, "instructions"));

    private static TimeOnly RequireLocalTime(JsonElement arguments)
    {
        var text = RequireString(arguments, "localTime");
        return TimeOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : throw new ArgumentException("localTime must be a clock time such as 09:00.");
    }

    private static DateOnly? OptionalDate(JsonElement arguments, string name) =>
        TryString(arguments, name, out var text) && !string.IsNullOrWhiteSpace(text)
            ? ParseDate(text, name)
            : null;

    private static DateOnly ParseDate(string text, string name) =>
        DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new ArgumentException($"{name} must be a calendar date.");

    private static DateTimeOffset ParseUtc(string text, string name)
    {
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var instant)
            || instant.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException($"{name} must be a UTC timestamp.");
        }

        return instant;
    }

    private static IReadOnlyList<DayOfWeek> ReadWeekdays(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("weekdays", out var element) || element.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("Weekly schedules require weekdays.");
        }

        var days = new List<DayOfWeek>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String
                || !Enum.TryParse<DayOfWeek>(item.GetString(), ignoreCase: true, out var day))
            {
                throw new ArgumentException("Weekly weekday is not valid.");
            }

            days.Add(day);
        }

        return days;
    }

    private static bool HasScheduleFields(JsonElement arguments) =>
        HasOneShotFields(arguments)
        || HasFixedIntervalScheduleFields(arguments)
        || HasCalendarRecurringFields(arguments);

    private static bool HasFixedIntervalScheduleFields(JsonElement arguments) =>
        arguments.TryGetProperty("intervalSeconds", out _)
        || arguments.TryGetProperty("endAtUtc", out _)
        || IsFixedIntervalKind(arguments);

    private static bool HasCalendarRecurringFields(JsonElement arguments) =>
        arguments.TryGetProperty("kind", out _)
        || arguments.TryGetProperty("localTime", out _)
        || arguments.TryGetProperty("weekdays", out _)
        || arguments.TryGetProperty("interval", out _)
        || arguments.TryGetProperty("startDate", out _)
        || arguments.TryGetProperty("endDate", out _)
        || arguments.TryGetProperty("maxOccurrences", out _);

    private static bool IsFixedIntervalKind(JsonElement arguments) =>
        TryString(arguments, "kind", out var kind)
        && (kind.Equals("fixed_interval", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("fixedInterval", StringComparison.OrdinalIgnoreCase));

    private static bool IsExplicitCalendarKindChange(JsonElement arguments) =>
        TryString(arguments, "kind", out var kind)
        && !kind.Equals("fixed_interval", StringComparison.OrdinalIgnoreCase)
        && !kind.Equals("fixedInterval", StringComparison.OrdinalIgnoreCase);

    private static (TriggerSchedule Schedule, DateTimeOffset? Next) ApplyFixedIntervalUpdate(
        FixedIntervalSchedule current,
        JsonElement arguments,
        DateTimeOffset referenceTime,
        TriggerPolicy policy)
    {
        var intervalSeconds = current.IntervalSeconds;
        if (arguments.TryGetProperty("intervalSeconds", out var secondsElement)
            && secondsElement.ValueKind == JsonValueKind.Number
            && secondsElement.TryGetInt32(out var parsedSeconds))
        {
            intervalSeconds = parsedSeconds;
        }

        ScheduleDefinitionPolicy.RequireInterval(policy, intervalSeconds, referenceTime);

        var endAt = ResolveOptionalEndAtUtc(arguments, current.EndAtUtc);

        int? maxOccurrences = current.MaxOccurrences;
        if (arguments.TryGetProperty("maxOccurrences", out var capElement) && capElement.ValueKind != JsonValueKind.Null)
        {
            maxOccurrences = capElement.GetInt32();
        }

        var updated = new FixedIntervalSchedule(intervalSeconds, current.AnchorAtUtc, endAt, maxOccurrences);
        ScheduleDefinitionPolicy.Validate(updated, policy, referenceTime);
        var next = TriggerScheduleCalculator.InitialNext(updated, referenceTime)
            ?? throw new ArgumentException("No future occurrence matches this schedule.");
        return (updated, next);
    }

    private static bool HasOneShotFields(JsonElement arguments) =>
        TryPresent(arguments, "relativeDayOffset", out _)
        || TryTimeString(arguments, "localDate", out _)
        || TryTimeString(arguments, "atUtc", out _)
        || TryPresent(arguments, "relativeDelaySeconds", out _);

    private static bool TryTimeString(JsonElement arguments, string name, out string value)
    {
        if (!TryString(arguments, name, out value) || string.IsNullOrWhiteSpace(value))
        {
            value = "";
            return false;
        }

        value = value.Trim();
        return true;
    }

    private static bool TryWholeNumber(JsonElement element, out int number)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out number))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.String
            && int.TryParse(element.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out number))
        {
            return true;
        }

        number = 0;
        return false;
    }

    private static bool TryPresent(JsonElement arguments, string name, out JsonElement element)
    {
        if (!arguments.TryGetProperty(name, out element)
            || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            element = default;
            return false;
        }

        return true;
    }

    private static string RequireString(JsonElement arguments, string name) =>
        TryString(arguments, name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new ArgumentException($"{name} is required.");

    private static DateTimeOffset? ResolveOptionalEndAtUtc(JsonElement arguments, DateTimeOffset? current = null)
    {
        if (!arguments.TryGetProperty("endAtUtc", out var element))
        {
            return current;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            throw new TriggerScheduleCommandException(
                "schedule_validation_failed",
                "endAtUtc must be a valid ISO-8601 timestamp (normalized to UTC).");
        }

        var text = element.GetString();
        if (string.IsNullOrWhiteSpace(text) || !DateTimeOffset.TryParse(text, out var parsed))
        {
            throw new TriggerScheduleCommandException(
                "schedule_validation_failed",
                "endAtUtc must be a valid ISO-8601 timestamp (normalized to UTC).");
        }

        return TriggerScheduleCalculator.Truncate(parsed.ToUniversalTime());
    }

    private static bool TryString(JsonElement arguments, string name, out string value)
    {
        value = "";
        if (!arguments.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? "";
        return true;
    }

    private static bool? OptionalRequirement(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw AgentCoreErrors.Validation(name + " must be a boolean.");
        return value.GetBoolean();
    }

    private static AutomationExecutionTarget ChatTarget(JsonElement args, Guid sessionId, Automation? current = null)
    {
        if (args.EnumerateObject().Any(p => p.Name is "sessionId" or "targetSessionId" or "reportToSessionId" or "agentInstanceId" or "profileId"))
            throw AgentCoreErrors.Validation("Core binds the destination; recipient identifiers are not permitted.");
        if (!args.TryGetProperty("executionTarget", out var mode)) return current?.ExecutionTarget ?? throw AgentCoreErrors.Validation("An explicit executionTarget is required.");
        return mode.GetString() switch { "currentSession" => AutomationExecutionTarget.Existing(sessionId),
            "backgroundSession" => AutomationExecutionTarget.Background, _ => throw AgentCoreErrors.Validation("executionTarget must be currentSession or backgroundSession.") };
    }
    private static AutomationCompletionDelivery ChatDelivery(JsonElement args, Guid sessionId, Automation? current = null)
    {
        var target = ChatTarget(args, sessionId, current);
        if (args.TryGetProperty("reportBack", out var report))
        {
            if (report.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || target.Kind != AutomationExecutionTargetKind.BackgroundSession)
                throw AgentCoreErrors.Validation("reportBack is only valid for backgroundSession.");
            return report.GetBoolean() ? AutomationCompletionDelivery.ToSession(sessionId) : AutomationCompletionDelivery.None;
        }
        return target.Kind == AutomationExecutionTargetKind.ExistingSession ? AutomationCompletionDelivery.None : current?.CompletionDelivery ?? AutomationCompletionDelivery.None;
    }
}
