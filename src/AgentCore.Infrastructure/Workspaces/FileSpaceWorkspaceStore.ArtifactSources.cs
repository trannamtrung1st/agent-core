using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Workspaces;

public sealed partial class FileSpaceWorkspaceStore : ITenantWorkspaceArtifactSourceStore
{
    public ValueTask<ITenantWorkspaceArtifactSource> CaptureArtifactSourceTenantAsync(TenantWorkspaceReference owner, string path, CancellationToken ct = default) =>
        CaptureSource(owner, path, ArtifactLimits.MaxBytesEach, ct);
    public ValueTask<ITenantWorkspaceArtifactSource> CaptureResourceSourceTenantAsync(TenantWorkspaceReference owner, string path, CancellationToken ct = default) =>
        CaptureSource(owner, path, AgentCore.Domain.Definitions.AgentResourceLimits.MaxItemBytes, ct);
    private async ValueTask<ITenantWorkspaceArtifactSource> CaptureSource(TenantWorkspaceReference owner, string path, long maximum, CancellationToken ct)
    {
        path = Concrete(owner, path);
        var files = await TenantFiles(owner, ct).ConfigureAwait(false);
        var bound = Math.Min(maxFileBytes, Math.Min(AgentWorkspaceLimits.MaxFileBytes, maximum));
        var captured = await files.CaptureArtifactSourceAsync(path, bound, true, ct).ConfigureAwait(false);
        await TenantFiles(owner, ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested();
        var source = new CapturedArtifactSource(files, path, bound, captured, new(owner, path, WorkspaceContentTypes.Of(path),
            captured.ByteSize, captured.Sha256Hex, captured.ObservationHash));
        await source.RequireCurrentAsync(ct).ConfigureAwait(false);
        return source;
    }
    private sealed class CapturedArtifactSource(CanonicalWorkspaceFilesystem files, string path, long bound,
        CanonicalWorkspaceFilesystem.ArtifactSource captured, TenantWorkspaceArtifactObservation observation) : ITenantWorkspaceArtifactSource
    {
        public TenantWorkspaceArtifactObservation Observation => observation;
        public byte[] CopyContent() => captured.Bytes.ToArray();
        public async ValueTask RequireCurrentAsync(CancellationToken ct = default)
        {
            try
            {
                var current = await files.CaptureArtifactSourceAsync(path, bound, false, ct).ConfigureAwait(false);
                if (current.ByteSize != captured.ByteSize || current.Sha256Hex != captured.Sha256Hex || current.ObservationHash != captured.ObservationHash)
                    throw new SecurityConflictException("workspace_source_changed");
            }
            catch (Exception e) when (e is AgentCoreException or IOException or UnauthorizedAccessException)
            { throw new SecurityConflictException("workspace_source_changed"); }
        }
    }
}
