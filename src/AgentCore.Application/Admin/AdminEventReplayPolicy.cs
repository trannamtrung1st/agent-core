using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;

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
}
