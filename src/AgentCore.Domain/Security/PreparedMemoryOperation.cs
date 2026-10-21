namespace AgentCore.Domain.Security;

// Immutable value witnesses only. Actual User ownership and current source authority
// are reloaded through the stored causal Run, never supplied by a tool argument.
public sealed record PreparedMemorySource(Guid MemoryId, long Revision, string ContentHash);
public sealed record PreparedMemoryOperation(Guid SessionId, string ToolName, string ArgumentsJson, string SourcesJson,
    Guid? ResultMemoryId, string ObservationHash, Guid RootSessionId, Guid? UserPrincipalId, string SourceLabelsJson);
