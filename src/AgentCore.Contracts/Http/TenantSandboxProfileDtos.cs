using System.Text.Json.Serialization;

namespace AgentCore.Contracts.Http;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TenantSandboxProfileSaveRequest(long ExpectedRevision, long ExpectedProfileRevision,
    string Name, string Description, bool Enabled, string ImageReference, string? InitializationScript = null,
    double Cpu = 1, int MemoryMiB = 1024, int Pids = 64, int MaxCommandSeconds = 120, int IdleTtlSeconds = 1800,
    int MaxLifetimeSeconds = 7200, long StorageBytes = 250L * 1024 * 1024, Guid? RegistryCredentialId = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TenantSandboxVerificationRequest(long ExpectedRevision, long ExpectedProfileRevision);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TenantSandboxInstanceVerificationRequest(long ExpectedRevision, long ExpectedInstanceRevision, string ProfileId, long ExpectedProfileRevision);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TenantSandboxResetRequest(long ExpectedRevision, long ExpectedSessionRevision, Guid ExpectedGeneration,
    long ExpectedLeaseRevision, [property: JsonRequired] Guid? ExpectedExecutionId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TenantSandboxProcessStopRequest(long ExpectedRevision, long ExpectedSessionRevision, Guid ExpectedGeneration,
    long ExpectedLeaseRevision, long ExpectedExecutionRevision);
