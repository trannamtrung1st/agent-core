using System.Text.Json;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Execution;

internal static class RunTaskIntents
{
    // Only the admitted Automation receipt is read here, never a retrieved page or model transcript.
    internal static AgentRunTaskIntent? FromOccurrence(TriggerOccurrence occurrence)
    {
        if (occurrence.AutomationId is null) return null;
        using var document = JsonDocument.Parse(occurrence.EvidenceJson);
        var data = document.RootElement;
        if (!data.TryGetProperty("instructions", out var instructions) || instructions.ValueKind != JsonValueKind.String)
            throw new ArgumentException("The admitted Automation receipt is missing its task instructions.");
        var text = TriggerText.RequireInstructions(instructions.GetString()!);
        var revision = data.TryGetProperty("automationRevision", out var value) && value.TryGetInt64(out var number)
            ? occurrence.AutomationId + ":" + number : null;
        return new(text, AgentRunTaskSource.Automation, revision, occurrence.SourceKind is TriggerSourceKind.CoreEvent or TriggerSourceKind.ApplicationEvent ? occurrence.SourceKind : null);
    }
}
