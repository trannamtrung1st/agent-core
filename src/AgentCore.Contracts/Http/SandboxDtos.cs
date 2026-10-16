using System.Text.Json.Serialization;

namespace AgentCore.Contracts.Http;

public sealed record SandboxProfileResponse(string Id, long Revision, string Name, string Description, bool Enabled,
    string ImageReference, string? InitializationScript, double Cpu, int MemoryMiB, int Pids, int MaxCommandSeconds,
    int IdleTtlSeconds, int MaxLifetimeSeconds, long StorageBytes, DateTimeOffset? LastVerifiedAt, string? VerificationReason, SandboxSetupVerificationResponse? Verification = null, Guid? RegistryCredentialId = null, string Availability = "NeedsVerification", string? AvailabilityMessage = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SaveSandboxProfileRequest(long ExpectedRevision, string Name, string Description, bool Enabled,
    string ImageReference, string? InitializationScript = null, double Cpu = 1, int MemoryMiB = 1024, int Pids = 64,
    int MaxCommandSeconds = 120, int IdleTtlSeconds = 1800, int MaxLifetimeSeconds = 7200, long StorageBytes = 250L * 1024 * 1024, Guid? RegistryCredentialId = null);
public sealed record SandboxPlatformResponse(string State, string Message, IReadOnlyList<SandboxProfileResponse> Profiles,
    IReadOnlyList<string> AllowedImagePrefixes, bool LocalImageIdsAllowed, string SecurityScope, string StorageScope, int WorkspaceContractVersion = 3, int VerificationContractVersion = 4, long MaxWorkingStorageBytes = 16L * 1024 * 1024 * 1024);
public sealed record SandboxVerificationResponse(bool Passed, string Message, SandboxProfileResponse Profile, int? ExitCode = null, string Stdout = "", string Stderr = "", bool Truncated = false, long DurationMs = 0, string? DiagnosticId = null);
public sealed record VerifySandboxProfileRequest(long ExpectedRevision);
public sealed record ResetSandboxRequest(Guid ExpectedGeneration, Guid? ExpectedExecutionId = null);

public sealed record VerifyInstanceSandboxRequest(long ExpectedInstanceRevision, string? ProfileId = null);
public sealed record SandboxSetupVerificationResponse(bool Passed, string Message, string ProfileId, long ProfileRevision, long? InstanceRevision,
    DateTimeOffset VerifiedAt, int? ExitCode, string Stdout, string Stderr, bool Truncated, long DurationMs, string? DiagnosticId);
