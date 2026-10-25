using AgentCore.Domain.Models;
using System.Text.Json.Serialization;

namespace AgentCore.Domain.Conversation;

public enum ModelSelectionSource
{
    TenantDefault,
    AgentDefault,
    User,
    Host
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SessionModelSelection(
    string OfferingId,
    string ConnectionId,
    string ExternalModelId,
    ModelSelectionSource SelectionSource,
    string? ReasoningEffort,
    bool HasExplicitReasoningEffort = false,
    ModelBinding? Binding = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelGenerationProvenance(
    string OfferingId,
    string ConnectionId,
    string ExternalModelId,
    string? ReasoningEffort,
    ModelBinding? Binding = null);
