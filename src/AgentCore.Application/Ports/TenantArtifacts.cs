namespace AgentCore.Application.Ports;

// Storage ownership only. Publishers must separately authorize their protected effect;
// this port does not grant permission, approve a tool or expose a model publication path.
public interface ITenantArtifactStore
{
    ValueTask<ArtifactRecord> CreateTenantAsync(TenantRuntimeFrame frame, Guid artifactId, string displayName,
        string contentType, ReadOnlyMemory<byte> bytes, CancellationToken ct = default);
    ValueTask<ArtifactRecord?> FindTenantAsync(Guid tenant, Guid session, Guid artifact, CancellationToken ct = default);
    ValueTask<ArtifactPage> ListTenantAsync(Guid tenant, Guid session, IReadOnlyList<Guid>? allowed,
        Guid? before, int limit, Guid? run = null, CancellationToken ct = default);
    ValueTask<byte[]> ReadTenantContentAsync(Guid tenant, Guid session, Guid artifact, CancellationToken ct = default);
}
