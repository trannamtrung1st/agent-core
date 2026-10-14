namespace AgentCore.Contracts.Http;

public sealed record SandboxProfileResponse(string Id, long Revision, string Name, string Description, bool Enabled,
    string ImageReference, string? InitializationScript, double Cpu, int MemoryMiB, int Pids, int MaxCommandSeconds,
    int IdleTtlSeconds, int MaxLifetimeSeconds, long StorageBytes, DateTimeOffset? LastVerifiedAt, string? VerificationReason, SandboxSetupVerificationResponse? Verification = null);
public sealed record SaveSandboxProfileRequest(long ExpectedRevision, string Name, string Description, bool Enabled,
    string ImageReference, string? InitializationScript = null, double Cpu = 1, int MemoryMiB = 1024, int Pids = 64,
    int MaxCommandSeconds = 120, int IdleTtlSeconds = 1800, int MaxLifetimeSeconds = 7200, long StorageBytes = 250L * 1024 * 1024);
public sealed record SandboxPlatformResponse(string State, string Message, IReadOnlyList<SandboxProfileResponse> Profiles,
    IReadOnlyList<string> AllowedImagePrefixes, bool LocalImageIdsAllowed, string SecurityScope, string StorageScope);
public sealed record SandboxVerificationResponse(bool Passed, string Message, SandboxProfileResponse Profile, int? ExitCode = null, string Stdout = "", string Stderr = "", bool Truncated = false, long DurationMs = 0, string? DiagnosticId = null);
public sealed record VerifySandboxProfileRequest(long ExpectedRevision);
public sealed record ResetSandboxRequest(Guid ExpectedGeneration);

public sealed record VerifyInstanceSandboxRequest(long ExpectedInstanceRevision);
public sealed record SandboxSetupVerificationResponse(bool Passed, string Message, string ProfileId, long ProfileRevision, long? InstanceRevision,
    DateTimeOffset VerifiedAt, int? ExitCode, string Stdout, string Stderr, bool Truncated, long DurationMs, string? DiagnosticId);
