namespace AgentCore.Application.Ports;

public sealed record TenantBrowserArtifactReference(string Kind, Guid ArtifactId);
public sealed record TenantBrowserArtifactResult(string Status, string? ReasonCode, IReadOnlyList<TenantBrowserArtifactReference> Files);
public sealed record TenantBrowserArtifactFile(string Kind, Guid ArtifactId, string DisplayName, string ContentType, ReadOnlyMemory<byte> Content);
public sealed record TenantBrowserArtifactBatch(IReadOnlyList<TenantBrowserArtifactFile> Files, string? UnavailableReason = null);
