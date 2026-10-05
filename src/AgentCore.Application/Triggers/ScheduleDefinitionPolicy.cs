using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Triggers;

/// <summary>Shared timing capabilities for conversational and owner-authored schedules.</summary>
public static class ScheduleDefinitionPolicy
{
    public static void Validate(TriggerSchedule schedule, TriggerPolicy policy, DateTimeOffset now, string intent = "")
    {
        if (!policy.Enabled) throw new ArgumentException("Scheduling is disabled for this agent.");
        switch (schedule)
        {
            case OneShotSchedule once:
                if (!policy.AllowOneShot) throw new ArgumentException("One-shot schedules are not enabled.");
                if (once.AtUtc <= now || once.AtUtc > now.AddDays(policy.OneShotHorizonDays))
                    throw new ArgumentException("One-shot time must be in the future and inside the scheduling horizon.");
                break;
            case DailySchedule daily:
                if (!policy.AllowDaily) throw new ArgumentException("Daily schedules are not enabled.");
                if (daily.IntervalDays < policy.MinRecurrenceDays)
                    throw new TriggerScheduleCommandException("unsupported_recurrence", "Daily schedules cannot represent sub-day recurrence. Use kind fixed_interval with intervalSeconds for minute or hour cadences.");
                RequireEnd(policy, daily.EndDate is not null || daily.MaxOccurrences is not null);
                break;
            case WeeklySchedule weekly:
                if (!policy.AllowWeekly) throw new ArgumentException("Weekly schedules are not enabled.");
                if ((long)weekly.IntervalWeeks * 7 < policy.MinRecurrenceDays) throw new ArgumentException("Weekly interval is shorter than the policy minimum.");
                RequireEnd(policy, weekly.EndDate is not null || weekly.MaxOccurrences is not null);
                break;
            case FixedIntervalSchedule fixedInterval:
                if (!policy.AllowFixedInterval) throw new TriggerScheduleCommandException("unsupported_recurrence", "Fixed-interval recurrence is not enabled for this agent.");
                RequireInterval(policy, fixedInterval.IntervalSeconds, now, intent);
                RequireEnd(policy, fixedInterval.EndAtUtc is not null || fixedInterval.MaxOccurrences is not null);
                break;
            default: throw new ArgumentException("Schedule kind is unsupported.");
        }
    }

    public static void RequireInterval(TriggerPolicy policy, int seconds, DateTimeOffset now, string intent = "")
    {
        if (seconds < policy.MinFixedIntervalSeconds)
            throw new TriggerScheduleCommandException("recurrence_below_minimum", $"The minimum supported fixed interval is {policy.MinFixedIntervalSeconds} seconds. Ask the user for a longer interval.",
                ScheduleDraftContext.ForFixedIntervalRejection(intent, seconds, "recurrence_below_minimum", now));
    }

    private static void RequireEnd(TriggerPolicy policy, bool hasEnd)
    {
        if (!hasEnd && !policy.AllowIndefiniteRecurrence)
            throw new ArgumentException("Indefinite recurrence is not enabled. Ask for an end date or occurrence cap.");
    }
}
