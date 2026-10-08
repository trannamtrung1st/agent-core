using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Triggers;

public sealed record ScheduleConversationContext(
    Guid AutomationId,
    long Revision,
    TriggerCommandAction LastAction,
    string Instructions,
    string TimeZoneId,
    TriggerScheduleKind ScheduleKind,
    AutomationStatus Status,
    DateTimeOffset? NextOccurrenceAtUtc)
{
    public bool IsReferentAvailable =>
        AutomationId != Guid.Empty
        && !string.IsNullOrWhiteSpace(Instructions)
        && Status == AutomationStatus.Active;

    public static ScheduleConversationContext? TryFromRegistrationJson(
        string json,
        TriggerCommandAction action)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Contains("\"error\"", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("scheduleKind", out var kindElement)
                || kindElement.ValueKind != JsonValueKind.String
                || !Enum.TryParse<TriggerScheduleKind>(kindElement.GetString(), ignoreCase: true, out var scheduleKind)
                || !Enum.IsDefined(scheduleKind)
                || !root.TryGetProperty("automationId", out var idElement)
                || !Guid.TryParse(idElement.GetString(), out var automationId)
                || automationId == Guid.Empty)
            {
                return null;
            }

            var revision = root.TryGetProperty("revision", out var revisionElement) && revisionElement.TryGetInt64(out var parsedRevision)
                ? parsedRevision
                : 0;
            var intent = root.TryGetProperty("instructions", out var intentElement)
                ? intentElement.GetString() ?? string.Empty
                : string.Empty;
            var timeZone = root.TryGetProperty("timeZone", out var zoneElement)
                ? zoneElement.GetString() ?? string.Empty
                : string.Empty;
            var statusText = root.TryGetProperty("status", out var statusElement)
                ? statusElement.GetString()
                : null;
            var status = Enum.TryParse<AutomationStatus>(statusText, ignoreCase: true, out var parsedStatus)
                ? parsedStatus
                : AutomationStatus.Active;
            DateTimeOffset? next = null;
            if (root.TryGetProperty("nextOccurrenceAtUtc", out var nextElement)
                && nextElement.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(nextElement.GetString(), out var parsedNext))
            {
                next = parsedNext;
            }

            return new ScheduleConversationContext(
                automationId,
                revision,
                action,
                intent,
                timeZone,
                scheduleKind,
                status,
                next);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static ScheduleConversationContext FromRegistration(
        Automation registration,
        TriggerCommandAction lastAction) =>
        new(
            registration.AutomationId,
            registration.Revision,
            lastAction,
            registration.Instructions,
            TimeZoneOf(registration.Schedule),
            ScheduleKindOf(registration.Schedule),
            registration.Status,
            registration.NextOccurrenceAtUtc);

    public static ScheduleConversationContext? RefreshFromRegistrationJson(
        ScheduleConversationContext? current,
        string json,
        TriggerCommandAction action)
    {
        var refreshed = TryFromRegistrationJson(json, action);
        if (refreshed is not null || current is null)
        {
            return refreshed;
        }

        // A schedule changed into an event is no longer a valid schedule referent.
        // Results for other Automations must not replace the current schedule.
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("automationId", out var id)
                && id.ValueKind == JsonValueKind.String
                && Guid.TryParse(id.GetString(), out var automationId)
                && automationId == current.AutomationId)
            {
                return null;
            }
        }
        catch (JsonException)
        {
            // An unrelated malformed result cannot revise a trusted referent.
        }

        return current;
    }

    public static async ValueTask<ScheduleConversationContext?> TryReconstructLatestReferentAsync(
        IAutomationService registrations,
        TriggerOwner owner,
        CancellationToken cancellationToken = default)
    {
        var rows = await registrations.ListAsync(owner, null, cancellationToken).ConfigureAwait(false);
        var latest = rows.FirstOrDefault(row => row.Status == AutomationStatus.Active && row.Trigger is ScheduleTrigger);
        return latest is null ? null : FromRegistration(latest, TriggerCommandAction.Create);
    }

    private static string TimeZoneOf(TriggerSchedule schedule) => schedule switch
    {
        OneShotSchedule oneShot => oneShot.TimeZoneId,
        DailySchedule daily => daily.TimeZoneId,
        WeeklySchedule weekly => weekly.TimeZoneId,
        FixedIntervalSchedule => "UTC",
        _ => string.Empty
    };

    private static TriggerScheduleKind ScheduleKindOf(TriggerSchedule schedule) => schedule switch
    {
        OneShotSchedule => TriggerScheduleKind.OneShot,
        DailySchedule => TriggerScheduleKind.Daily,
        WeeklySchedule => TriggerScheduleKind.Weekly,
        FixedIntervalSchedule => TriggerScheduleKind.FixedInterval,
        _ => TriggerScheduleKind.OneShot
    };

    public IReadOnlyList<string> ToPromptLines()
    {
        if (!IsReferentAvailable)
        {
            return [];
        }

        return
        [
            "Trusted schedule referent (resolve “another”, “that”, “it”, or “same”; does not authorize by itself):",
            $"lastAction={LastAction}",
            $"automationId={AutomationId:D}",
            $"instructions=\"{Instructions}\"",
            $"timeZone={TimeZoneId}",
            $"scheduleKind={ScheduleKind}",
            $"status={Status}",
            NextOccurrenceAtUtc is { } next ? $"nextOccurrenceAtUtc={next:O}" : "nextOccurrenceAtUtc=(none)"
        ];
    }
}
