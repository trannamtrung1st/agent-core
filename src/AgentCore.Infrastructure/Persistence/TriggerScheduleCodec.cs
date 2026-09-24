using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Infrastructure.Persistence;

internal static class TriggerScheduleCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(TriggerSchedule schedule) => schedule switch
    {
        OneShotSchedule oneShot => JsonSerializer.Serialize(new OneShotDto(
            "oneShot",
            oneShot.AtUtc.ToUnixTimeMilliseconds(),
            oneShot.TimeZoneId,
            FormatDate(oneShot.LocalDate),
            FormatTime(oneShot.LocalTime)), Options),
        DailySchedule daily => JsonSerializer.Serialize(new DailyDto(
            "daily",
            daily.IntervalDays,
            FormatTime(daily.LocalTime)!,
            daily.TimeZoneId,
            FormatDate(daily.StartDate),
            FormatDate(daily.EndDate),
            daily.MaxOccurrences), Options),
        WeeklySchedule weekly => JsonSerializer.Serialize(new WeeklyDto(
            "weekly",
            weekly.IntervalWeeks,
            weekly.Weekdays.Select(day => day.ToString()).ToArray(),
            FormatTime(weekly.LocalTime)!,
            weekly.TimeZoneId,
            FormatDate(weekly.StartDate),
            FormatDate(weekly.EndDate),
            weekly.MaxOccurrences), Options),
        FixedIntervalSchedule fixedInterval => JsonSerializer.Serialize(new FixedIntervalDto(
            "fixedInterval",
            fixedInterval.IntervalSeconds,
            fixedInterval.AnchorAtUtc.ToUnixTimeMilliseconds(),
            fixedInterval.EndAtUtc?.ToUnixTimeMilliseconds(),
            fixedInterval.MaxOccurrences), Options),
        _ => throw new InvalidOperationException("Schedule kind is not supported.")
    };

    public static TriggerSchedule Deserialize(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var kind = document.RootElement.GetProperty("kind").GetString();
            return kind switch
            {
                "oneShot" => MapOneShot(JsonSerializer.Deserialize<OneShotDto>(json, Options)),
                "daily" => MapDaily(JsonSerializer.Deserialize<DailyDto>(json, Options)),
                "weekly" => MapWeekly(JsonSerializer.Deserialize<WeeklyDto>(json, Options)),
                "fixedInterval" => MapFixedInterval(JsonSerializer.Deserialize<FixedIntervalDto>(json, Options)),
                _ => throw AgentCoreErrors.Persistence("Stored trigger schedule kind is not supported.")
            };
        }
        catch (AgentCoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or KeyNotFoundException or FormatException)
        {
            throw AgentCoreErrors.Persistence("Stored trigger schedule could not be read.");
        }
    }

    private static OneShotSchedule MapOneShot(OneShotDto? dto)
    {
        if (dto is null)
        {
            throw AgentCoreErrors.Persistence("Stored trigger schedule could not be read.");
        }

        return new OneShotSchedule(
            DateTimeOffset.FromUnixTimeMilliseconds(dto.AtUtc),
            dto.TimeZoneId,
            ParseDate(dto.LocalDate),
            ParseTime(dto.LocalTime));
    }

    private static DailySchedule MapDaily(DailyDto? dto)
    {
        if (dto is null || ParseTime(dto.LocalTime) is not { } localTime)
        {
            throw AgentCoreErrors.Persistence("Stored trigger schedule could not be read.");
        }

        return new DailySchedule(
            dto.IntervalDays,
            localTime,
            dto.TimeZoneId,
            ParseDate(dto.StartDate),
            ParseDate(dto.EndDate),
            dto.MaxOccurrences);
    }

    private static WeeklySchedule MapWeekly(WeeklyDto? dto)
    {
        if (dto is null || ParseTime(dto.LocalTime) is not { } localTime || dto.Weekdays is null)
        {
            throw AgentCoreErrors.Persistence("Stored trigger schedule could not be read.");
        }

        var weekdays = dto.Weekdays.Select(day => Enum.Parse<DayOfWeek>(day, ignoreCase: false)).ToArray();
        return new WeeklySchedule(
            dto.IntervalWeeks,
            weekdays,
            localTime,
            dto.TimeZoneId,
            ParseDate(dto.StartDate),
            ParseDate(dto.EndDate),
            dto.MaxOccurrences);
    }

    private static string? FormatDate(DateOnly? value) =>
        value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? FormatTime(TimeOnly? value) =>
        value?.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private static DateOnly? ParseDate(string? value) =>
        value is null ? null : DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static TimeOnly? ParseTime(string? value) =>
        value is null ? null : TimeOnly.ParseExact(value, "HH:mm:ss", CultureInfo.InvariantCulture);

    private sealed record OneShotDto(string Kind, long AtUtc, string TimeZoneId, string? LocalDate, string? LocalTime);

    private sealed record DailyDto(
        string Kind,
        int IntervalDays,
        string LocalTime,
        string TimeZoneId,
        string? StartDate,
        string? EndDate,
        int? MaxOccurrences);

    private sealed record WeeklyDto(
        string Kind,
        int IntervalWeeks,
        string[] Weekdays,
        string LocalTime,
        string TimeZoneId,
        string? StartDate,
        string? EndDate,
        int? MaxOccurrences);

    private static FixedIntervalSchedule MapFixedInterval(FixedIntervalDto? dto)
    {
        if (dto is null)
        {
            throw AgentCoreErrors.Persistence("Stored trigger schedule could not be read.");
        }

        DateTimeOffset? end = dto.EndAtUtc is long endMs
            ? DateTimeOffset.FromUnixTimeMilliseconds(endMs)
            : null;
        return new FixedIntervalSchedule(
            dto.IntervalSeconds,
            DateTimeOffset.FromUnixTimeMilliseconds(dto.AnchorAtUtc),
            end,
            dto.MaxOccurrences);
    }

    private sealed record FixedIntervalDto(
        string Kind,
        int IntervalSeconds,
        long AnchorAtUtc,
        long? EndAtUtc,
        int? MaxOccurrences);
}
