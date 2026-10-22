namespace AgentCore.Domain.Security;

public sealed record PreparedManagementRead(Guid SessionId, Guid RootSessionId, string ToolName, string ArgumentsJson,
    long InstanceRevision, string ConfigurationHash, string StableConfigurationHash, string ObservationJson, string ObservationHash);
public sealed record TenantManagementReadReference(Guid TenantId, string ToolName, string ArgumentsJson,
    long InstanceRevision, string ConfigurationHash, string StableConfigurationHash, string ObservationHash);
