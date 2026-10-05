using System.Text.Json;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Triggers;

public static class ScheduleRegistrationRules
{
    public static void ValidateSave(TriggerRegistration? current, TriggerRegistration proposed, long expectedRevision)
    {
        if (proposed.Provenance.AuthorizationOrigin is not (TriggerAuthorizationOrigin.AdminOwner or TriggerAuthorizationOrigin.CurrentUserTurn)
            || proposed.EventSourceId is not null || current?.Provenance.AuthorizationOrigin == TriggerAuthorizationOrigin.AdminThought)
            throw AgentCoreErrors.Forbidden("Only schedules can be changed here.");
        if ((current?.Revision ?? 0) != expectedRevision || proposed.Revision != expectedRevision + 1)
            throw AgentCoreErrors.Conflict("Schedule revision is stale.");
        if (current?.Status is TriggerRegistrationStatus.Cancelled or TriggerRegistrationStatus.Completed or TriggerRegistrationStatus.Expired)
            throw AgentCoreErrors.Validation("Terminal schedule cannot be restored.");
        if (current is not null && (proposed.Provenance.AuthorizationOrigin != current.Provenance.AuthorizationOrigin
            || proposed.Provenance.SourceSessionId != current.Provenance.SourceSessionId || proposed.Provenance.SourceEventId != current.Provenance.SourceEventId))
            throw AgentCoreErrors.Forbidden("Schedule provenance cannot change.");
        if (proposed.Schedule is FixedIntervalSchedule { IntervalSeconds: < TriggerLimits.MinFixedIntervalSeconds })
            throw AgentCoreErrors.Validation("Schedule interval must be at least 60 seconds.");
        TriggerText.RequireIntent(proposed.Intent);
    }
    public static bool IsManual(TriggerOccurrence o)
    {
        if (o.SourceKind != TriggerSourceKind.Schedule) return false;
        try
        {
            using var json = JsonDocument.Parse(o.EvidenceJson);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("manualOwnerRun", out var marker) && marker.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }
    public static TriggerOccurrence ManualOccurrence(TriggerOccurrence o)
    {
        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(o.EvidenceJson)!;
        data["manualOwnerRun"] = JsonSerializer.SerializeToElement(true);
        return new(o.OccurrenceId, o.DedupeKey, o.RegistrationId, o.Owner, o.SourceKind, o.ScheduledAtUtc,
            o.ObservedAtUtc, o.AdmittedAtUtc, JsonSerializer.Serialize(data), o.SourceEventId, o.ScheduleRevision,
            o.Disposition, o.DispositionReason, o.RoutingRevision, o.RoutingUpdatedAtUtc, o.ClaimId, o.ClaimLeaseExpiresAtUtc,
            o.DurableWorkItemId, o.ModelPin);
    }
}
