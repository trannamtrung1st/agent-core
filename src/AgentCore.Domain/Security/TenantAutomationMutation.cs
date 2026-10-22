using System.Text.Json.Serialization;
using System.Text.Json;
using AgentCore.Domain.Triggers;

namespace AgentCore.Domain.Security;

// Exact existing-resource authoring data; identity, intent and witnesses come from Core.
public sealed record PreparedAutomationMutation(Guid SessionId, Guid RootSessionId, string ToolName, string ArgumentsJson,
    [property: JsonConverter(typeof(PreparedAutomationJsonConverter))] Automation? Current,
    [property: JsonConverter(typeof(PreparedAutomationJsonConverter))] Automation Proposed,
    string IntentHash, bool SemanticReviewRequired,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedAutomationInvocation? Invocation = null)
{
    public bool Equals(PreparedAutomationMutation? other) => other is not null && JsonSerializer.Serialize(this) == JsonSerializer.Serialize(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(JsonSerializer.Serialize(this));
}

// Core-selected identities and witnesses for one manual occurrence, never model input.
public sealed record PreparedAutomationInvocation(Guid SessionId, Guid EntryId, Guid ActivationId, Guid RunId, Guid ResponseId,
    string ConfigurationHash, ExecutionModelPin Model,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedAutomationConversation? Conversation = null);

// Content-free, immutable evidence for known same-task changes to an observed parent.
public sealed record TenantAutomationMutationReceipt(Guid AutomationId, string ToolName, long ExpectedRevision, long Revision,
    string? BeforeHash, string AfterHash,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? InvocationOccurrenceId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? InvocationSessionId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? InvocationRunId = null);
