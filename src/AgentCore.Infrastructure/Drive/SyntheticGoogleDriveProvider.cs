using System.Globalization;
using System.Security.Cryptography;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
namespace AgentCore.Infrastructure.Drive;

internal sealed class SyntheticGoogleDriveProvider(Guid credential, ISyntheticDriveStore store, IIdGenerator ids) : IGoogleDriveProvider
{
    public ValueTask<string> GenerateIdAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult("synthetic-" + ids.NewId().ToString("N")); }
    public async ValueTask<DriveFilePage> ListAsync(string? folder, string? query, string? cursor, int limit, CancellationToken ct)
    {
        var offset = 0; if (cursor is not null && (!int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0)) throw AgentCoreErrors.Validation("Invalid Drive cursor.");
        var all = (await store.ListAsync(credential, ct)).Where(file => !file.Trashed && (folder is null || file.Parents.Contains(folder)) && (query is null || file.Name.Contains(query, StringComparison.OrdinalIgnoreCase))).OrderBy(file => file.Id, StringComparer.Ordinal).ToArray();
        return new(all.Skip(offset).Take(limit).ToArray(), offset + limit < all.Length ? (offset + limit).ToString(CultureInfo.InvariantCulture) : null);
    }
    public async ValueTask<DriveFile> GetAsync(string id, CancellationToken ct) => await store.GetAsync(credential, id, ct) ?? throw new AgentCoreException("drive_not_found", "Drive file was not found in files available to Agent Core.", 404);
    private async ValueTask Parent(string id, CancellationToken ct) { if (id == "root") return; var file = await GetAsync(id, ct); if (!file.Folder || file.Trashed) throw AgentCoreErrors.Validation("Drive parent must be an available app-authorized folder."); }
    public async ValueTask CreateFolderAsync(string id, string name, string parent, CancellationToken ct) { await Parent(parent, ct); await store.SaveAsync(credential, new(id, name, "application/vnd.google-apps.folder", 0, "1", null, [parent], false), [], null, ct); }
    public async ValueTask UploadAsync(string id, string name, string parent, string mime, ReadOnlyMemory<byte> bytes, DriveOperation operation, CancellationToken ct) { await Parent(parent, ct); if (bytes.Length > GoogleDriveOptions.MaxTransferBytes) throw AgentCoreErrors.Validation("Drive file exceeds 50 MiB."); await store.SaveAsync(credential, new(id, name, mime, bytes.Length, "1", Convert.ToHexStringLower(MD5.HashData(bytes.Span)), [parent], false), bytes.ToArray(), null, ct); }
    public ValueTask<byte[]> DownloadAsync(DriveFile file, CancellationToken ct) => store.ReadAsync(credential, file.Id, ct);
    public async ValueTask<byte[]> DownloadAsync(DriveFile file, int maxBytes, CancellationToken ct)
    {
        if (maxBytes is <= 0 or > GoogleDriveOptions.MaxTransferBytes) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (file.ByteSize > maxBytes) throw new AgentCoreException("preview_too_large", "Drive file exceeds its effective preview limit. Download the original.", 413);
        var bytes = await store.ReadAsync(credential, file.Id, ct);
        if (bytes.Length > maxBytes) throw new AgentCoreException("preview_too_large", "Drive file exceeds its effective preview limit. Download the original.", 413);
        return bytes;
    }
    private static string Next(DriveFile file) => (long.Parse(file.Version, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture);
    public async ValueTask RenameAsync(string id, string name, CancellationToken ct) { var file = await GetAsync(id, ct); await store.SaveAsync(credential, file with { Name = name, Version = Next(file) }, null, file.Version, ct); }
    public async ValueTask MoveAsync(string id, string source, string destination, CancellationToken ct) { await Parent(destination, ct); var file = await GetAsync(id, ct); if (!file.Parents.Contains(source) || id == destination) throw AgentCoreErrors.Conflict("Drive parent changed or destination is invalid."); await store.SaveAsync(credential, file with { Parents = [destination], Version = Next(file) }, null, file.Version, ct); }
    public async ValueTask TrashAsync(string id, CancellationToken ct) { var file = await GetAsync(id, ct); if (file.Folder) throw AgentCoreErrors.Validation("Folder trash is unsupported. Select one file."); await store.SaveAsync(credential, file with { Trashed = true, Version = Next(file) }, null, file.Version, ct); }
}
