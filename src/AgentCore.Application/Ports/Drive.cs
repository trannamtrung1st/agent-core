namespace AgentCore.Application.Ports;

public sealed record DriveFile(string Id, string Name, string MimeType, long ByteSize, string Version, string? Md5Checksum, IReadOnlyList<string> Parents, bool Trashed, string? WebViewLink = null)
{
    public bool Folder => MimeType == "application/vnd.google-apps.folder";
    public bool NativeDocument => MimeType.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal) && !Folder;
}
public sealed record DriveFilePage(IReadOnlyList<DriveFile> Items, string? NextCursor, bool LimitedToAppAuthorizedFiles = true);
public sealed record DriveOperation(Guid OperationId, Guid AgentInstanceId, Guid CredentialId, string EffectHash, long AuthEpoch, string Tool,
    string FileId, string SnapshotJson, string State, long Revision, DateTimeOffset CreatedAtUtc, string? ResultJson = null, string? ProtectedUploadSession = null);
// An adapter may report this only when no effect request was sent or its initial
// request was explicitly rejected. Absence/timeouts are never this evidence.
public sealed class DriveEffectNotExecutedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
public sealed record DriveOperationFailure(string Code, string Message, string Proof, DriveFile? VerifiedFile = null);
public interface IDriveOperationStore
{
    ValueTask<DriveOperation?> GetAsync(Guid owner, Guid operationId, CancellationToken ct);
    ValueTask<DriveOperation?> FindAsync(Guid owner, Guid credential, string effectHash, CancellationToken ct);
    ValueTask<DriveOperation> ClaimAsync(DriveOperation operation, CancellationToken ct);
    ValueTask<DriveOperation> UpdateAsync(DriveOperation operation, long expectedRevision, CancellationToken ct);
    ValueTask DeleteOwnerAsync(Guid instanceId, CancellationToken ct);
}
public interface IGoogleDriveProvider
{
    ValueTask<string> GenerateIdAsync(CancellationToken ct);
    ValueTask<DriveFilePage> ListAsync(string? folderId, string? query, string? cursor, int limit, CancellationToken ct);
    ValueTask<DriveFile> GetAsync(string fileId, CancellationToken ct);
    ValueTask CreateFolderAsync(string fileId, string name, string parentId, CancellationToken ct);
    ValueTask UploadAsync(string fileId, string name, string parentId, string mimeType, ReadOnlyMemory<byte> bytes, DriveOperation operation, CancellationToken ct);
    ValueTask<byte[]> DownloadAsync(DriveFile file, CancellationToken ct);
    /// <summary>Preview-specific byte ceiling, enforced before and during transfer. Original downloads retain their separate ceiling.</summary>
    ValueTask<byte[]> DownloadAsync(DriveFile file, int maxBytes, CancellationToken ct);
    ValueTask RenameAsync(string fileId, string name, CancellationToken ct);
    ValueTask MoveAsync(string fileId, string sourceParent, string destinationParent, CancellationToken ct);
    ValueTask TrashAsync(string fileId, CancellationToken ct);
}
public interface ISyntheticDriveStore
{
    ValueTask<DriveFile?> GetAsync(Guid credential, string id, CancellationToken ct);
    ValueTask<IReadOnlyList<DriveFile>> ListAsync(Guid credential, CancellationToken ct);
    ValueTask<byte[]> ReadAsync(Guid credential, string id, CancellationToken ct);
    ValueTask SaveAsync(Guid credential, DriveFile file, byte[]? bytes, string? expectedVersion, CancellationToken ct);
}
