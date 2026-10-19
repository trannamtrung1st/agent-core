namespace AgentCore.Domain.Definitions;

// Instance-owned factual reset state. It is never permission to use or reset a Browser.
public sealed record AgentBrowserProfileReset(Guid OperationId, Guid PrincipalId, Guid ProviderEpoch, string Witness,
    string Status, string ReasonCode, DateTimeOffset RequestedAt, DateTimeOffset? RecordedAt = null);
