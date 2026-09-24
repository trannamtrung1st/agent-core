using System.Text.Json;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Triggers;

public sealed record ScheduleConversationContext(
    Guid RegistrationId,
    long Revision,
    TriggerCommandAction LastAction,
    string Intent,
    string TimeZoneId,
    TriggerScheduleKind ScheduleKind,
    TriggerRegistrationStatus Status,
    DateTimeOffset? NextOccurrenceAtUtc)
{
    public bool IsReferentAvailable =>
        RegistrationId != Guid.Empty
        && !string.IsNullOrWhiteSpace(Intent)
        && Status == TriggerRegistrationStatus.Active;

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
            if (!root.TryGetProperty("registrationId", out var idElement)
                || !Guid.TryParse(idElement.GetString(), out var registrationId)
                || registrationId == Guid.Empty)
            {
                return null;
            }

            var revision = root.TryGetProperty("revision", out var revisionElement) && revisionElement.TryGetInt64(out var parsedRevision)
                ? parsedRevision
                : 0;
            var intent = root.TryGetProperty("intent", out var intentElement)
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
            var status = Enum.TryParse<TriggerRegistrationStatus>(statusText, ignoreCase: true, out var parsedStatus)
                ? parsedStatus
                : TriggerRegistrationStatus.Active;
            DateTimeOffset? next = null;
            if (root.TryGetProperty("nextOccurrenceAtUtc", out var nextElement)
                && nextElement.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(nextElement.GetString(), out var parsedNext))
            {
                next = parsedNext;
            }

            return new ScheduleConversationContext(
                registrationId,
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
            $"registrationId={RegistrationId:D}",
            $"intent=\"{Intent}\"",
            $"timeZone={TimeZoneId}",
            $"scheduleKind={ScheduleKind}",
            $"status={Status}",
            NextOccurrenceAtUtc is { } next ? $"nextOccurrenceAtUtc={next:O}" : "nextOccurrenceAtUtc=(none)"
        ];
    }
}
