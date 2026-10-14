namespace AgentCore.Contracts.Http;

public sealed record SandboxProfileResponse(string Id, long Revision, string Name, string Description, bool Enabled,
    string ImageReference, string? InitializationScript, double Cpu, int MemoryMiB, int Pids, int MaxCommandSeconds,
    int IdleTtlSeconds, int MaxLifetimeSeconds, long StorageBytes, DateTimeOffset? LastVerifiedAt, string? VerificationReason);
public sealed record SaveSandboxProfileRequest(long ExpectedRevision, string Name, string Description, bool Enabled,
    string ImageReference, string? InitializationScript = null, double Cpu = 1, int MemoryMiB = 1024, int Pids = 64,
    int MaxCommandSeconds = 120, int IdleTtlSeconds = 1800, int MaxLifetimeSeconds = 7200, long StorageBytes = 250L * 1024 * 1024);
public sealed record SandboxPlatformResponse(string State, string Message, IReadOnlyList<SandboxProfileResponse> Profiles,
    IReadOnlyList<string> AllowedImagePrefixes, bool LocalImageIdsAllowed, string SecurityScope, string StorageScope);
public sealed record SandboxVerificationResponse(bool Passed, string Message, SandboxProfileResponse Profile);
public sealed record VerifySandboxProfileRequest(long ExpectedRevision);
public sealed record ResetSandboxRequest(Guid ExpectedGeneration);
