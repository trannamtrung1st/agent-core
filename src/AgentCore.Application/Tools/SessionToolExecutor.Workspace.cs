using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public static class WorkspaceSearchLimits
{
    public const int DefaultMaxResults = 20;
    public const int MaxResultsCap = 50;
    public const int MaxQueryLength = 200;
    public const int MaxSnippetChars = 160;
    public const int MaxFileBytesToScan = 256 * 1024;
    public const int MaxFilesScanned = 200;
    public const int MaxDepth = 8;
    public const int MaxEntriesScanned = 4096;
}

public sealed partial class SessionToolExecutor
{
    private async Task<string> SearchWorkspaceAsync(
        AgentDefinition definition,
        Guid sessionId,
        JsonElement args,
        CancellationToken cancellationToken)
    {
        if (!TryString(args, "query", out var query))
        {
            return Error("invalid", "query is required.");
        }

        if (workspace is null)
        {
            return Error("unavailable", "Workspace is unavailable.");
        }

        var store = workspace;

        if (query.Length > WorkspaceSearchLimits.MaxQueryLength)
        {
            return Error("invalid", "query is too long.");
        }

        var maxResults = WorkspaceSearchLimits.DefaultMaxResults;
        if (args.TryGetProperty("maxResults", out var maxElement) && maxElement.ValueKind == JsonValueKind.Number)
        {
            if (!maxElement.TryGetInt32(out maxResults) || maxResults < 1 || maxResults > WorkspaceSearchLimits.MaxResultsCap)
            {
                return Error("invalid", "maxResults must be between 1 and 50.");
            }
        }

        var root = ".";
        if (args.TryGetProperty("path", out var pathElement) && pathElement.ValueKind == JsonValueKind.String)
        {
            var raw = pathElement.GetString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                root = raw;
            }
        }

        if (!TryResolveWorkspacePath(root, sessionId, out root, out var pathError))
        {
            return pathError;
        }

        string? glob = null;
        if (args.TryGetProperty("glob", out var globElement) && globElement.ValueKind == JsonValueKind.String)
        {
            glob = globElement.GetString();
            if (!string.IsNullOrWhiteSpace(glob) && WorkspaceLogicalPath.HasParentSegment(glob))
            {
                return Error("invalid", "glob cannot contain '..'.");
            }
        }

        await store.EnsureAsync(sessionId, definition, cancellationToken).ConfigureAwait(false);
        var matches = new List<object>();
        var scanned = 0;
        var visited = 0;
        var truncated = false;
        await WalkAsync(root, 0).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            query,
            path = root,
            truncated,
            matches
        });

        async Task WalkAsync(string logicalPath, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (matches.Count >= maxResults || scanned >= WorkspaceSearchLimits.MaxFilesScanned)
            {
                truncated = true;
                return;
            }

            string? cursor = null;
            do
            {
                WorkspaceNodePage page;
                try
                {
                    page = await ListExecutionWorkspacePageAsync(sessionId, definition, logicalPath, cursor, cancellationToken).ConfigureAwait(false);
                }
                catch (AgentCoreException)
                {
                    truncated = true; // Includes concurrent removal: this is an observation, not a snapshot.
                    return;
                }
                foreach (var node in page.Entries)
                {
                    if (matches.Count >= maxResults || scanned >= WorkspaceSearchLimits.MaxFilesScanned
                        || visited >= WorkspaceSearchLimits.MaxEntriesScanned)
                    {
                        truncated = true;
                        return;
                    }
                    visited++;
                    if (node.Directory)
                    {
                        if (depth < WorkspaceSearchLimits.MaxDepth)
                            await WalkAsync(node.LogicalPath, depth + 1).ConfigureAwait(false);
                        else truncated = true;
                    }
                    else await ConsiderFileAsync(node).ConfigureAwait(false);
                }
                if (page.NextPath is null)
                {
                    truncated |= page.Truncated;
                    return;
                }
                if (cursor is not null && string.CompareOrdinal(page.NextPath, cursor) <= 0)
                {
                    truncated = true; // A faulty/churning cursor must not loop forever.
                    return;
                }
                cursor = page.NextPath;
                if (matches.Count >= maxResults || scanned >= WorkspaceSearchLimits.MaxFilesScanned
                    || visited >= WorkspaceSearchLimits.MaxEntriesScanned)
                {
                    truncated = true;
                    return;
                }
            } while (cursor is not null);
        }

        async Task ConsiderFileAsync(WorkspaceNode node)
        {
            scanned++;
            var relative = RelativeTo(root, node.LogicalPath);
            if (!WorkspaceGlob.Matches(relative, glob))
            {
                return;
            }

            var nameMatch = node.LogicalPath.Contains(query, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(node.LogicalPath).Contains(query, StringComparison.OrdinalIgnoreCase);
            string? snippet = null;
            var kind = "name";
            if (node.ByteSize <= WorkspaceSearchLimits.MaxFileBytesToScan && node.ByteSize > 0)
            {
                try
                {
                    var content = await ReadExecutionWorkspaceAsync(sessionId, definition, node.LogicalPath, cancellationToken)
                        .ConfigureAwait(false);
                    if (WorkspaceSearchText.TryReadText(content.Bytes, out var text)
                        && text.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        snippet = WorkspaceSearchText.Snippet(text, query);
                        kind = "content";
                    }
                }
                catch (AgentCoreException)
                {
                    // Filename evidence survives a read failure, but content coverage is incomplete.
                    truncated = true;
                }
            }

            if (!nameMatch && kind != "content")
            {
                return;
            }

            if (matches.Count >= maxResults)
            {
                truncated = true;
                return;
            }

            matches.Add(new
            {
                path = node.LogicalPath,
                kind,
                snippet = snippet ?? Path.GetFileName(node.LogicalPath)
            });
        }
    }

    private static string RelativeTo(string root, string path)
    {
        if (string.Equals(path, root, StringComparison.Ordinal))
        {
            return Path.GetFileName(path);
        }

        var prefix = root.TrimEnd('/') + "/";
        return path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : Path.GetFileName(path);
    }
}

internal static class WorkspaceGlob
{
    public static bool Matches(string relativePath, string? glob)
    {
        if (string.IsNullOrWhiteSpace(glob) || glob is "*" or "**")
        {
            return true;
        }

        var pattern = "^" + Regex.Escape(glob.Replace('\\', '/'))
            .Replace("\\*\\*/", "(?:.*/)?", StringComparison.Ordinal)
            .Replace("\\*\\*", ".*", StringComparison.Ordinal)
            .Replace("\\*", "[^/]*", StringComparison.Ordinal)
            .Replace("\\?", "[^/]", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(relativePath, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }
}
