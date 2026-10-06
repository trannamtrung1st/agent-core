using AgentCore.Application.Ports;
using AgentCore.Application.Observability;
using AgentCore.Application.Sessions;

namespace AgentCore.Application.Workspaces;

public sealed record WorkspacePlannedOperation(WorkspaceStructuralOperation Operation,
    IReadOnlyList<WorkspaceTreeEntry> SourceTree, IReadOnlyList<string> ParentsToCreate,
    WorkspaceOperationOutcome Outcome);

/// <summary>Pure logical preflight shared by physical Session scratch and metadata/blob home.</summary>
public static class WorkspaceTreePlanner
{
    public static IReadOnlyList<WorkspacePlannedOperation> Plan(IReadOnlyList<WorkspaceTreeEntry> entries,
        IReadOnlyList<WorkspaceStructuralOperation> operations, string root, long maxBytes,
        long maxFileBytes, CancellationToken cancellationToken = default, int maxFiles = WorkspaceStructureLimits.MaxEntries)
    {
        if (operations.Count is < 1 or > WorkspaceStructureLimits.MaxOperations
            || System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(operations).Length > WorkspaceStructureLimits.MaxRequestBytes)
            throw AgentCoreErrors.Validation("A batch requires 1–16 structural operations.");
        if (root is not ("/home" or "/workspace")) throw AgentCoreErrors.Forbidden("Workspace scope is unavailable.");
        var tree = new Dictionary<string, WorkspaceTreeEntry>(StringComparer.OrdinalIgnoreCase);
        var protectedRoots = root == "/home" ? new[] { root } : new[] { root, root + "/working", root + "/artifacts", root + "/state" };
        foreach (var p in protectedRoots) tree[p] = new(p, true, 0);
        foreach (var e in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (protectedRoots.Contains(e.Path)) continue;
            Mutable(e.Path);
            if (!tree.TryAdd(e.Path, e)) throw AgentCoreErrors.Conflict("Case-only workspace paths are ambiguous.");
        }
        // Old home metadata may have implicit parents. The logical snapshot still has complete directories.
        foreach (var e in entries) Parents(e.Path, []);
        CheckBounds();
        var plans = new List<WorkspacePlannedOperation>();
        foreach (var op in operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parents = new List<string>();
            IReadOnlyList<WorkspaceTreeEntry> source = [];
            string kind; string status = "completed";
            switch (op.Op)
            {
                case "mkdir":
                    if (op.Path is null || op.Source is not null || op.Destination is not null || op.Recursive)
                        throw AgentCoreErrors.Validation("mkdir accepts only path.");
                    Mutable(op.Path); kind = "directory";
                    if (Find(op.Path) is { } exists)
                    {
                        if (!exists.Directory) throw AgentCoreErrors.Conflict("A file occupies the directory path.");
                        status = "alreadyExists";
                    }
                    else { Parents(op.Path, parents); tree.Add(op.Path, new(op.Path, true, 0)); parents.Add(op.Path); }
                    break;
                case "copy":
                case "move":
                    if (op.Source is null || op.Destination is null || op.Path is not null || op.Recursive)
                        throw AgentCoreErrors.Validation("copy/move accept only source and destination.");
                    Mutable(op.Source); Mutable(op.Destination);
                    var node = Find(op.Source) ?? throw AgentCoreErrors.NotFound("Workspace source was not found.");
                    kind = node.Directory ? "directory" : "file";
                    if (op.Source.Equals(op.Destination, StringComparison.OrdinalIgnoreCase)
                        || node.Directory && Under(op.Source, op.Destination))
                        throw AgentCoreErrors.Validation("A workspace tree cannot copy/move into itself or its descendant.");
                    if (Find(op.Destination) is not null) throw AgentCoreErrors.Conflict("Destination already exists; trees are not merged.");
                    source = tree.Values.Where(e => e.Path == op.Source || Under(op.Source, e.Path)).OrderBy(e => e.Path, StringComparer.Ordinal).ToArray();
                    Parents(op.Destination, parents);
                    if (op.Op == "move") foreach (var e in source) tree.Remove(e.Path);
                    foreach (var e in source)
                    {
                        var path = op.Destination + e.Path[op.Source.Length..]; Mutable(path);
                        if (!tree.TryAdd(path, e with { Path = path })) throw AgentCoreErrors.Conflict("Destination conflicts with an existing entry.");
                    }
                    break;
                case "delete":
                    if (op.Path is null || op.Source is not null || op.Destination is not null)
                        throw AgentCoreErrors.Validation("delete accepts path and explicit recursive intent.");
                    Mutable(op.Path);
                    var deleted = Find(op.Path) ?? throw AgentCoreErrors.NotFound("Workspace path was not found.");
                    kind = deleted.Directory ? "directory" : "file";
                    source = tree.Values.Where(e => e.Path == op.Path || Under(op.Path, e.Path)).ToArray();
                    if (deleted.Directory && source.Count > 1 && !op.Recursive)
                        throw AgentCoreErrors.Conflict("Non-empty directory deletion requires recursive: true.");
                    foreach (var e in source) tree.Remove(e.Path);
                    break;
                default: throw AgentCoreErrors.Validation("Only mkdir/copy/move/delete are allowed in a structural batch.");
            }
            CheckBounds();
            var outcome = new WorkspaceOperationOutcome(plans.Count, op.Op, kind, status,
                source.Count(e => !e.Directory), source.Count(e => e.Directory) + parents.Count,
                source.Where(e => !e.Directory).Sum(e => e.ByteSize));
            plans.Add(new(op, source, parents, outcome));
        }
        return plans;

        WorkspaceTreeEntry? Find(string path)
        {
            if (!tree.TryGetValue(path, out var entry)) return null;
            if (entry.Path != path) throw AgentCoreErrors.Conflict("Case-only workspace paths are denied.");
            return entry;
        }
        void Parents(string path, List<string> created)
        {
            var parent = path[..path.LastIndexOf('/')];
            if (parent.Length < root.Length) return;
            if (Find(parent) is { } existing)
            {
                if (!existing.Directory) throw AgentCoreErrors.Conflict("A destination parent is a file.");
                return;
            }
            Mutable(parent); Parents(parent, created); tree.Add(parent, new(parent, true, 0)); created.Add(parent);
        }
        void Mutable(string path)
        {
            if (path.Length > WorkspaceStructureLimits.MaxPathChars)
                throw AgentCoreErrors.Validation("Workspace path exceeds its length bound.");
            if (protectedRoots.Contains(path) || !Under(root, path)
                || root == "/workspace" && !protectedRoots.Skip(1).Any(p => Under(p, path)))
                throw AgentCoreErrors.Forbidden("Structural operations require a child of an authorized mutable workspace.");
            // Common portable leaf/path policy; physical host names never enter the plan.
            AgentHomePath.Normalize("/home/" + path[(root.Length + 1)..]);
        }
        void CheckBounds()
        {
            if (tree.Count > WorkspaceStructureLimits.MaxEntries
                || tree.Values.Count(e => !e.Directory) > maxFiles
                || tree.Values.Any(e => !e.Directory && (e.ByteSize < 0 || e.ByteSize > maxFileBytes))
                || tree.Values.Where(e => !e.Directory).Sum(e => e.ByteSize) > maxBytes)
            {
                OperationalDiagnostics.RecordResourceLimit("workspaceFilesystem");
                throw AgentCoreErrors.WorkspaceQuotaExceeded();
            }
        }
    }

    private static bool Under(string parent, string path) => path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);
}
