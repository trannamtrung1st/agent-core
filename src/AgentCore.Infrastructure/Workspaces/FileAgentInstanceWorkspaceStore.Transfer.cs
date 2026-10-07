using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Workspaces;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Workspaces;

public sealed partial class FileAgentInstanceWorkspaceStore
{
    public ValueTask<WorkspaceTransfer> ExportAsync(Guid instanceId, string path, CancellationToken cancellationToken = default) =>
        WithAsync(instanceId, async ct =>
        {
            path = AgentHomePath.Normalize(path);
            var rows = (await RowsAsync(instanceId, ct)).Select(r => (Row: r, Item: FromRow(r)))
                .Where(p => p.Item.LogicalPath == path || p.Item.LogicalPath.StartsWith(path + "/", StringComparison.Ordinal))
                .OrderBy(p => p.Item.LogicalPath, StringComparer.Ordinal).ToArray();
            if (rows.Length == 0) throw AgentCoreErrors.NotFound("Home source was not found.");
            var entries = new List<WorkspaceTransferEntry>();
            if (rows[0].Item.LogicalPath != path) entries.Add(new("", true, "inode/directory", []));
            foreach (var pair in rows)
            {
                ct.ThrowIfCancellationRequested();
                var item = pair.Item;
                var physical = item.Directory ? null : BlobPath(instanceId, pair.Row.BlobKey);
                if (!item.Directory && (!File.Exists(physical) || new FileInfo(physical!).Length != item.ByteSize || item.ByteSize > maxFileBytes))
                    throw AgentCoreErrors.Conflict("Home source integrity check failed.");
                var bytes = item.Directory ? [] : await File.ReadAllBytesAsync(physical!, ct);
                if (!item.Directory && (bytes.LongLength != item.ByteSize || Hash(bytes) != item.Sha256Hex))
                    throw AgentCoreErrors.Conflict("Home source integrity check failed.");
                entries.Add(new(item.LogicalPath == path ? "" : item.LogicalPath[(path.Length + 1)..], item.Directory, item.ContentType, bytes));
            }
            return new WorkspaceTransfer(entries);
        }, cancellationToken);

    public async ValueTask ImportAsync(Guid instanceId, string destination, WorkspaceTransfer transfer, Guid? sourceSessionId = null,
        long? expectedRevision = null, string? expectedSha256 = null, CancellationToken cancellationToken = default)
    {
        destination = AgentHomePath.Normalize(destination);
        if (transfer.Entries.Count == 1 && !transfer.Entries[0].Directory)
        {
            var entry = transfer.Entries[0];
            await WriteFileAsync(instanceId, destination, entry.ContentType, entry.Bytes, sourceSessionId, expectedRevision, expectedSha256, cancellationToken);
            return;
        }
        await WithAsync(instanceId, async ct =>
        {
            if (expectedRevision is not null || expectedSha256 is not null)
                throw AgentCoreErrors.Validation("Directory copies never overwrite or merge; CAS applies to file destinations only.");
            var rows = await RowsAsync(instanceId, ct);
            var plan = WorkspaceTransferPlan.Preflight(rows.Select(r => new WorkspaceTreeEntry(FromRow(r).LogicalPath, FromRow(r).Directory, r.ByteSize)).ToArray(),
                destination, transfer, "/home", maxInstanceBytes, maxFileBytes, AgentWorkspaceLimits.MaxItems, ct);
            var added = new List<AgentWorkspaceRow>();
            var blobs = new List<string>();
            try
            {
                foreach (var parent in plan[0].ParentsToCreate) added.Add(DirectoryRow(instanceId, parent, sourceSessionId));
                foreach (var entry in transfer.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var path = destination + (entry.RelativePath.Length == 0 ? "" : "/" + entry.RelativePath);
                    AgentHomePath.Normalize(path);
                    if (entry.Directory) { added.Add(DirectoryRow(instanceId, path, sourceSessionId)); continue; }
                    var blob = ids.NewId().ToString("N");
                    var physical = BlobPath(instanceId, blob); blobs.Add(physical);
                    Directory.CreateDirectory(Path.GetDirectoryName(physical)!); physical = BlobPath(instanceId, blob);
                    await WriteImmutableBlobAsync(physical, entry.Bytes, ct);
                    added.Add(Row(new AgentWorkspaceItem(ids.NewId(), instanceId, path, entry.ContentType, entry.Bytes.LongLength,
                        Hash(entry.Bytes), 1, time.GetUtcNow(), time.GetUtcNow(), sourceSessionId), blob));
                }
                await CommitRowsAsync(instanceId, [], added, ct);
                return 0;
            }
            catch { foreach (var blob in blobs) { DeleteFile(blob + ".partial"); DeleteFile(blob); } throw; }
        }, cancellationToken);
    }
}
