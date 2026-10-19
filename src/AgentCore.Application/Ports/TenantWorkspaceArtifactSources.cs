namespace AgentCore.Application.Ports;

// Private source evidence, not authority. Callers separately require current IAM, ownership and a live Run/consent.
public sealed record TenantWorkspaceArtifactObservation(TenantWorkspaceReference Owner, string Path, string ContentType,
    long ByteSize, string Sha256Hex, string ObservationHash);

public interface ITenantWorkspaceArtifactSource
{
    TenantWorkspaceArtifactObservation Observation { get; }
    byte[] CopyContent();
    // Filesystem-only: must not reenter IAM/database owners from inside publication transactions/locks.
    ValueTask RequireCurrentAsync(CancellationToken ct = default);
}

public interface ITenantWorkspaceArtifactSourceStore
{
    ValueTask<ITenantWorkspaceArtifactSource> CaptureArtifactSourceTenantAsync(TenantWorkspaceReference owner, string path, CancellationToken ct = default);
}
