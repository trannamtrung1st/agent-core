using AgentCore.Application.Models;
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
    Guid AutomationId,
    string Intent,
    AutomationStatus Status,
    string ScheduleKind,
    string TimeZoneId,
    string ScheduleSummary,
    DateTimeOffset? NextOccurrenceAtUtc,
    long Revision,
    string? SuspensionReason,
    AdminAutomationProvenance Provenance,
    string? ModelOverrideCatalogKey,
    string? ModelOverrideReasoningEffort,
    string ModelSource);

public sealed class AdminAutomationService(
    IAgentInstanceStore instances,
    IAutomationService triggers,
    ILocalUserProfileService localProfiles,
    IModelCatalog? modelCatalog = null)
{
    public const int MaxRegistrations = 256;

    public async ValueTask<IReadOnlyList<AdminAutomationRegistration>> ListRegistrationsAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default)
    {
        var instance = await RequireManagedInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        var profile = await localProfiles.GetLocalProfileAsync(cancellationToken).ConfigureAwait(false);
        var owner = new TriggerOwner(instanceId, profile.ProfileId);
        var rows = await triggers.ListAsync(owner, status: null, cancellationToken).ConfigureAwait(false);
        return rows
            .Where(row => row.EventId is null)
            .Where(row => row.Status is AutomationStatus.Active or AutomationStatus.SuspendedPolicy)
            .OrderByDescending(row => row.Provenance.CreatedAt)
            .ThenByDescending(row => row.AutomationId)
            .Take(MaxRegistrations)
            .Select(row => Map(instance, row))
            .ToArray();
    }

    public async ValueTask<AdminAutomationRegistration> SetModelOverrideAsync(
        Guid instanceId,
        Guid automationId,
        long expectedRevision,
        string? catalogKey,
        string? reasoningEffort,
        CancellationToken cancellationToken = default)
    {
        var instance = await RequireManagedInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        if (modelCatalog is null)
        {
            throw AgentCoreErrors.Validation("The model catalog is not available.");
        }

        ExecutionModelPolicy.RequireSelectable(modelCatalog, catalogKey, reasoningEffort);
        var profile = await localProfiles.GetLocalProfileAsync(cancellationToken).ConfigureAwait(false);
        var owner = new TriggerOwner(instanceId, profile.ProfileId);
        var updated = await triggers.SetModelOverrideAsync(
            owner,
            automationId,
            expectedRevision,
            catalogKey,
            reasoningEffort,
            cancellationToken).ConfigureAwait(false);
        return Map(instance, updated);
    }

    public async ValueTask<AdminAutomationRegistration> GetRegistrationAsync(
        Guid instanceId,
        Guid automationId,
        CancellationToken cancellationToken = default)
    {
        var instance = await RequireManagedInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        var profile = await localProfiles.GetLocalProfileAsync(cancellationToken).ConfigureAwait(false);
        var owner = new TriggerOwner(instanceId, profile.ProfileId);
        var existing = await triggers.GetAsync(owner, automationId, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Trigger registration was not found.");
        return Map(instance, existing);
    }

    public async ValueTask<AdminAutomationRegistration> CancelRegistrationAsync(
        Guid instanceId,
        Guid automationId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var instance = await RequireManagedInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        var profile = await localProfiles.GetLocalProfileAsync(cancellationToken).ConfigureAwait(false);
        var owner = new TriggerOwner(instanceId, profile.ProfileId);
        var existing = await triggers.GetAsync(owner, automationId, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Trigger registration was not found.");
        if (existing.Status is AutomationStatus.Completed or AutomationStatus.Cancelled)
        {
            throw AgentCoreErrors.Conflict("Trigger registration is no longer cancellable.");
        }

        var cancelled = await triggers
            .CancelAsync(owner, automationId, expectedRevision, cancellationToken)
            .ConfigureAwait(false);
        return Map(instance, cancelled);
    }

    private async ValueTask<AgentInstance> RequireManagedInstanceAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        var instance = await instances.FindAsync(instanceId, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Agent instance was not found.");
        return instance;
    }

    private static AdminAutomationRegistration Map(AgentInstance instance, Automation registration)
    {
        var (kind, zone, summary) = DescribeSchedule(registration.Schedule);
        var source = !string.IsNullOrWhiteSpace(registration.ModelOverrideCatalogKey)
            ? "Trigger override"
            : !string.IsNullOrWhiteSpace(instance.UnattendedModelCatalogKey)
                ? "Unattended default"
                : "Conversation default";
        return new AdminAutomationRegistration(
            registration.AutomationId,
            registration.Instructions,
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
                registration.Provenance.UpdatedAt),
            registration.ModelOverrideCatalogKey,
            registration.ModelOverrideReasoningEffort,
            source);
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
