using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantWorkspaceRuntimeReadService
{
    private sealed record SearchMatch(string Path, string Kind, string Snippet);

    private async ValueTask<(string Output, string Hash)> Search(TenantRuntimeFrame frame, TenantWorkspaceReadArguments args,
        Read before, int budget, CancellationToken ct, bool approved, bool live)
    {
        var matches = new List<SearchMatch>(); var scanned = 0; var visited = 0; var truncated = false;
        // Include negative observations and skipped files: a changed non-match can become a match.
        // Only the digest is retained as the private approval witness; no names/snippets enter preview.
        using var evidence = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Observe<T>(T value) { evidence.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))); evidence.AppendData([0]); }
        async ValueTask Boundary() => await Fence(frame, ToolCatalog.WorkspaceSearch, args, before, ct, approved, live).ConfigureAwait(false);
        async ValueTask Walk(string path, int depth)
        {
            ct.ThrowIfCancellationRequested(); string? cursor = null;
            do
            {
                if (matches.Count >= args.MaxResults || scanned >= WorkspaceSearchLimits.MaxFilesScanned || visited >= WorkspaceSearchLimits.MaxEntriesScanned)
                { truncated = true; return; }
                AgentWorkspacePage page;
                try { page = await files.ListTenantAsync(before.Workspace, path, cursor, WorkspaceLimits.MaxListEntries, ct).ConfigureAwait(false); }
                catch (AgentCoreException)
                { Observe(new { path, cursor, unavailable = true }); truncated = true; return; }
                await Boundary().ConfigureAwait(false);
                Observe(new { path, cursor, page.Items, page.NextPath, page.Truncated });
                foreach (var node in page.Items)
                {
                    if (matches.Count >= args.MaxResults || scanned >= WorkspaceSearchLimits.MaxFilesScanned || visited >= WorkspaceSearchLimits.MaxEntriesScanned)
                    { truncated = true; return; }
                    visited++;
                    TenantWorkspaceRules.Path(before.Workspace, node.LogicalPath);
                    // A provider cannot return the directory itself/another branch and widen discovery.
                    if (!node.LogicalPath.StartsWith(path.TrimEnd('/') + "/", StringComparison.Ordinal)
                        && !(depth == 0 && !node.Directory && node.LogicalPath == path)) throw new SecurityConflictException("workspace_source_changed");
                    if (node.Directory)
                    {
                        if (depth < WorkspaceSearchLimits.MaxDepth) await Walk(node.LogicalPath, depth + 1).ConfigureAwait(false);
                        else truncated = true;
                        continue;
                    }
                    scanned++;
                    var relative = node.LogicalPath == args.Path ? Path.GetFileName(node.LogicalPath) : node.LogicalPath[(args.Path.TrimEnd('/').Length + 1)..];
                    if (!WorkspaceGlob.Matches(relative, args.Glob)) continue;
                    var nameMatch = node.LogicalPath.Contains(args.Query!, StringComparison.OrdinalIgnoreCase);
                    string? snippet = null;
                    if (node.ByteSize is > 0 and <= WorkspaceSearchLimits.MaxFileBytesToScan)
                    {
                        try
                        {
                            var metadata = await files.InspectTenantAsync(before.Workspace, node.LogicalPath, 0, ct).ConfigureAwait(false);
                            if (metadata.Item.ByteSize > WorkspaceSearchLimits.MaxFileBytesToScan)
                            { Observe(new { node.LogicalPath, metadata.Item, skipped = true }); }
                            else
                            {
                                var content = await files.InspectTenantAsync(before.Workspace, node.LogicalPath, WorkspaceSearchLimits.MaxFileBytesToScan, ct).ConfigureAwait(false);
                                await Boundary().ConfigureAwait(false);
                                if (content.Item != metadata.Item || content.Bytes.LongLength != content.Item.ByteSize
                                    || (await files.InspectTenantAsync(before.Workspace, node.LogicalPath, 0, ct).ConfigureAwait(false)).Item != content.Item)
                                    throw new SecurityConflictException("workspace_source_changed");
                                Observe(new { node.LogicalPath, content.Item, hash = Convert.ToHexStringLower(SHA256.HashData(content.Bytes)) });
                                if (WorkspaceSearchText.TryReadText(content.Bytes, out var text) && text.Contains(args.Query!, StringComparison.OrdinalIgnoreCase))
                                    snippet = WorkspaceSearchText.Snippet(text, args.Query!);
                            }
                        }
                        catch (AgentCoreException)
                        { Observe(new { node.LogicalPath, unavailable = true }); truncated = true; }
                    }
                    if (nameMatch || snippet is not null) matches.Add(new(node.LogicalPath, snippet is null ? "name" : "content", snippet ?? Path.GetFileName(node.LogicalPath)));
                }
                if (page.NextPath is null) { truncated |= page.Truncated; return; }
                if (cursor is not null && string.CompareOrdinal(page.NextPath, cursor) <= 0) { truncated = true; return; }
                TenantWorkspaceRules.Path(before.Workspace, page.NextPath);
                cursor = page.NextPath;
            } while (cursor is not null);
        }
        await Walk(args.Path, 0).ConfigureAwait(false);
        Observe(new { scanned, visited, truncated });
        var fingerprint = Convert.ToHexStringLower(evidence.GetHashAndReset());
        var count = matches.Count;
        while (true)
        {
            var output = JsonSerializer.Serialize(new { path = args.Path, query = args.Query, truncated = truncated || count < matches.Count,
                matches = matches.Take(count).Select(m => new { path = m.Path, kind = m.Kind, snippet = m.Snippet }), untrusted = true,
                tenantWorkspaceRead = true, workspaceRead = new { tool = ToolCatalog.WorkspaceSearch, arguments = args } });
            if (Encoding.UTF8.GetByteCount(output) <= budget && (count > 0 || matches.Count == 0)) return (output, fingerprint);
            if (count == 0) return (ToolJsonResults.FitToBudget(budget, """{"error":"output_limit"}"""), fingerprint);
            count--;
        }
    }
}
