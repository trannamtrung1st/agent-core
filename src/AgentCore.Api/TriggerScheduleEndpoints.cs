using AgentCore.Api.Http;
using AgentCore.Api.Mapping;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Triggers;

namespace AgentCore.Api;

public static class TriggerScheduleEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/sessions/{sessionId:guid}/triggers")
            .AddEndpointFilter<OwnerCapabilityFilter>();

        group.MapGet("", async (
            Guid sessionId,
            SessionManager sessions,
            ILocalUserProfileService profiles,
            ITriggerRegistrationService triggers,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var owner = await RequireOwnerAsync(sessions, profiles, sessionId, cancellationToken).ConfigureAwait(false);
                var rows = await triggers.ListAsync(owner, status: null, cancellationToken).ConfigureAwait(false);
                return Results.Json(new TriggerScheduleListResponse(rows.Select(ToResponse).ToArray()));
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
            ITriggerRegistrationService triggers,
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
        if (snapshot.ProfileId != local.ProfileId || snapshot.AgentInstanceId is not Guid instanceId)
        {
            throw AgentCoreErrors.NotFound("Session was not found.");
        }

        return new TriggerOwner(instanceId, local.ProfileId);
    }

    private static TriggerScheduleResponse ToResponse(TriggerRegistration registration)
    {
        var (kind, zone, summary) = Describe(registration.Schedule);
        return new TriggerScheduleResponse(
            registration.RegistrationId.ToString(),
            registration.Intent,
            ToStatus(registration.Status),
            kind,
            zone,
            summary,
            registration.NextOccurrenceAtUtc is DateTimeOffset next ? HttpMapping.Format(next) : null,
            registration.Revision,
            registration.SuspensionReason);
    }

    private static string ToStatus(TriggerRegistrationStatus status) => status switch
    {
        TriggerRegistrationStatus.Active => "active",
        TriggerRegistrationStatus.Completed => "completed",
        TriggerRegistrationStatus.Cancelled => "cancelled",
        TriggerRegistrationStatus.Expired => "expired",
        TriggerRegistrationStatus.SuspendedPolicy => "suspendedPolicy",
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
        _ => throw new ArgumentOutOfRangeException(nameof(schedule), schedule.GetType().Name, null)
    };

    private static string FormatWeekdays(IReadOnlyList<DayOfWeek> weekdays) =>
        string.Join(", ", weekdays.Select(day => day.ToString()));
}
