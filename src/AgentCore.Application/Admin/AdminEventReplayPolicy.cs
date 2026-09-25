using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

public static class AdminEventReplayPolicy
{
    public static void EnsureInstanceDefinitionVersionChangedReplayMatches(
        AdminEvent existingEvent,
        AdminEventAppend historyAppend,
        AgentInstanceRevisionUpdate update)
    {
        if (update.ActiveVersion is not int requestedToVersion)
        {
            throw AgentCoreErrors.Validation("Active version is required.");
        }

        if (existingEvent.Version != requestedToVersion
            || historyAppend.Version != requestedToVersion)
        {
            throw AgentCoreErrors.Conflict("Instance definition version change does not match the retried command.");
        }

        var existingFrom = ReadVersionTransition(existingEvent.SummaryJson, "fromVersion");
        var existingTo = ReadVersionTransition(existingEvent.SummaryJson, "toVersion");
        var incomingFrom = ReadVersionTransition(historyAppend.SummaryJson, "fromVersion");
        var incomingTo = ReadVersionTransition(historyAppend.SummaryJson, "toVersion");
        if (existingFrom != incomingFrom
            || existingTo != incomingTo
            || incomingTo != requestedToVersion)
        {
            throw AgentCoreErrors.Conflict("Instance definition version change does not match the retried command.");
        }
    }

    private static int ReadVersionTransition(string summaryJson, string propertyName)
    {
        using var document = JsonDocument.Parse(summaryJson);
        if (!document.RootElement.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            throw AgentCoreErrors.Conflict("Instance definition version change history is missing version metadata.");
        }

        return value.GetInt32();
    }

    public static void EnsurePersonaChangedReplayMatches(
        AdminEvent existingEvent,
        AdminEventAppend historyAppend,
        AgentInstanceRevisionUpdate update)
    {
        if (update.Persona is null || update.ExpectedPersonaRevision is not long fromPersonaRevision)
        {
            throw AgentCoreErrors.Validation("Persona and expected persona revision are required.");
        }

        var existingFrom = ReadLongTransition(existingEvent.SummaryJson, "fromPersonaRevision");
        var existingTo = ReadLongTransition(existingEvent.SummaryJson, "personaRevision");
        var incomingFrom = ReadLongTransition(historyAppend.SummaryJson, "fromPersonaRevision");
        var incomingTo = ReadLongTransition(historyAppend.SummaryJson, "personaRevision");
        if (existingFrom != incomingFrom
            || existingTo != incomingTo
            || incomingFrom != fromPersonaRevision
            || existingEvent.Revision != incomingTo
            || historyAppend.Revision != incomingTo)
        {
            throw AgentCoreErrors.Conflict("Persona change does not match the retried command.");
        }

        var existingFingerprint = ReadStringTransition(existingEvent.SummaryJson, "personaFingerprint");
        var incomingFingerprint = ReadStringTransition(historyAppend.SummaryJson, "personaFingerprint");
        var requestedFingerprint = AdminPersonaHistoryFingerprint.Compute(update.Persona);
        if (!string.Equals(existingFingerprint, incomingFingerprint, StringComparison.Ordinal)
            || !string.Equals(incomingFingerprint, requestedFingerprint, StringComparison.Ordinal))
        {
            throw AgentCoreErrors.Conflict("Persona change does not match the retried command.");
        }
    }

    private static string ReadStringTransition(string summaryJson, string propertyName)
    {
        using var document = JsonDocument.Parse(summaryJson);
        if (!document.RootElement.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw AgentCoreErrors.Conflict("Persona change history is missing persona fingerprint metadata.");
        }

        return value.GetString()
            ?? throw AgentCoreErrors.Conflict("Persona change history is missing persona fingerprint metadata.");
    }

    private static long ReadLongTransition(string summaryJson, string propertyName)
    {
        using var document = JsonDocument.Parse(summaryJson);
        if (!document.RootElement.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            throw AgentCoreErrors.Conflict("Persona change history is missing revision metadata.");
        }

        return value.GetInt64();
    }
}
