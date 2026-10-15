using AgentCore.Application.Sessions;
using Microsoft.Win32.SafeHandles;

namespace AgentCore.Infrastructure.Workspaces;

internal sealed partial class CanonicalWorkspaceFilesystem
{
    // Bounded metadata discovery for the existing composer picker. This is a
    // current lexical walk, never a tree snapshot used to authorize mutations.
    internal Page FindFiles(string query, string? afterPath, int limit, CancellationToken ct)
    {
        using var root = OpenRoot();
        var entries = new List<Entry>(); var scanned = 0; var stopped = false;
        string? lastVisited = null;
        void Walk(SafeFileHandle directory, string prefix, int depth)
        {
            if (depth > 256) throw AgentCoreErrors.Validation("Workspace discovery scan budget exceeded. Use a concrete file path.");
            // Order subtree prefixes with their slash. For example a.txt must
            // precede a/child; sorting only the directory name loses a.txt when
            // continuation resumes after a child of a.
            foreach (var entry in Names(directory).Select(name => (Name: name, Node: LStat(directory, name)))
                .Where(entry => entry.Node is not null)
                .OrderBy(entry => entry.Name + (entry.Node!.Kind == DirectoryKind ? "/" : ""), StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                if (stopped) return;
                var name = entry.Name; var node = entry.Node!;
                var path = prefix + "/" + name;
                var key = path + (node.Kind == DirectoryKind ? "/" : "");
                var beforeCursor = afterPath is not null && string.CompareOrdinal(key, afterPath) <= 0;
                if (node.Kind == DirectoryKind)
                {
                    // A lexically earlier sibling subtree cannot contain a later
                    // cursor entry. Its own descendants can, so keep that branch.
                    if (beforeCursor && !afterPath!.StartsWith(key, StringComparison.Ordinal)) continue;
                    if (!beforeCursor)
                    {
                        if (++scanned > 20000) { stopped = true; return; }
                        lastVisited = key;
                    }
                    using var child = OpenChild(directory, name, directory: true);
                    Walk(child, path, depth + 1);
                }
                else if (!beforeCursor)
                {
                    if (++scanned > 20000) { stopped = true; return; }
                    lastVisited = path;
                    if (node.Kind is FileKind or LinkKind && (path.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || WorkspaceContentTypes.Of(path).Contains(query, StringComparison.OrdinalIgnoreCase)))
                        entries.Add(Describe(path, node, node.Kind == LinkKind ? ReadLink(directory, name) : null));
                    if (entries.Count > limit) { stopped = true; return; }
                }
            }
        }
        Walk(root, _logicalRoot, 0);
        return new(entries.Take(limit).ToArray(), stopped ? entries.Count > limit ? entries[limit - 1].Path : lastVisited : null, stopped);
    }
}
