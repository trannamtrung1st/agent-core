using AgentCore.Domain.Conversation;
using System.Text.Json.Serialization;

namespace AgentCore.Domain.Triggers;

public enum ExecutionModelSource
{
    TriggerOverride = 0,
    UnattendedDefault = 1,
    ConversationDefault = 2
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ExecutionModelPin
{
    public ExecutionModelPin(
        string offeringId,
        string connectionId,
        string externalModelId,
        string? reasoningEffort,
        ExecutionModelSource source,
        AgentCore.Domain.Models.ModelBinding? binding = null, AgentCore.Domain.Models.ModelDecisionEvidence? decision = null)
    {
        if (!Enum.IsDefined(source))
        {
            throw new ArgumentException("Execution model source is not valid.", nameof(source));
        }

        OfferingId = AgentRunText.RequireToken(offeringId, AgentRunLimits.MaxModelFieldCharacters, "Model catalog key");
        ConnectionId = AgentRunText.RequireToken(connectionId, AgentRunLimits.MaxModelFieldCharacters, "Model provider");
        ExternalModelId = AgentRunText.RequireToken(externalModelId, AgentRunLimits.MaxExternalModelIdCharacters, "External model ID");
        ReasoningEffort = reasoningEffort is null
            ? null
            : AgentRunText.RequireToken(reasoningEffort, AgentRunLimits.MaxReasoningEffortCharacters, "Reasoning effort");
        Source = source;
        binding?.RequireIdentity(OfferingId, ConnectionId, ExternalModelId);
        Binding = binding;
        Decision = decision;
    }

    public string OfferingId { get; }

    public string ConnectionId { get; }

    public string ExternalModelId { get; }

    public string? ReasoningEffort { get; }

    public ExecutionModelSource Source { get; }

    public AgentCore.Domain.Models.ModelDecisionEvidence? Decision { get; }
    public AgentCore.Domain.Models.ModelBinding? Binding { get; }

    public static string ToWire(ExecutionModelSource source) =>
        source switch
        {
            ExecutionModelSource.TriggerOverride => "triggerOverride",
            ExecutionModelSource.UnattendedDefault => "unattendedDefault",
            ExecutionModelSource.ConversationDefault => "conversationDefault",
            _ => "conversationDefault"
        };
}
