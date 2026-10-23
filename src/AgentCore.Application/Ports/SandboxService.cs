using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Application.Admin;

namespace AgentCore.Application.Ports;

public sealed record SandboxCommandRequest(string? Command, IReadOnlyList<string>? Argv = null,
    string Cwd = "/working", int? TimeoutSeconds = null, bool Background = false, string? ProfileId = null);
public sealed record SandboxCommandResult(Guid ExecutionId, string Status, int? ExitCode, string Stdout,
    string Stderr, bool Truncated, long DurationMs, bool FilesystemEffectsMayHaveOccurred, string? DiagnosticId = null, bool Background = false, string CommandText = "", string? ProfileId = null, long? ProfileRevision = null, Guid? Generation = null,
    string? Phase = null, string? FailureCategory = null, string? CancellationReason = null, bool TerminationConfirmed = false, string? Error = null, string? Recovery = null, string? EnvironmentState = null, Guid? EnvironmentGeneration = null,
    bool GenerationRecycled = false, bool RemoteTerminationConfirmed = false);
public sealed record SandboxEnvironmentInfo(Guid SessionId, Guid Generation, string State, string ProfileId,
    long ProfileRevision, DateTimeOffset ExpiresAt, Guid? ActiveExecutionId, string? DiagnosticId, Guid? LastExecutionId = null, string? Message = null,
    DateTimeOffset? CreatedAt = null, DateTimeOffset? LastActivityAt = null, DateTimeOffset? MaximumLifetimeAt = null,
    string? CleanupReason = null, bool CleanupPending = false, int CleanupPasses = 0, DateTimeOffset? LastCleanupAttemptAt = null,
    SandboxInitializationEvidence? Initialization = null, string? ProfileName = null, string? ImageReference = null, string? SelectedProfileId = null, string? TransitionProfileId = null, string? TransitionPhase = null, string? FailureCategory = null, string? FailurePhase = null, Guid? AgentRunId = null, string? HomeAccess = null, string StorageEnforcement = "sampled");

public enum SandboxStopIntent { SessionLifecycle, OwnerReset, PolicyChanged }

public interface ISandboxService
{
    ValueTask StopSessionAsync(Guid trustedSessionId, CancellationToken ct = default, Guid? expectedGeneration = null, SandboxStopIntent intent = SandboxStopIntent.SessionLifecycle);
    ValueTask StopSessionAsync(Guid trustedSessionId, CancellationToken ct, Guid? expectedGeneration, Guid? expectedExecutionId, bool checkExecutionIdentity, SandboxStopIntent intent = SandboxStopIntent.SessionLifecycle)
        => StopSessionAsync(trustedSessionId, ct, expectedGeneration, intent);
    ValueTask ReconcileAsync(CancellationToken ct = default);
}

public interface ISandboxStore
{
    ValueTask<ResourceStateObservation> ReadStateAsync(AgentRunOwner owner, DateTimeOffset observedAt, CancellationToken ct = default) => throw new NotSupportedException();
    ValueTask<ResourceStateObservation> ReadSessionStateAsync(AgentRunOwner owner, Guid sessionId, DateTimeOffset observedAt, CancellationToken ct = default) => throw new NotSupportedException();
    ValueTask<IReadOnlyList<SandboxProfile>> ListProfilesAsync(CancellationToken ct = default);
    ValueTask<SandboxProfile?> GetProfileAsync(string id, long? revision = null, CancellationToken ct = default);
    ValueTask SaveProfileAsync(SandboxProfile profile, long expectedRevision, CancellationToken ct = default, AdminEventAppend? history = null);
    ValueTask<SandboxProfileVerification?> GetVerificationAsync(string id, long revision, CancellationToken ct = default);
    ValueTask SaveVerificationAsync(SandboxProfileVerification verification, CancellationToken ct = default);
    ValueTask<SandboxInstanceVerification?> GetInstanceVerificationAsync(Guid instanceId, long revision, CancellationToken ct = default);
    async ValueTask<SandboxInstanceVerification?> GetInstanceVerificationAsync(Guid instanceId, long revision, SandboxProfile profile, CancellationToken ct = default)
        => (await GetInstanceVerificationAsync(instanceId, revision, ct))?.For(profile);
    ValueTask SaveInstanceVerificationAsync(SandboxInstanceVerification verification, CancellationToken ct = default);
    ValueTask<SandboxLease?> GetLeaseAsync(Guid sessionId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<SandboxLease>> ListLeasesAsync(CancellationToken ct = default);
    ValueTask SaveLeaseAsync(SandboxLease lease, long expectedRevision, CancellationToken ct = default);
    ValueTask<SandboxExecution?> GetExecutionAsync(Guid sessionId, Guid executionId, CancellationToken ct = default);
    ValueTask<SandboxExecution?> GetOperationAsync(Guid sessionId, Guid agentRunId, string toolCallId, CancellationToken ct = default);
    ValueTask SaveExecutionAsync(SandboxExecution execution, bool create, CancellationToken ct = default);
}
