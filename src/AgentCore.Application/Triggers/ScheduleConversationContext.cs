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
            if (!root.TryGetProperty("automationId", out var idElement)
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
            var kindText = root.TryGetProperty("scheduleKind", out var kindElement)
                ? kindElement.GetString()
                : null;
            var scheduleKind = Enum.TryParse<TriggerScheduleKind>(kindText, ignoreCase: true, out var parsedKind)
                ? parsedKind
                : TriggerScheduleKind.OneShot;
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

    public static async ValueTask<ScheduleConversationContext?> TryReconstructLatestReferentAsync(
        IAutomationService registrations,
        TriggerOwner owner,
        CancellationToken cancellationToken = default)
    {
        var rows = await registrations.ListAsync(owner, null, cancellationToken).ConfigureAwait(false);
        var latest = rows.FirstOrDefault(row => row.Status == AutomationStatus.Active);
        return latest is null ? null : FromRegistration(latest, TriggerCommandAction.Create);
    }

    private static string TimeZoneOf(TriggerSchedule schedule) => schedule switch
    {
        OneShotSchedule oneShot => oneShot.TimeZoneId,
        DailySchedule daily => daily.TimeZoneId,
        WeeklySchedule weekly => weekly.TimeZoneId,
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
