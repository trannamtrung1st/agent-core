using System.Text.Json.Serialization;
using System.Text.Json;
using AgentCore.Domain.Triggers;

namespace AgentCore.Domain.Security;

// Exact existing-resource authoring data; identity, intent and witnesses come from Core.
public sealed record PreparedAutomationMutation(Guid SessionId, Guid RootSessionId, string ToolName, string ArgumentsJson,
    [property: JsonConverter(typeof(PreparedAutomationJsonConverter))] Automation? Current,
    [property: JsonConverter(typeof(PreparedAutomationJsonConverter))] Automation Proposed,
    string IntentHash, bool SemanticReviewRequired)
{
    public bool Equals(PreparedAutomationMutation? other) => other is not null && JsonSerializer.Serialize(this) == JsonSerializer.Serialize(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(JsonSerializer.Serialize(this));
}

// Content-free, immutable evidence for known same-task changes to an observed parent.
public sealed record TenantAutomationMutationReceipt(Guid AutomationId, string ToolName, long ExpectedRevision, long Revision,
    string? BeforeHash, string AfterHash);
