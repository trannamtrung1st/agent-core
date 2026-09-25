using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Admin;

public sealed record AdminAutomationProvenance(
    string AuthorizationOrigin,
    string? SourceSessionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AdminAutomationRegistration(
    Guid RegistrationId,
    string Intent,
    TriggerRegistrationStatus Status,
    string ScheduleKind,
    string TimeZoneId,
    string ScheduleSummary,
    DateTimeOffset? NextOccurrenceAtUtc,
    long Revision,
    string? SuspensionReason,
    AdminAutomationProvenance Provenance);

public sealed class AdminAutomationService(
    IAgentInstanceStore instances,
    ITriggerRegistrationService triggers,
    ILocalUserProfileService localProfiles)
{
    public const int MaxRegistrations = 256;

    public async ValueTask<IReadOnlyList<AdminAutomationRegistration>> ListRegistrationsAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        var instance = await RequireManagedInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        _ = instance;
        var profile = await localProfiles.GetLocalProfileAsync(cancellationToken).ConfigureAwait(false);
        var owner = new TriggerOwner(instanceId, profile.ProfileId);
        var rows = await triggers.ListAsync(owner, status: null, cancellationToken).ConfigureAwait(false);
        return rows
            .Where(row => row.Status is TriggerRegistrationStatus.Active or TriggerRegistrationStatus.SuspendedPolicy)
            .OrderByDescending(row => row.Provenance.CreatedAt)
            .ThenByDescending(row => row.RegistrationId)
            .Take(MaxRegistrations)
            .Select(Map)
            .ToArray();
    }

    public async ValueTask<AdminAutomationRegistration> GetRegistrationAsync(
        Guid instanceId,
        Guid registrationId,
        CancellationToken cancellationToken = default)
    {
        await RequireManagedInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        var profile = await localProfiles.GetLocalProfileAsync(cancellationToken).ConfigureAwait(false);
        var owner = new TriggerOwner(instanceId, profile.ProfileId);
        var existing = await triggers.GetAsync(owner, registrationId, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Trigger registration was not found.");
        return Map(existing);
    }

    public async ValueTask<AdminAutomationRegistration> CancelRegistrationAsync(
        Guid instanceId,
        Guid registrationId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await RequireManagedInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        var profile = await localProfiles.GetLocalProfileAsync(cancellationToken).ConfigureAwait(false);
        var owner = new TriggerOwner(instanceId, profile.ProfileId);
        var existing = await triggers.GetAsync(owner, registrationId, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Trigger registration was not found.");
        if (existing.Status is TriggerRegistrationStatus.Completed or TriggerRegistrationStatus.Cancelled)
        {
            throw AgentCoreErrors.Conflict("Trigger registration is no longer cancellable.");
        }

        var cancelled = await triggers
            .CancelAsync(owner, registrationId, expectedRevision, cancellationToken)
            .ConfigureAwait(false);
        return Map(cancelled);
    }

    private async ValueTask<AgentInstance> RequireManagedInstanceAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        var instance = await instances.FindAsync(instanceId, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Agent instance was not found.");
        if (instance.Compatibility)
        {
            throw AgentCoreErrors.Forbidden("Automation administration requires a managed instance.");
        }

        return instance;
    }

    private static AdminAutomationRegistration Map(TriggerRegistration registration)
    {
        var (kind, zone, summary) = DescribeSchedule(registration.Schedule);
        return new AdminAutomationRegistration(
            registration.RegistrationId,
            registration.Intent,
            registration.Status,
            kind,
            zone,
            summary,
            registration.NextOccurrenceAtUtc,
            registration.Revision,
            registration.SuspensionReason,
            new AdminAutomationProvenance(
                registration.Provenance.AuthorizationOrigin.ToString(),
                registration.Provenance.SourceSessionId?.ToString("D"),
                registration.Provenance.CreatedAt,
                registration.Provenance.UpdatedAt));
    }

    private static (string Kind, string TimeZoneId, string Summary) DescribeSchedule(TriggerSchedule schedule) =>
        schedule switch
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
                $"Every {weekly.IntervalWeeks} week(s) on selected days at {weekly.LocalTime:HH:mm}"),
            FixedIntervalSchedule fixedInterval => (
                "fixedInterval",
                "UTC",
                $"Every {fixedInterval.IntervalSeconds} seconds"),
            _ => ("unknown", "UTC", "Schedule")
        };
}
