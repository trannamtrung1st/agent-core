namespace AgentCore.Domain.Security;

public sealed record PreparedManagementRead(Guid SessionId, Guid RootSessionId, string ToolName, string ArgumentsJson,
    long InstanceRevision, string ConfigurationHash, string StableConfigurationHash, string ObservationJson, string ObservationHash,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? SourcesJson = null);
public sealed record TenantManagementReadReference(Guid TenantId, string ToolName, string ArgumentsJson,
    long InstanceRevision, string ConfigurationHash, string StableConfigurationHash, string ObservationHash,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? SourcesJson = null);

// A metadata read never supplies child authority. The owning aggregate rechecks each witness.
public sealed record TenantManagementChildRead(string Kind, string Key, string Fingerprint, string ResourceId);
