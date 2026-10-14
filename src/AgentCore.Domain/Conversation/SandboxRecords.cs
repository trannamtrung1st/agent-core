using AgentCore.Domain.Definitions;

namespace AgentCore.Domain.Conversation;

public enum SandboxLeaseState { Provisioning, Ready, Executing, NeedsReconciliation, NeedsResync, Terminating, Terminated, Failed }
public enum SandboxExecutionState { Dispatching, Running, Completed, Canceled, Indeterminate }

// Provider identities are opaque server-side values; public projections omit them.
public sealed record SandboxLease(Guid SessionId, Guid AgentInstanceId, Guid Generation, long Revision,
    SandboxProfile Profile, SandboxLeaseState State, string? ProviderId, DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt, DateTimeOffset? InitializedAt = null, string? ScratchFingerprint = null,
    Guid? ActiveExecutionId = null, string? DiagnosticId = null, IReadOnlyList<string>? BaselineProcesses = null, Guid? LastExecutionId = null);

public sealed record SandboxExecution(Guid Id, Guid SessionId, Guid AgentInstanceId, Guid Generation,
    Guid AgentRunId, Guid ClaimGeneration, string ToolCallId, string ActionHash, SandboxExecutionState State,
    DateTimeOffset StartedAt, string? ProviderCommandId = null, DateTimeOffset? FinishedAt = null,
    int? ExitCode = null, string Stdout = "", string Stderr = "", bool Truncated = false,
    string WorkspaceSyncStatus = "pending", string? DiagnosticId = null, bool Background = false, string CommandText = "", long? DurationMs = null);
