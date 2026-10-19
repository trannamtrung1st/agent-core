using System.Text.Json.Serialization;

namespace AgentCore.Domain.Security;

public enum TenantOperationOutcome { Completed, Denied, Indeterminate }

// An immutable effect result, not an authority token. No content or credential is copied into it.
public sealed record TenantOperationReceipt(Guid OperationId, Guid? RequestId, Guid TenantId, Guid RunId, Guid SessionId, Guid RootActivationId,
    Guid ExecutorPrincipalId, Guid InstanceId, string ActionHash, TenantOperationOutcome Outcome, string ReasonCode,
    long InstanceRevision, long PersonaRevision, DateTimeOffset RecordedAt, string? SourceCallId = null, string? ParametersHash = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BrowserArtifactsJson = null);
