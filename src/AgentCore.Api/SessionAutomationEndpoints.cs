using AgentCore.Application.Triggers;
using AgentCore.Api.Http;
using AgentCore.Api.Mapping;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Triggers;

namespace AgentCore.Api;

public static class SessionAutomationEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/sessions/{sessionId:guid}/automations")
            .AddEndpointFilter<OwnerCapabilityFilter>();

        group.MapGet("", async (
            Guid sessionId,
            int? limit,
            Guid? before,
            SessionManager sessions,
            ILocalUserProfileService profiles,
            IAutomationService triggers,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (limit is < 1) throw AgentCoreErrors.Validation("limit must be at least 1.");
                var owner = await RequireOwnerAsync(sessions, profiles, sessionId, cancellationToken).ConfigureAwait(false);
                var rows = limit is null && before is null
                    ? await triggers.ListAsync(owner, status: null, cancellationToken).ConfigureAwait(false)
                    : await triggers.ListAutomationsPageAsync(owner, limit ?? 50, before, cancellationToken).ConfigureAwait(false);
                return Results.Json(new SessionAutomationListResponse(
                    rows.Select(ToResponse).ToArray()));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });

        group.MapPost("/{triggerId:guid}/cancel", async (
            Guid sessionId,
            Guid triggerId,
            CancelTriggerRequest? body,
            SessionManager sessions,
            ILocalUserProfileService profiles,
            IAutomationService triggers,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (body is null || body.ExpectedRevision < 1)
                {
                    throw AgentCoreErrors.Validation("expectedRevision is required.");
                }

                var owner = await RequireOwnerAsync(sessions, profiles, sessionId, cancellationToken).ConfigureAwait(false);
                var cancelled = await triggers.CancelAsync(owner, triggerId, body.ExpectedRevision, cancellationToken)
                    .ConfigureAwait(false);
                return Results.Json(ToResponse(cancelled));
            }
            catch (AgentCoreException ex)
            {
                return ProblemResults.From(ex);
            }
        });
    }

    private static async Task<TriggerOwner> RequireOwnerAsync(
        SessionManager sessions,
        ILocalUserProfileService profiles,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var snapshot = await sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var local = await profiles.GetLocalProfileAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot.ProfileId != local.ProfileId)
        {
            throw AgentCoreErrors.NotFound("Session was not found.");
        }

        return new TriggerOwner(snapshot.AgentInstanceId, local.ProfileId);
    }

    internal static SessionAutomationResponse ToResponse(Automation registration)
    {
        var (kind, zone, summary) = registration.Trigger is ScheduleTrigger scheduled
            ? Describe(scheduled.Schedule)
            : ("events", (string?)null, AutomationRules.Describe(registration));
        return new SessionAutomationResponse(
            registration.AutomationId.ToString(),
            registration.Instructions,
            ToStatus(registration.Status),
            kind,
            zone,
            summary,
            registration.NextOccurrenceAtUtc is DateTimeOffset next ? HttpMapping.Format(next) : null,
            registration.Revision,
            registration.SuspensionReason, registration.Name,
            new(registration.ExecutionTarget.SessionId is null ? "backgroundSession" : "existingSession", registration.ExecutionTarget.SessionId?.ToString("D")),
            new(registration.CompletionDelivery.SessionId is null ? "none" : "toSession", registration.CompletionDelivery.SessionId?.ToString("D")), registration.Owner.AgentInstanceId.ToString("D"));
    }

    private static string ToStatus(AutomationStatus status) => status switch
    {
        AutomationStatus.Active => "active",
        AutomationStatus.Completed => "completed",
        AutomationStatus.Cancelled => "cancelled",
        AutomationStatus.Expired => "expired",
        AutomationStatus.SuspendedPolicy => "suspendedPolicy",
        AutomationStatus.Disabled => "disabled",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    private static (string Kind, string TimeZone, string Summary) Describe(TriggerSchedule schedule) => schedule switch
    {
        OneShotSchedule oneShot => (
            "oneShot",
            oneShot.TimeZoneId,
            oneShot.LocalDate is DateOnly date && oneShot.LocalTime is TimeOnly time
                ? $"Once on {date:yyyy-MM-dd} at {time:HH:mm}"
                : $"Once at {oneShot.AtUtc:yyyy-MM-dd HH:mm} UTC"),
        DailySchedule daily => (
            "daily",
            daily.TimeZoneId,
            daily.IntervalDays == 1
                ? $"Every day at {daily.LocalTime:HH:mm}"
                : $"Every {daily.IntervalDays} days at {daily.LocalTime:HH:mm}"),
        WeeklySchedule weekly => (
            "weekly",
            weekly.TimeZoneId,
            weekly.IntervalWeeks == 1
                ? $"Every {FormatWeekdays(weekly.Weekdays)} at {weekly.LocalTime:HH:mm}"
                : $"Every {weekly.IntervalWeeks} weeks on {FormatWeekdays(weekly.Weekdays)} at {weekly.LocalTime:HH:mm}"),
        FixedIntervalSchedule fixedInterval => (
            "fixedInterval",
            "UTC",
            FormatFixedInterval(fixedInterval)),
        _ => throw new ArgumentOutOfRangeException(nameof(schedule), schedule.GetType().Name, null)
    };

    private static string FormatWeekdays(IReadOnlyList<DayOfWeek> weekdays) =>
        string.Join(", ", weekdays.Select(day => day.ToString()));

    private static string FormatFixedInterval(FixedIntervalSchedule schedule)
    {
        var seconds = schedule.IntervalSeconds;
        if (seconds % 3600 == 0)
        {
            var hours = seconds / 3600;
            return hours == 1 ? "Every 1 hour" : $"Every {hours} hours";
        }

        if (seconds % 60 == 0)
        {
            var minutes = seconds / 60;
            return minutes == 1 ? "Every 1 minute" : $"Every {minutes} minutes";
        }

        return seconds == 1 ? "Every 1 second" : $"Every {seconds} seconds";
    }
}
