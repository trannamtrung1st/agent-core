using AgentCore.Application.Drive;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Credentials;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Contracts.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
namespace AgentCore.Api.Tests;

public sealed partial class DriveJourneyTests
{
    private sealed class Factory : AgentCoreApiFactory
    { protected override IReadOnlyDictionary<string, string?> ExtraConfiguration => new Dictionary<string, string?> { ["GoogleDriveOAuth:PublicAppUrl"] = "http://localhost" }; }
    private sealed class DisabledFactory : AgentCoreApiFactory
    { protected override IReadOnlyDictionary<string, string?> ExtraConfiguration => new Dictionary<string, string?> { ["GoogleDrive:Enabled"] = "false" }; }
    private sealed class LostFactory(IGoogleDriveProviderFactory inner) : IGoogleDriveProviderFactory
    {
        public int Uploads; public string Mode = "after"; public TaskCompletionSource? Release; public IDriveOperationStore? Operations;
        public string? DeniedParent; public int ParentReads, Trashes, Renames;
        public string? FailTrashId; public string TrashFailure = "none"; public bool FailTrashRead;
        public bool NormalizeUploadMime;
        public Func<string, CancellationToken, Task>? AfterTrash;
        public bool Enabled => inner.Enabled; public bool Configured => inner.Configured; public bool Synthetic => inner.Synthetic;
        public IGoogleDriveProvider Create(Guid id, long revision) => new LostProvider(inner.Create(id, revision), this);
    }
    private sealed class LostProvider(IGoogleDriveProvider inner, LostFactory owner) : IGoogleDriveProvider
    {
        public ValueTask<string> GenerateIdAsync(CancellationToken ct) => inner.GenerateIdAsync(ct);
        public ValueTask<DriveFilePage> ListAsync(string? folder, string? query, string? cursor, int limit, CancellationToken ct) => inner.ListAsync(folder, query, cursor, limit, ct);
        public async ValueTask<DriveFile> GetAsync(string id, CancellationToken ct)
        {
            if (id == owner.FailTrashId && owner.FailTrashRead && owner.Trashes > 0) throw new AgentCore.Application.Sessions.AgentCoreException("drive_not_found", "Target temporarily unavailable after PATCH.", 404);
            if (id == owner.DeniedParent) { owner.ParentReads++; throw new AgentCore.Application.Sessions.AgentCoreException("drive_not_found", "Parent unavailable under drive.file.", 404); }
            var file = await inner.GetAsync(id, ct);
            return owner.NormalizeUploadMime ? file with { MimeType = file.MimeType.Split(';')[0].Trim() } : file;
        }
        public ValueTask CreateFolderAsync(string id, string name, string parent, CancellationToken ct) => inner.CreateFolderAsync(id, name, parent, ct);
        public async ValueTask UploadAsync(string id, string name, string parent, string mime, ReadOnlyMemory<byte> bytes, DriveOperation op, CancellationToken ct)
        {
            owner.Uploads++;
            if (owner.Mode == "rejected") throw new DriveEffectNotExecutedException("drive_scope_limited", "Check the destination permission.");
            if (owner.Mode == "before") throw new HttpRequestException("unknown before creation");
            if (owner.Mode == "partial")
            {
                await owner.Operations!.UpdateAsync(op with { ProtectedUploadSession = "protected-test-session", Revision = op.Revision + 1 }, op.Revision, ct);
                throw new HttpRequestException("lost partial upload");
            }
            await inner.UploadAsync(id, name, parent, mime, bytes, op, ct);
            if (owner.Release is not null) await owner.Release.Task.WaitAsync(ct);
            if (owner.Mode == "after") throw new HttpRequestException("lost response");
        }
        public ValueTask<byte[]> DownloadAsync(DriveFile file, CancellationToken ct) => inner.DownloadAsync(file, ct);
        public ValueTask<byte[]> DownloadAsync(DriveFile file, int maxBytes, CancellationToken ct) => inner.DownloadAsync(file, maxBytes, ct);
        public ValueTask RenameAsync(string id, string name, CancellationToken ct) { owner.Renames++; return inner.RenameAsync(id, name, ct); }
        public ValueTask MoveAsync(string id, string source, string destination, CancellationToken ct) => inner.MoveAsync(id, source, destination, ct);
        public async ValueTask TrashAsync(string id, CancellationToken ct)
        {
            owner.Trashes++;
            if (id == owner.FailTrashId && owner.TrashFailure == "known") throw new DriveEffectNotExecutedException("drive_scope_limited", "Provider rejected this target.");
            if (id == owner.FailTrashId && owner.TrashFailure == "unknown-before") throw new HttpRequestException("uncertain trash");
            await inner.TrashAsync(id, ct);
            if (owner.AfterTrash is not null) await owner.AfterTrash(id, ct);
            if (id == owner.FailTrashId && owner.TrashFailure == "after") throw new HttpRequestException("lost trash response");
        }
    }

}
