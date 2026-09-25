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

    public static void EnsureInstanceLifecycleReplayMatches(
        AdminEvent existingEvent,
        AdminEventAppend historyAppend,
        AgentInstanceRevisionUpdate update)
    {
        if (update.Lifecycle is not AgentInstanceLifecycle requestedLifecycle)
        {
            throw AgentCoreErrors.Validation("Lifecycle is required.");
        }

        if (existingEvent.Operation != historyAppend.Operation
            || existingEvent.Operation is not (AdminEventOperationKind.InstanceArchived or AdminEventOperationKind.InstanceUnarchived))
        {
            throw AgentCoreErrors.Conflict("Operation id is already used for a different admin event.");
        }

        var existingFrom = ReadLifecycleTransition(existingEvent.SummaryJson, "fromLifecycle");
        var existingTo = ReadLifecycleTransition(existingEvent.SummaryJson, "toLifecycle");
        var incomingFrom = ReadLifecycleTransition(historyAppend.SummaryJson, "fromLifecycle");
        var incomingTo = ReadLifecycleTransition(historyAppend.SummaryJson, "toLifecycle");
        var requestedTo = requestedLifecycle.ToString();
        if (!string.Equals(existingFrom, incomingFrom, StringComparison.Ordinal)
            || !string.Equals(existingTo, incomingTo, StringComparison.Ordinal)
            || !string.Equals(incomingTo, requestedTo, StringComparison.Ordinal)
            || existingEvent.Revision != historyAppend.Revision)
        {
            throw AgentCoreErrors.Conflict("Instance lifecycle change does not match the retried command.");
        }
    }

    public static void EnsureMemoryItemDeletedReplayMatches(
        AdminEvent existingEvent,
        AdminEventAppend historyAppend,
        Guid instanceId,
        AdminLearnedMemoryScope scope,
        Guid memoryId,
        Guid? sessionId)
    {
        EnsureMemoryMutationReplayMatches(
            existingEvent,
            historyAppend,
            AdminEventOperationKind.MemoryItemDeleted,
            instanceId,
            scope,
            sessionId);
        var existingMemoryId = ReadStringTransition(existingEvent.SummaryJson, "memoryId");
        var incomingMemoryId = ReadStringTransition(historyAppend.SummaryJson, "memoryId");
        var requestedMemoryId = memoryId.ToString("D");
        if (!string.Equals(existingMemoryId, incomingMemoryId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(incomingMemoryId, requestedMemoryId, StringComparison.OrdinalIgnoreCase))
        {
            throw AgentCoreErrors.Conflict("Memory item delete does not match the retried command.");
        }
    }

    public static void EnsureMemoryScopeResetReplayMatches(
        AdminEvent existingEvent,
        AdminEventAppend historyAppend,
        Guid instanceId,
        AdminLearnedMemoryScope scope,
        int itemsRemoved,
        Guid? sessionId)
    {
        EnsureMemoryMutationReplayMatches(
            existingEvent,
            historyAppend,
            AdminEventOperationKind.MemoryScopeReset,
            instanceId,
            scope,
            sessionId);
        var existingRemoved = ReadIntTransition(existingEvent.SummaryJson, "itemsRemoved");
        var incomingRemoved = ReadIntTransition(historyAppend.SummaryJson, "itemsRemoved");
        if (existingRemoved != incomingRemoved || incomingRemoved != itemsRemoved)
        {
            throw AgentCoreErrors.Conflict("Memory scope reset does not match the retried command.");
        }
    }

    public static void EnsureTriggerRegistrationRevokedReplayMatches(
        AdminEvent existingEvent,
        AdminEventAppend historyAppend,
        Guid instanceId,
        Guid registrationId,
        long revision)
    {
        if (existingEvent.Operation != historyAppend.Operation
            || existingEvent.Operation != AdminEventOperationKind.TriggerRegistrationRevoked)
        {
            throw AgentCoreErrors.Conflict("Operation id is already used for a different admin event.");
        }

        var existingInstanceId = ReadStringTransition(existingEvent.SummaryJson, "instanceId");
        var incomingInstanceId = ReadStringTransition(historyAppend.SummaryJson, "instanceId");
        var requestedInstanceId = instanceId.ToString("D");
        if (!string.Equals(existingInstanceId, incomingInstanceId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(incomingInstanceId, requestedInstanceId, StringComparison.OrdinalIgnoreCase))
        {
            throw AgentCoreErrors.Conflict("Trigger registration revoke does not match the retried command.");
        }

        var existingRegistrationId = ReadStringTransition(existingEvent.SummaryJson, "registrationId");
        var incomingRegistrationId = ReadStringTransition(historyAppend.SummaryJson, "registrationId");
        var requestedRegistrationId = registrationId.ToString("D");
        if (!string.Equals(existingRegistrationId, incomingRegistrationId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(incomingRegistrationId, requestedRegistrationId, StringComparison.OrdinalIgnoreCase))
        {
            throw AgentCoreErrors.Conflict("Trigger registration revoke does not match the retried command.");
        }

        if (existingEvent.Revision != revision || historyAppend.Revision != revision)
        {
            throw AgentCoreErrors.Conflict("Trigger registration revoke does not match the retried command.");
        }
    }

    private static void EnsureMemoryMutationReplayMatches(
        AdminEvent existingEvent,
        AdminEventAppend historyAppend,
        AdminEventOperationKind operation,
        Guid instanceId,
        AdminLearnedMemoryScope scope,
        Guid? sessionId)
    {
        if (existingEvent.Operation != historyAppend.Operation || existingEvent.Operation != operation)
        {
            throw AgentCoreErrors.Conflict("Operation id is already used for a different admin event.");
        }

        var existingInstanceId = ReadStringTransition(existingEvent.SummaryJson, "instanceId");
        var incomingInstanceId = ReadStringTransition(historyAppend.SummaryJson, "instanceId");
        var requestedInstanceId = instanceId.ToString("D");
        if (!string.Equals(existingInstanceId, incomingInstanceId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(incomingInstanceId, requestedInstanceId, StringComparison.OrdinalIgnoreCase))
        {
            throw AgentCoreErrors.Conflict("Memory administration does not match the retried command.");
        }

        var existingScope = ReadStringTransition(existingEvent.SummaryJson, "scope");
        var incomingScope = ReadStringTransition(historyAppend.SummaryJson, "scope");
        var requestedScope = scope.ToString();
        if (!string.Equals(existingScope, incomingScope, StringComparison.Ordinal)
            || !string.Equals(incomingScope, requestedScope, StringComparison.Ordinal))
        {
            throw AgentCoreErrors.Conflict("Memory administration does not match the retried command.");
        }

        var existingSessionId = ReadNullableGuidTransition(existingEvent.SummaryJson, "sessionId");
        var incomingSessionId = ReadNullableGuidTransition(historyAppend.SummaryJson, "sessionId");
        var requestedSessionId = sessionId?.ToString("D");
        if (!string.Equals(existingSessionId, incomingSessionId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(incomingSessionId, requestedSessionId, StringComparison.OrdinalIgnoreCase))
        {
            throw AgentCoreErrors.Conflict("Memory administration does not match the retried command.");
        }
    }

    private static int ReadIntTransition(string summaryJson, string propertyName)
    {
        using var document = JsonDocument.Parse(summaryJson);
        if (!document.RootElement.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            throw AgentCoreErrors.Conflict("Memory administration history is missing metadata.");
        }

        return value.GetInt32();
    }

    private static string? ReadNullableGuidTransition(string summaryJson, string propertyName)
    {
        using var document = JsonDocument.Parse(summaryJson);
        if (!document.RootElement.TryGetProperty(propertyName, out var value))
        {
            throw AgentCoreErrors.Conflict("Memory administration history is missing metadata.");
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw AgentCoreErrors.Conflict("Memory administration history is missing metadata.");
        }

        return value.GetString();
    }

    private static string ReadLifecycleTransition(string summaryJson, string propertyName)
    {
        using var document = JsonDocument.Parse(summaryJson);
        if (!document.RootElement.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw AgentCoreErrors.Conflict("Instance lifecycle change history is missing lifecycle metadata.");
        }

        return value.GetString()
            ?? throw AgentCoreErrors.Conflict("Instance lifecycle change history is missing lifecycle metadata.");
    }
}
