using System.Collections.Immutable;
using System.Text.Json;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Workflows;

namespace AgentCore.Application.Workflows;

public static class WorkflowCausationRules
{
    public static WorkflowEventCausation FromRoot(AgentRun root)
    {
        var activation = root.Admission.Activation;
        // Only an occurrence-qualified activation carries trusted event metadata.
        // User, initiative and message evidence never becomes causal authority by resembling its JSON shape.
        if (activation.TriggerOccurrenceId is null || activation.Kind is not (ActivationKind.ScheduledWork or ActivationKind.ApplicationEvent or ActivationKind.CoreEvent or ActivationKind.ManualBackground))
            return new(root.AgentRunId, 0, []);
        return FromEvidence(activation.EvidenceJson, root.AgentRunId);
    }
    public static WorkflowEventCausation FromEvidence(string? evidence, Guid? fallbackRoot = null)
    {
        if (evidence is null) return new(fallbackRoot, 0, []);
        try
        {
            using var outer = JsonDocument.Parse(evidence);
            var json = outer.RootElement.ValueKind == JsonValueKind.Object && outer.RootElement.TryGetProperty("Text", out var text)
                && text.ValueKind == JsonValueKind.String ? text.GetString()! : evidence;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("triggerContext", out var context)
                || context.ValueKind != JsonValueKind.Object || !context.TryGetProperty("causation", out var cause) || cause.ValueKind != JsonValueKind.Object) return new(fallbackRoot, 0, []);
            var root = cause.TryGetProperty("rootAgentRunId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetGuid() : fallbackRoot;
            var result = new WorkflowEventCausation(root, cause.GetProperty("triggerDepth").GetInt32(),
                cause.GetProperty("visitedAutomationIds").EnumerateArray().Select(i => i.GetGuid()).ToImmutableArray());
            result.Validate(); return result;
        }
        catch (JsonException) { return new(fallbackRoot, 0, []); } // Retained non-event root evidence may be plain text.
    }
}
