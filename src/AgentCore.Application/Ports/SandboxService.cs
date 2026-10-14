using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Application.Admin;

namespace AgentCore.Application.Ports;

public sealed record SandboxAccess(Guid SessionId, Guid AgentInstanceId, Guid AgentRunId, Guid ClaimGeneration,
    string ToolCallId, SandboxProfile Profile, AgentDefinition Definition);
public sealed record SandboxCommandRequest(string? Command, IReadOnlyList<string>? Argv = null,
    string Cwd = "/working", int TimeoutSeconds = 120, bool Background = false);
public sealed record SandboxCommandResult(Guid ExecutionId, string Status, int? ExitCode, string Stdout,
    string Stderr, bool Truncated, long DurationMs, string WorkspaceSyncStatus, string? DiagnosticId = null, bool Background = false, string CommandText = "");
public sealed record SandboxEnvironmentInfo(Guid SessionId, Guid Generation, string State, string ProfileId,
    long ProfileRevision, DateTimeOffset ExpiresAt, Guid? ActiveExecutionId, string? DiagnosticId, Guid? LastExecutionId = null, string? Message = null,
    DateTimeOffset? CreatedAt = null, DateTimeOffset? LastActivityAt = null, DateTimeOffset? MaximumLifetimeAt = null,
    string? CleanupReason = null, bool CleanupPending = false, int CleanupPasses = 0, DateTimeOffset? LastCleanupAttemptAt = null,
    SandboxInitializationEvidence? Initialization = null, string? ProfileName = null, string? ImageReference = null);
public sealed record ScratchSnapshot(WorkspaceTransfer Transfer, string Fingerprint);

public interface ISandboxService
{
    ValueTask<SandboxCommandResult> ExecuteAsync(SandboxAccess access, SandboxCommandRequest command, CancellationToken ct = default);
    ValueTask<SandboxCommandResult> InspectProcessAsync(Guid trustedSessionId, Guid processId, CancellationToken ct = default);
    ValueTask<SandboxCommandResult> StopProcessAsync(Guid trustedSessionId, Guid processId, CancellationToken ct = default);
    ValueTask<SandboxEnvironmentInfo?> InspectAsync(Guid trustedSessionId, CancellationToken ct = default);
    ValueTask StopSessionAsync(Guid trustedSessionId, CancellationToken ct = default, Guid? expectedGeneration = null);
    ValueTask ReconcileAsync(CancellationToken ct = default);
}

public interface ISandboxStore
{
    ValueTask<IReadOnlyList<SandboxProfile>> ListProfilesAsync(CancellationToken ct = default);
    ValueTask<SandboxProfile?> GetProfileAsync(string id, long? revision = null, CancellationToken ct = default);
    ValueTask SaveProfileAsync(SandboxProfile profile, long expectedRevision, CancellationToken ct = default, AdminEventAppend? history = null);
    ValueTask<SandboxProfileVerification?> GetVerificationAsync(string id, long revision, CancellationToken ct = default);
    ValueTask SaveVerificationAsync(SandboxProfileVerification verification, CancellationToken ct = default);
    ValueTask<SandboxInstanceVerification?> GetInstanceVerificationAsync(Guid instanceId, long revision, CancellationToken ct = default);
    ValueTask SaveInstanceVerificationAsync(SandboxInstanceVerification verification, CancellationToken ct = default);
    ValueTask<SandboxLease?> GetLeaseAsync(Guid sessionId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<SandboxLease>> ListLeasesAsync(CancellationToken ct = default);
    ValueTask SaveLeaseAsync(SandboxLease lease, long expectedRevision, CancellationToken ct = default);
    ValueTask<SandboxExecution?> GetExecutionAsync(Guid sessionId, Guid executionId, CancellationToken ct = default);
    ValueTask<SandboxExecution?> GetOperationAsync(Guid sessionId, Guid agentRunId, string toolCallId, CancellationToken ct = default);
    ValueTask SaveExecutionAsync(SandboxExecution execution, bool create, CancellationToken ct = default);
}
