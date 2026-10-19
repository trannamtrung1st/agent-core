using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Security;

namespace AgentCore.Infrastructure.Workspaces;

internal sealed partial class CanonicalWorkspaceFilesystem
{
    internal WorkspaceStructureResult Structure(IReadOnlyList<WorkspaceStructuralOperation> operations,
        long maxFileBytes, long maxRootBytes, CancellationToken ct = default) =>
        StructureCoreAsync(operations, maxFileBytes, maxRootBytes, null, ct).GetAwaiter().GetResult();

    internal ValueTask<WorkspaceStructureResult> StructureAuthorizedAsync(IReadOnlyList<WorkspaceStructuralOperation> operations,
        long maxFileBytes, long maxRootBytes, Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default) =>
        StructureCoreAsync(operations, maxFileBytes, maxRootBytes, authorizeCommit ?? throw new ArgumentNullException(nameof(authorizeCommit)), ct);

    private async ValueTask<WorkspaceStructureResult> StructureCoreAsync(IReadOnlyList<WorkspaceStructuralOperation> input,
        long maxFileBytes, long maxRootBytes, Func<CancellationToken, ValueTask>? authorizeCommit, CancellationToken ct)
    {
        // Validate every input before the first mutation. Filesystem existence is
        // deliberately checked per operation: earlier operations may create inputs.
        if (input.Count is < 1 or > WorkspaceStructureLimits.MaxOperations)
            throw AgentCoreErrors.Validation("A filesystem batch requires 1–16 operations.");
        var operations = input.ToArray();
        foreach (var op in operations)
        {
            if (op is null) throw AgentCoreErrors.Validation("Invalid filesystem operation.");
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
        for (var index = 0; index < operations.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            var op = operations[index];
            var mutationStarted = false;
            try
            {
                if (authorizeCommit is not null) await authorizeCommit(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var entry = op.Op == "mkdir" ? null : Stat(op.Path ?? op.Source!);
                mutationStarted = true;
                switch (op.Op)
                {
                    case "mkdir": Mkdir(op.Path!); break;
                    case "delete": Delete(op.Path!, op.Recursive, ct); break;
                    case "move": Move(op.Source!, op.Destination!); break;
                    case "copy":
                        if (authorizeCommit is null) CopyTo(op.Source!, this, op.Destination!, ct, maxFileBytes, maxRootBytes);
                        else await CopyToAuthorizedAsync(op.Source!, this, op.Destination!, authorizeCommit, ct, maxFileBytes, maxRootBytes).ConfigureAwait(false);
                        break;
                }
                // Counts identify the explicitly addressed nodes, not a fabricated
                // coherent snapshot of descendants concurrently changed by Linux.
                results.Add(new(index, op.Op, entry?.Kind ?? "directory", "completed",
                    entry?.Kind == "file" ? 1 : 0, entry is null || entry.Directory ? 1 : 0, entry?.ByteSize ?? 0));
            }
            catch (Exception error) when (error is AccessDeniedException or SecurityConflictException)
            {
                return Failed(index, "workspace_authority_changed", index > 0 || mutationStarted);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or AgentCoreException)
            {
                return Failed(index, error is AgentCoreException known ? known.Code : "filesystem_error", true);
            }
        }
        return new(true, operations.Length, operations.Length, null, results);

        WorkspaceStructureResult Failed(int index, string code, bool partial)
        {
            results.Add(new(index, operations[index].Op, "unknown", "failed", 0, 0, 0));
            results.AddRange(operations.Skip(index + 1).Select((pending, offset) =>
                new WorkspaceOperationOutcome(index + offset + 1, pending.Op, "unknown", "notExecuted", 0, 0, 0)));
            return new(false, operations.Length, index, index, results, code,
                "Filesystem operation stopped. Inspect current paths before retrying; completed and partial effects remain.", partial);
        }
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
