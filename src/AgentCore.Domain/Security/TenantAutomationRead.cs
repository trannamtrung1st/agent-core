using AgentCore.Domain.Triggers;
namespace AgentCore.Domain.Security;

public sealed record TenantAutomationReadSource(Guid AutomationId, long Revision, string ConfigurationHash);
public sealed record TenantAutomationEventReadSource(Guid AutomationId, Guid EventId, Guid TriggerId, TriggerSourceKind Kind,
    Guid? ResourceId, Guid? EmailAccountId, string SourceHash);
public sealed record PreparedAutomationRead(Guid SessionId, Guid RootSessionId, string ToolName, string ArgumentsJson,
    string SourcesJson, string EventSourcesJson, string ObservationJson, string ObservationHash);
public sealed record TenantAutomationReadReference(Guid TenantId, string ToolName, string ArgumentsJson,
    string SourcesJson, string EventSourcesJson, string ObservationHash);
