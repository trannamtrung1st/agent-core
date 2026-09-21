using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public sealed record HttpRequestApprovalPreparation(
    string ActionHash,
    string Summary,
    Dictionary<string, string> Details);

public sealed record HttpRequestApprovalPrepareResult(
    HttpRequestApprovalPreparation? Preparation,
    string? ErrorJson);

public static class WorkspaceSearchLimits
{
    public const int DefaultMaxResults = 20;
    public const int MaxResultsCap = 50;
    public const int MaxQueryLength = 200;
    public const int MaxSnippetChars = 160;
    public const int MaxFileBytesToScan = 256 * 1024;
    public const int MaxFilesScanned = 200;
    public const int MaxDepth = 8;
}

public sealed partial class SessionToolExecutor
{
    public HttpRequestApprovalPrepareResult PrepareHttpRequestApproval(JsonElement args)
    {
        if (httpRequestClient is null)
        {
            return new HttpRequestApprovalPrepareResult(null, Error("unavailable", "HTTP requests are unavailable."));
        }

        if (!HttpRequestNormalizer.TryNormalize(args, out var snapshot, out var code, out var message) || snapshot is null)
        {
            return new HttpRequestApprovalPrepareResult(null, Error(code, message));
        }

        return new HttpRequestApprovalPrepareResult(
            new HttpRequestApprovalPreparation(
                HttpRequestNormalizer.ComputeActionHash(snapshot),
                HttpRequestNormalizer.Summary(snapshot),
                HttpRequestNormalizer.Details(snapshot)),
            null);
    }

    private async Task<string> ExecuteHttpRequestAsync(
        JsonElement args,
        ToolApprovalGrant? approvalGrant,
        CancellationToken cancellationToken)
    {
        if (httpRequestClient is null)
        {
            return Error("unavailable", "HTTP requests are unavailable.");
        }

        if (approvalGrant is null)
        {
            return Error("approval_required", "Tool execution requires explicit approval.");
        }

        if (!HttpRequestNormalizer.TryNormalize(args, out var snapshot, out var code, out var message) || snapshot is null)
        {
            return Error(code, message);
        }

        var actionHash = HttpRequestNormalizer.ComputeActionHash(snapshot);
        if (!string.Equals(actionHash, approvalGrant.ActionHash, StringComparison.Ordinal)
            || !string.Equals(approvalGrant.ToolName, ToolCatalog.HttpRequest, StringComparison.Ordinal))
        {
            return Error("stale_approval", "Approval no longer matches the requested action.");
        }

        if (!Uri.TryCreate(snapshot.Url, UriKind.Absolute, out var uri))
        {
            return Error("invalid", "url must be an absolute http or https URL.");
        }

        var result = await httpRequestClient.SendAsync(
                new HttpToolRequest(
                    snapshot.Method,
                    uri,
                    snapshot.Headers,
                    Encoding.UTF8.GetBytes(snapshot.Body),
                    snapshot.FollowRedirects),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.ErrorCode is not null)
        {
            return JsonSerializer.Serialize(new
            {
                untrusted = true,
                method = snapshot.Method,
                finalUrl = result.FinalUrl,
                error = result.ErrorCode,
                message = result.ErrorMessage
            });
        }

        return JsonSerializer.Serialize(new
        {
            untrusted = true,
            method = snapshot.Method,
            status = result.StatusCode,
            finalUrl = result.FinalUrl,
            contentType = result.ContentType,
            redirectLocation = result.RedirectLocation,
            truncated = result.Truncated,
            body = result.Body
        });
    }

    private async Task<string> MoveWorkspaceAsync(Guid sessionId, JsonElement args, CancellationToken cancellationToken)
    {
        if (workspace is null)
        {
            return Error("unavailable", "Workspace is unavailable.");
        }

        if (!TryString(args, "source", out var source) || !TryString(args, "destination", out var destination))
        {
            return Error("invalid", "source and destination are required.");
        }

        if (!TryResolveWorkspacePath(source, sessionId, out source, out var sourceError))
        {
            return sourceError;
        }

        if (!TryResolveWorkspacePath(destination, sessionId, out destination, out var destinationError))
        {
            return destinationError;
        }

        if (string.Equals(source, destination, StringComparison.Ordinal))
        {
            return Error("invalid", "source and destination are the same path.");
        }

        await workspace.MoveAsync(sessionId, source, destination, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { source, destination });
    }

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

            IReadOnlyList<WorkspaceNode> nodes;
            try
            {
                nodes = await store.ListAsync(sessionId, definition, logicalPath, cancellationToken).ConfigureAwait(false);
            }
            catch (AgentCoreException)
            {
                return;
            }

            if (nodes.Count == 1 && !nodes[0].Directory && string.Equals(nodes[0].LogicalPath, logicalPath, StringComparison.Ordinal))
            {
                await ConsiderFileAsync(nodes[0]).ConfigureAwait(false);
                return;
            }

            foreach (var node in nodes)
            {
                if (matches.Count >= maxResults || scanned >= WorkspaceSearchLimits.MaxFilesScanned)
                {
                    truncated = true;
                    return;
                }

                if (node.Directory)
                {
                    if (depth < WorkspaceSearchLimits.MaxDepth)
                    {
                        await WalkAsync(node.LogicalPath, depth + 1).ConfigureAwait(false);
                    }
                    else
                    {
                        truncated = true;
                    }

                    continue;
                }

                await ConsiderFileAsync(node).ConfigureAwait(false);
            }
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
                    var content = await store.ReadAsync(sessionId, definition, node.LogicalPath, cancellationToken)
                        .ConfigureAwait(false);
                    if (TryReadSearchText(content.Bytes, out var text)
                        && text.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        snippet = Snippet(text, query);
                        kind = "content";
                    }
                }
                catch (AgentCoreException)
                {
                    // Filename matches can still be returned when a file cannot be decoded.
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

    private static bool TryReadSearchText(byte[] bytes, out string text)
    {
        text = "";
        var sample = Math.Min(bytes.Length, 8192);
        for (var index = 0; index < sample; index++)
        {
            if (bytes[index] == 0)
            {
                return false;
            }
        }

        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static string Snippet(string text, string query)
    {
        var index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return "";
        }

        var start = Math.Max(0, index - 40);
        var length = Math.Min(text.Length - start, WorkspaceSearchLimits.MaxSnippetChars);
        var snippet = text.Substring(start, length).Replace('\r', ' ').Replace('\n', ' ');
        if (start > 0)
        {
            snippet = "…" + snippet;
        }

        if (start + length < text.Length)
        {
            snippet += "…";
        }

        return snippet;
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
        return Regex.IsMatch(relativePath, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
