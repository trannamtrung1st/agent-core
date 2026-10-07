using AgentCore.Application.Ports;
using AgentCore.Application.Agents;
using AgentCore.Application.Sessions;
using AgentCore.Application.Workspaces;

namespace AgentCore.Infrastructure.Workspaces;

public sealed partial class FileSessionWorkspace
{
    internal Func<int, CancellationToken, ValueTask>? BeforeStructuralOperation { get; set; }
    internal Func<CancellationToken, ValueTask>? BeforeFilesystemRead { get; set; }

    private async ValueTask<T> WithFilesystemAsync<T>(Guid sessionId, Func<CancellationToken, ValueTask<T>> action, CancellationToken ct)
    {
        var gate = Gate(sessionId); await gate.WaitAsync(ct);
        try
        {
            ThrowIfDeleted(sessionId);
            using var linked = LinkWriter(sessionId, ct);
            linked.Token.ThrowIfCancellationRequested();
            return await action(linked.Token);
        }
        finally { gate.Release(); }
    }

    public ValueTask<WorkspaceStructureResult> StructureAsync(Guid sessionId,
        IReadOnlyList<WorkspaceStructuralOperation> operations, CancellationToken cancellationToken = default) =>
        StructureCoreAsync(sessionId, operations, cancellationToken);

