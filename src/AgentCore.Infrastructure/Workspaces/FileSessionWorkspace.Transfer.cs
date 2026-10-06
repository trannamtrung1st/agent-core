using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Workspaces;

namespace AgentCore.Infrastructure.Workspaces;

public sealed partial class FileSessionWorkspace
{
    public ValueTask<WorkspaceTransfer> ExportAsync(Guid sessionId, string path, CancellationToken cancellationToken = default) =>
        WithFilesystemAsync(sessionId, async ct =>
        {
            ValidateStructurePath(sessionId, path);
            var all = SnapshotPhysicalTree(sessionId, ct);
            var tree = all.Where(e => e.Path == path || e.Path.StartsWith(path + "/", StringComparison.Ordinal)).OrderBy(e => e.Path, StringComparer.Ordinal).ToArray();
            if (tree.Length == 0) throw AgentCoreErrors.NotFound("Workspace source was not found.");
            var entries = new List<WorkspaceTransferEntry>();
            foreach (var node in tree)
            {
                var physical = SafePhysical(sessionId, node.Path);
                if (!node.Directory && new FileInfo(physical).Length != node.ByteSize)
                    throw AgentCoreErrors.Conflict("Scratch source changed during copy.");
                var bytes = node.Directory ? [] : await File.ReadAllBytesAsync(physical, ct);
                if (bytes.LongLength != node.ByteSize) throw AgentCoreErrors.Conflict("Scratch source changed during copy.");
                entries.Add(new(node.Path == path ? "" : node.Path[(path.Length + 1)..], node.Directory,
                    node.Directory ? "inode/directory" : ContentType(physical), bytes));
            }
            return new WorkspaceTransfer(entries);
        }, cancellationToken);

    public ValueTask ImportAsync(Guid sessionId, string destination, WorkspaceTransfer transfer,
        long? expectedRevision = null, string? expectedSha256 = null, CancellationToken cancellationToken = default) =>
        ImportCoreAsync(sessionId, destination, transfer, expectedRevision, expectedSha256, cancellationToken);

    private async ValueTask ImportCoreAsync(Guid sessionId, string destination, WorkspaceTransfer transfer,
        long? expectedRevision, string? expectedSha256, CancellationToken cancellationToken)
    {
        await WithFilesystemAsync(sessionId, async ct =>
        {
            if (expectedRevision is not null || expectedSha256 is not null)
                throw AgentCoreErrors.Validation("Scratch copy never overwrites; expected revision/hash apply to durable destinations only.");
            ValidateStructurePath(sessionId, destination);
            var plan = WorkspaceTransferPlan.Preflight(SnapshotPhysicalTree(sessionId, ct), destination, transfer,
                "/workspace", _maxWritableBytes, _maxWritableBytes, WorkspaceStructureLimits.MaxEntries, ct);
            foreach (var entry in transfer.Entries)
                ValidateStructurePath(sessionId, destination + (entry.RelativePath.Length == 0 ? "" : "/" + entry.RelativePath));
            var physical = SafePhysical(sessionId, destination);
            var parent = SafePhysicalParent(sessionId, physical);
            var stage = Path.Combine(SessionRoot(sessionId), ".transfer-" + Guid.NewGuid().ToString("N"));
            try
            {
                var directory = transfer.Entries[0].Directory;
                if (directory) Directory.CreateDirectory(stage);
                foreach (var entry in transfer.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var target = entry.RelativePath.Length == 0 ? stage : Path.Combine(stage, entry.RelativePath);
                    if (entry.Directory) Directory.CreateDirectory(target);
                    else { Directory.CreateDirectory(Path.GetDirectoryName(target)!); await File.WriteAllBytesAsync(target, entry.Bytes, ct); }
                }
                ct.ThrowIfCancellationRequested();
                foreach (var logicalParent in plan[0].ParentsToCreate) Directory.CreateDirectory(SafePhysical(sessionId, logicalParent));
                Directory.CreateDirectory(parent);
                if (directory) Directory.Move(stage, physical); else File.Move(stage, physical, false);
                return 0;
            }
            finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); else if (File.Exists(stage)) File.Delete(stage); }
        }, cancellationToken);
    }
}
