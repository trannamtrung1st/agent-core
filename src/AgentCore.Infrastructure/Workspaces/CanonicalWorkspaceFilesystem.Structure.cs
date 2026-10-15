using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;

namespace AgentCore.Infrastructure.Workspaces;

internal sealed partial class CanonicalWorkspaceFilesystem
{
    internal WorkspaceStructureResult Structure(IReadOnlyList<WorkspaceStructuralOperation> operations,
        long maxFileBytes, long maxRootBytes, CancellationToken ct = default)
    {
        // Validate every input before the first mutation. Filesystem existence is
        // deliberately checked per operation: earlier operations may create inputs.
        if (operations.Count is < 1 or > WorkspaceStructureLimits.MaxOperations)
            throw AgentCoreErrors.Validation("A filesystem batch requires 1–16 operations.");
        foreach (var op in operations)
        {
            if (op.Op is "mkdir" or "delete")
            {
                if (op.Path is null || op.Source is not null || op.Destination is not null || op.Op == "mkdir" && op.Recursive)
                    throw AgentCoreErrors.Validation("Invalid filesystem operation.");
                ProtectRoot(Relative(op.Path));
            }
            else if (op.Op is "copy" or "move")
            {
                if (op.Path is not null || op.Source is null || op.Destination is null || op.Recursive)
                    throw AgentCoreErrors.Validation("Invalid filesystem operation.");
                ProtectRoot(Relative(op.Source)); ProtectRoot(Relative(op.Destination));
            }
            else throw AgentCoreErrors.Validation("Unknown filesystem operation.");
        }
        var results = new List<WorkspaceOperationOutcome>();
        for (var index = 0; index < operations.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var op = operations[index];
            try
            {
                var entry = op.Op == "mkdir" ? null : Stat(op.Path ?? op.Source!);
                switch (op.Op)
                {
                    case "mkdir": Mkdir(op.Path!); break;
                    case "delete": Delete(op.Path!, op.Recursive, ct); break;
                    case "move": Move(op.Source!, op.Destination!); break;
                    case "copy": CopyTo(op.Source!, this, op.Destination!, ct, maxFileBytes, maxRootBytes); break;
                }
                // Counts identify the explicitly addressed nodes, not a fabricated
                // coherent snapshot of descendants concurrently changed by Linux.
                results.Add(new(index, op.Op, entry?.Kind ?? "directory", "completed",
                    entry?.Kind == "file" ? 1 : 0, entry is null || entry.Directory ? 1 : 0, entry?.ByteSize ?? 0));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or AgentCoreException)
            {
                results.Add(new(index, op.Op, "unknown", "failed", 0, 0, 0));
                results.AddRange(operations.Skip(index + 1).Select((pending, offset) =>
                    new WorkspaceOperationOutcome(index + offset + 1, pending.Op, "unknown", "notExecuted", 0, 0, 0)));
                return new(false, operations.Count, index, index, results,
                    error is AgentCoreException known ? known.Code : "filesystem_error",
                    "Filesystem operation failed. Inspect current paths before retrying; completed and partial effects remain.", true);
            }
        }
        return new(true, operations.Count, operations.Count, null, results);
    }

    internal static void PurgeOwner(string physicalOwnerRoot, CancellationToken ct)
    {
        var path = Path.GetFullPath(physicalOwnerRoot);
        var parentPath = Path.GetDirectoryName(path) ?? throw new ArgumentException("Invalid owner root.");
        var files = new CanonicalWorkspaceFilesystem(parentPath, "/working");
        Microsoft.Win32.SafeHandles.SafeFileHandle parentHandle;
        try { parentHandle = files.OpenRoot(); }
        catch (AgentCoreException error) when (error.Code == "NotFound") { return; }
        using var parent = parentHandle;
        var name = Path.GetFileName(path);
        var entry = LStat(parent, name);
        if (entry is null) return;
        if (entry.Kind != DirectoryKind) throw AgentCoreErrors.Forbidden("Owner root was substituted; cleanup requires operator inspection.");
        Remove(parent, name, recursive: true, ct);
    }
}