    private async ValueTask<WorkspaceStructureResult> StructureCoreAsync(Guid sessionId,
        IReadOnlyList<WorkspaceStructuralOperation> operations, CancellationToken cancellationToken)
    {
        operations = WorkspaceStructuralPaths.Normalize(sessionId, operations);
        ThrowIfDeleted(sessionId);
        var gate = Gate(sessionId); await gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDeleted(sessionId);
            using var linked = LinkWriter(sessionId, cancellationToken);
            var ct = linked.Token; ct.ThrowIfCancellationRequested();
            var entries = SnapshotPhysicalTree(sessionId, ct);
            var plan = WorkspaceTreePlanner.Plan(entries, operations, "/workspace", _maxWritableBytes, _maxWritableBytes, ct);
            foreach (var step in plan)
            {
                foreach (var path in new[] { step.Operation.Path, step.Operation.Source, step.Operation.Destination }.OfType<string>()) ValidateStructurePath(sessionId, path);
                if (step.Operation.Op is "copy" or "move") foreach (var entry in step.SourceTree)
                    ValidateStructurePath(sessionId, step.Operation.Destination! + entry.Path[step.Operation.Source!.Length..]);
            }
            var results = new List<WorkspaceOperationOutcome>();
            foreach (var step in plan)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (BeforeStructuralOperation is not null) await BeforeStructuralOperation(results.Count, ct);
                    // Recheck every traversed child before any recursive physical mutation.
                    foreach (var entry in step.SourceTree)
                    {
                        var path = SafePhysical(sessionId, entry.Path);
                        if (entry.Directory ? !Directory.Exists(path) : !File.Exists(path))
                            throw AgentCoreErrors.Conflict("Workspace source changed after preflight.");
                    }
                    foreach (var parent in step.ParentsToCreate) Directory.CreateDirectory(SafePhysical(sessionId, parent));
                    var op = step.Operation;
                    if (op.Op == "mkdir") Directory.CreateDirectory(SafePhysical(sessionId, op.Path!));
                    else if (op.Op == "delete")
                    {
                        var source = SafePhysical(sessionId, op.Path!);
                        if (step.Outcome.Kind == "directory") Directory.Delete(source, op.Recursive);
                        else File.Delete(source);
                    }
                    else if (op.Op == "move")
                    {
                        var source = SafePhysical(sessionId, op.Source!); var destination = SafePhysical(sessionId, op.Destination!);
                        Directory.CreateDirectory(SafePhysicalParent(sessionId, destination));
                        if (step.Outcome.Kind == "directory") Directory.Move(source, destination);
                        else File.Move(source, destination);
                    }
                    else
                    {
                        foreach (var entry in step.SourceTree)
                        {
                            ct.ThrowIfCancellationRequested();
                            var target = op.Destination! + entry.Path[op.Source!.Length..];
                            var destination = SafePhysical(sessionId, target);
                            if (entry.Directory) Directory.CreateDirectory(destination);
                            else
                            {
                                Directory.CreateDirectory(SafePhysicalParent(sessionId, destination));
                                await CopyFileAsync(SafePhysical(sessionId, entry.Path), destination, entry.ByteSize, ct);
                            }
                        }
                    }
                    results.Add(step.Outcome);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or AgentCoreException)
                {
                    return new(false, plan.Count, results.Count, results.Count, [.. results, step.Outcome with { Status = "failed" }, .. plan.Skip(results.Count + 1).Select(p => p.Outcome with { Status = "notExecuted" })],
                        "executionFailed", "Workspace execution stopped. Inspect current paths before retrying; earlier changes are retained.", true);
                }
            }
            return new(true, plan.Count, results.Count, null, results);
        }
        finally { gate.Release(); }
    }

    private IReadOnlyList<WorkspaceTreeEntry> SnapshotPhysicalTree(Guid sessionId, CancellationToken ct)
    {
        var entries = new List<WorkspaceTreeEntry>();
        var physical = SessionWorkspaceDir(sessionId);
        DenyAllLinks(physical, SessionRoot(sessionId));
        if (Directory.Exists(physical)) Walk(physical, 0);
        return entries;
        void Walk(string directory, int depth)
        {
            if (depth > WorkspaceStructureLimits.MaxDepth) throw AgentCoreErrors.Validation("Workspace tree exceeds its traversal depth.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested(); DenyAllLinks(entry, SessionRoot(sessionId));
                var dir = Directory.Exists(entry); var logical = ToLogical(sessionId, entry);
                if (!WritableTrees.Contains(logical)) ValidateStructurePath(sessionId, logical);
                entries.Add(new(logical, dir, dir ? 0 : new FileInfo(entry).Length));
                if (entries.Count > WorkspaceStructureLimits.MaxEntries) throw AgentCoreErrors.WorkspaceQuotaExceeded();
                if (dir) Walk(entry, depth + 1);
            }
        }
    }

    private string SafePhysical(Guid sessionId, string logicalPath)
    {
        var path = MapWorkspacePath(sessionId, logicalPath);
        DenyAllLinks(path, SessionRoot(sessionId)); return path;
    }
    private static void ValidateStructurePath(Guid sessionId, string path)
    {
        RolePermissions.EnsureLogicalPathAllowed(path, sessionId);
        if (!IsWritableFile(path) || IsForbiddenPersist(path)) throw AgentCoreErrors.Forbidden("Protected workspace entries cannot be restructured.");
    }
    private string SafePhysicalParent(Guid sessionId, string physical)
    {
        var parent = Path.GetDirectoryName(physical)!;
        DenyAllLinks(parent, SessionRoot(sessionId)); return parent;
    }
    private static void DenyAllLinks(string path, string root)
    {
        DenyEscapingLinks(path, root);
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (new FileInfo(current).LinkTarget is not null || new DirectoryInfo(current).LinkTarget is not null)
                throw AgentCoreErrors.Forbidden("Linked filesystem entries are not supported.");
            if (current == root) break;
        }
    }
    private static async Task CopyFileAsync(string source, string destination, long expectedBytes, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetDirectoryName(destination)!, ".workspace-copy-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                if (input.Length != expectedBytes) throw AgentCoreErrors.Conflict("Workspace source changed after preflight.");
                await input.CopyToAsync(output, ct); await output.FlushAsync(ct);
                if (output.Length != expectedBytes) throw AgentCoreErrors.Conflict("Workspace source changed during copy.");
            }
            ct.ThrowIfCancellationRequested(); File.Move(temp, destination, overwrite: false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
