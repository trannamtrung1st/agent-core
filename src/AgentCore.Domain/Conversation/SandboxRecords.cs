using AgentCore.Domain.Definitions;

namespace AgentCore.Domain.Conversation;

public enum SandboxLeaseState { Provisioning, Ready, Executing, NeedsReconciliation, Terminating, Terminated, Failed }
public enum SandboxExecutionState { Dispatching, Running, Completed, Canceled, Indeterminate }

public sealed record SandboxMountAuthority(string WorkingIdentity, string? HomeIdentity, HomeAccess HomeAccess, int Uid, int Gid, bool TranslatedOwnership = false, int CompatibilityContract = 0);

// Provider identities are opaque server-side values; public projections omit them.
public sealed record SandboxLease(Guid SessionId, Guid AgentInstanceId, Guid Generation, long Revision,
    SandboxProfile Profile, SandboxLeaseState State, string? ProviderId, DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt, DateTimeOffset? InitializedAt = null, SandboxMountAuthority? MountAuthority = null,
    Guid? ActiveExecutionId = null, string? DiagnosticId = null, IReadOnlyList<string>? BaselineProcesses = null, Guid? LastExecutionId = null, string? SafeMessage = null, SandboxInitializationEvidence? Initialization = null,
    DateTimeOffset? LastActivityAt = null, string? CleanupReason = null, int CleanupPasses = 0, DateTimeOffset? LastCleanupAttemptAt = null, string? SelectedProfileId = null, string? TransitionProfileId = null, long? TransitionProfileRevision = null, string? TransitionPhase = null, Guid? AdmissionRunId = null, string? AdmissionToolCallId = null, string? FailureCategory = null, string? FailurePhase = null,
    Guid? TenantId = null, Guid? ExecutorPrincipalId = null, string? ProviderHostScope = null);

public sealed record SandboxExecution(Guid Id, Guid SessionId, Guid AgentInstanceId, Guid Generation,
    Guid AgentRunId, Guid ClaimGeneration, string ToolCallId, string ActionHash, SandboxExecutionState State,
    DateTimeOffset StartedAt, string? ProviderCommandId = null, DateTimeOffset? FinishedAt = null,
    int? ExitCode = null, string Stdout = "", string Stderr = "", bool Truncated = false,
    bool FilesystemEffectsMayHaveOccurred = false, string? DiagnosticId = null, bool Background = false, string CommandText = "", long? DurationMs = null, DateTimeOffset? DeadlineAt = null, string? ProfileId = null, long? ProfileRevision = null,
    string? Phase = null, string? FailureCategory = null, string? CancellationReason = null, bool TerminationConfirmed = false,
    bool GenerationRecycled = false, bool RemoteTerminationConfirmed = false, Guid? TenantId = null, Guid? ExecutorPrincipalId = null, long Revision = 0, Guid? ApprovalRequestId = null, Guid? OperationId = null, bool InitializesGeneration = false, DateTimeOffset? CommandStartedAt = null);

// Verification is an observation of one exact immutable configuration revision.
public sealed record SandboxProfileVerification(string ProfileId, long ProfileRevision,
    DateTimeOffset VerifiedAt, bool Passed, string Message, string? FailureCode = null, int? ExitCode = null, string Stdout = "", string Stderr = "", bool Truncated = false, long DurationMs = 0, string? DiagnosticId = null, int ContractVersion = 0, Guid? RegistryCredentialId = null, long? RegistryCredentialRevision = null, long? RegistrySecretRevision = null, Guid? TenantId = null, bool IncludesInitialization = true, string? HostScope = null, Guid? InstanceId = null, long? InstanceRevision = null)
{
    public const int CurrentContractVersion = 4;
}

public sealed record SandboxInitializationEvidence(string State, string ProfileId, long ProfileRevision, long? InstanceRevision,
    DateTimeOffset? StartedAt = null, DateTimeOffset? FinishedAt = null, int? ExitCode = null,
    string Stdout = "", string Stderr = "", bool Truncated = false, long DurationMs = 0, string? DiagnosticId = null, string? Phase = null);

public sealed record SandboxInstanceVerification(Guid InstanceId, long InstanceRevision, SandboxProfileVerification Result,
    IReadOnlyList<SandboxProfileVerification>? Observations = null)
{
    public SandboxInstanceVerification Merge(SandboxInstanceVerification? previous)
    {
        if (previous is null) return this;
        var observations = (previous.Observations ?? [previous.Result]).Where(p => !SameKey(p, Result)).Append(Result).TakeLast(16).ToArray();
        return observations.Length == 1 ? this : this with { Observations = observations };
    }
    public SandboxInstanceVerification? For(SandboxProfile profile)
    {
        var result = (Observations ?? [Result]).LastOrDefault(p => p.ProfileId == profile.Id && p.ProfileRevision == profile.Revision
            && p.RegistryCredentialId == profile.RegistryCredentialId && p.RegistryCredentialRevision == profile.RegistryCredentialRevision && p.RegistrySecretRevision == profile.RegistrySecretRevision);
        return result is null ? null : new(InstanceId, InstanceRevision, result);
    }
    private static bool SameKey(SandboxProfileVerification a, SandboxProfileVerification b) => a.ProfileId == b.ProfileId && a.ProfileRevision == b.ProfileRevision
        && a.RegistryCredentialId == b.RegistryCredentialId && a.RegistryCredentialRevision == b.RegistryCredentialRevision && a.RegistrySecretRevision == b.RegistrySecretRevision;
}
