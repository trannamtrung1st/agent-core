using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Credentials;

namespace AgentCore.Infrastructure.Credentials;

public sealed partial class TenantConnectedAccountReader
{
    // Synthetic adapters and native callbacks share the same per-call authority boundary.
    // Real Google callbacks additionally repeat it before each transport retry/chunk.
    private sealed class ScopedDrive(IGoogleDriveProvider inner, Func<CancellationToken, ValueTask<Credential>> current) : IGoogleDriveProvider
    {
        private async ValueTask<T> Read<T>(Func<ValueTask<T>> action, CancellationToken ct)
        {
            await current(ct).ConfigureAwait(false);
            var value = await action().ConfigureAwait(false);
            await current(ct).ConfigureAwait(false); return value;
        }
        private async ValueTask Write(Func<ValueTask> action, CancellationToken ct)
        {
            try { await current(ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested(); }
            catch (AccessDeniedException e) { throw new DriveEffectNotExecutedException(e.ReasonCode, "Current account access refused dispatch."); }
            catch (SecurityConflictException e) { throw new DriveEffectNotExecutedException(e.ReasonCode, "The account source changed before dispatch."); }
            catch (OperationCanceledException) { throw new DriveEffectNotExecutedException("drive_cancelled_before_dispatch", "The write was cancelled before dispatch."); }
            await action().ConfigureAwait(false);
        }
        public ValueTask<string> GenerateIdAsync(CancellationToken ct) => Read(() => inner.GenerateIdAsync(ct), ct);
        public ValueTask<DriveFilePage> ListAsync(string? folder, string? query, string? cursor, int limit, CancellationToken ct) => Read(() => inner.ListAsync(folder, query, cursor, limit, ct), ct);
        public ValueTask<DriveFile> GetAsync(string id, CancellationToken ct) => Read(() => inner.GetAsync(id, ct), ct);
        public ValueTask<byte[]> DownloadAsync(DriveFile file, CancellationToken ct) => Read(() => inner.DownloadAsync(file, ct), ct);
        public ValueTask<byte[]> DownloadAsync(DriveFile file, int maxBytes, CancellationToken ct) => Read(() => inner.DownloadAsync(file, maxBytes, ct), ct);
        public ValueTask CreateFolderAsync(string id, string name, string parent, CancellationToken ct) => Write(() => inner.CreateFolderAsync(id, name, parent, ct), ct);
        public ValueTask UploadAsync(string id, string name, string parent, string mime, ReadOnlyMemory<byte> bytes, DriveOperation operation, CancellationToken ct) => Write(() => inner.UploadAsync(id, name, parent, mime, bytes, operation, ct), ct);
        public ValueTask RenameAsync(string id, string name, CancellationToken ct) => Write(() => inner.RenameAsync(id, name, ct), ct);
        public ValueTask MoveAsync(string id, string source, string destination, CancellationToken ct) => Write(() => inner.MoveAsync(id, source, destination, ct), ct);
        public ValueTask TrashAsync(string id, CancellationToken ct) => Write(() => inner.TrashAsync(id, ct), ct);
    }
}
