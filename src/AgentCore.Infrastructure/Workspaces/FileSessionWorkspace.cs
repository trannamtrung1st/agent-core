using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Domain.Conversation;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Workspaces;

public sealed class FileSessionWorkspace : ISessionWorkspace
{
    private static readonly string[] WritableTrees = ["/workspace/working", "/workspace/artifacts", "/workspace/state"];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _root;
    private readonly string _templateRoot;
    private readonly IAttachmentStore? _attachments;
    private readonly long _maxWritableBytes;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _writers = new();
    private readonly ConcurrentDictionary<Guid, byte> _deleted = new();

    public FileSessionWorkspace(
        string workspaceRoot,
        string templateRoot,
        IAttachmentStore? attachments = null,
        long maxWritableBytes = WorkspaceLimits.MaxWritableBytes)
    {
        AttachmentBlobKeys.EnsureSafeRoot(workspaceRoot);
        AttachmentBlobKeys.EnsureSafeRoot(templateRoot);
        _root = Path.GetFullPath(workspaceRoot);
        _templateRoot = Path.GetFullPath(templateRoot);
        _attachments = attachments;
        _maxWritableBytes = maxWritableBytes;
        Directory.CreateDirectory(_root);
    }

    public async ValueTask EnsureAsync(
        Guid sessionId,
        AgentDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDeleted(sessionId);
        var gate = Gate(sessionId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDeleted(sessionId);
            var physical = SessionRoot(sessionId);
            Directory.CreateDirectory(Path.Combine(physical, "workspace", "working"));
            Directory.CreateDirectory(Path.Combine(physical, "workspace", "artifacts"));
            Directory.CreateDirectory(Path.Combine(physical, "workspace", "state"));
            var marker = Path.Combine(physical, ".provisioned");
            if (File.Exists(marker))
            {
                return;
            }

            ApplyTemplate(definition, physical);
            await File.WriteAllTextAsync(marker, RoleEnvironments.Of(definition).WorkspacePolicy.TemplateId ?? "", cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<WorkspaceNode>> ListAsync(
        Guid sessionId,
        AgentDefinition definition,
        string prefix,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(sessionId, definition, cancellationToken).ConfigureAwait(false);
        var path = Normalize(prefix);
        RolePermissions.EnsureLogicalPathAllowed(path, sessionId);
        if (path is "/" or "")
        {
            return
            [
                new WorkspaceNode("/agent", true, 0, false),
                new WorkspaceNode("/attachments", true, 0, false),
                new WorkspaceNode("/workspace", true, 0, false)
            ];
        }

        if (path.StartsWith("/agent", StringComparison.Ordinal))
        {
            return ListAgent(definition, path);
        }

        if (path.StartsWith("/attachments", StringComparison.Ordinal))
        {
            return await ListAttachmentsAsync(sessionId, path, cancellationToken).ConfigureAwait(false);
        }

        if (path.StartsWith("/workspace", StringComparison.Ordinal))
        {
            return ListPhysical(sessionId, path);
        }

        throw AgentCoreErrors.Forbidden("Path is not permitted for this role.");
    }

    public async ValueTask<WorkspaceContent> ReadAsync(
        Guid sessionId,
        AgentDefinition definition,
        string logicalPath,
        CancellationToken cancellationToken = default)
    {
        await EnsureAsync(sessionId, definition, cancellationToken).ConfigureAwait(false);
        var path = Normalize(logicalPath);
        RolePermissions.EnsureLogicalPathAllowed(path, sessionId);
        if (path.StartsWith("/agent/", StringComparison.Ordinal) || path == "/agent")
        {
            return ReadAgent(definition, path);
        }

        if (path.StartsWith("/attachments/", StringComparison.Ordinal))
        {
            return await ReadAttachmentAsync(sessionId, path, cancellationToken).ConfigureAwait(false);
        }

        if (!path.StartsWith("/workspace/", StringComparison.Ordinal))
        {
            throw AgentCoreErrors.Forbidden("Path is not permitted for this role.");
        }

        var physical = MapWorkspaceFile(sessionId, path);
        DenyEscapingLinks(physical, SessionRoot(sessionId));
        if (!File.Exists(physical) || Directory.Exists(physical))
        {
            throw AgentCoreErrors.NotFound("Workspace path was not found.");
        }

        var bytes = await File.ReadAllBytesAsync(physical, cancellationToken).ConfigureAwait(false);
        return new WorkspaceContent(path, ContentType(physical), bytes);
    }

    public async ValueTask WriteAsync(
        Guid sessionId,
        string logicalPath,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDeleted(sessionId);
        var path = Normalize(logicalPath);
        RolePermissions.EnsureLogicalPathAllowed(path, sessionId);
        if (!IsWritableFile(path))
        {
            throw AgentCoreErrors.Forbidden("Execution view writes are limited to /workspace.");
        }

        if (IsForbiddenPersist(path))
        {
            throw AgentCoreErrors.Forbidden("Runtime internals and secret files cannot be stored in the workspace.");
        }

        var gate = Gate(sessionId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDeleted(sessionId);
            var linked = CancellationTokenSource.CreateLinkedTokenSource(Writer(sessionId).Token, cancellationToken);
            linked.Token.ThrowIfCancellationRequested();
            var physical = MapWorkspaceFile(sessionId, path);
            var parent = Path.GetDirectoryName(physical)!;
            DenyEscapingLinks(physical, SessionRoot(sessionId));
            DenyEscapingLinks(parent, SessionRoot(sessionId));
            Directory.CreateDirectory(parent);
            DenyEscapingLinks(physical, SessionRoot(sessionId));
            var used = Measure(SessionWorkspaceDir(sessionId));
            var existing = File.Exists(physical) ? new FileInfo(physical).Length : 0;
            if (used - existing + bytes.Length > _maxWritableBytes)
            {
                throw AgentCoreErrors.WorkspaceQuotaExceeded();
            }

            await using var stream = new FileStream(
                physical,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous);
            await stream.WriteAsync(bytes, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<WorkspacePatchResult> PatchTextAsync(
        Guid sessionId,
        AgentDefinition definition,
        string logicalPath,
        string expectedSha256Hex,
        IReadOnlyList<WorkspaceTextEdit> edits,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDeleted(sessionId);
        if (edits.Count == 0 || edits.Count > WorkspaceLimits.MaxPatchEdits)
        {
            throw AgentCoreErrors.Validation("Patch must include between 1 and 32 edits.");
        }

        foreach (var edit in edits)
        {
            if (string.IsNullOrEmpty(edit.OldText))
            {
                throw AgentCoreErrors.Validation("Each edit oldText must be non-empty.");
            }
        }

        if (!IsLowerHexSha256(expectedSha256Hex))
        {
            throw AgentCoreErrors.Validation("expectedSha256 must be a lowercase SHA-256 hex digest.");
        }

        var path = Normalize(logicalPath);
        RolePermissions.EnsureLogicalPathAllowed(path, sessionId);
        if (!IsWritableFile(path))
        {
            throw AgentCoreErrors.Forbidden("Execution view writes are limited to /workspace.");
        }

        if (IsForbiddenPersist(path))
        {
            throw AgentCoreErrors.Forbidden("Runtime internals and secret files cannot be stored in the workspace.");
        }

        await EnsureAsync(sessionId, definition, cancellationToken).ConfigureAwait(false);
        var gate = Gate(sessionId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDeleted(sessionId);
            var linked = CancellationTokenSource.CreateLinkedTokenSource(Writer(sessionId).Token, cancellationToken);
            linked.Token.ThrowIfCancellationRequested();
            var physical = MapWorkspaceFile(sessionId, path);
            DenyEscapingLinks(physical, SessionRoot(sessionId));
            if (!File.Exists(physical) || Directory.Exists(physical))
            {
                throw AgentCoreErrors.NotFound("Workspace path was not found.");
            }

            var originalBytes = await File.ReadAllBytesAsync(physical, linked.Token).ConfigureAwait(false);
            if (!TryDecodeStrictUtf8(originalBytes, out var text))
            {
                throw AgentCoreErrors.Validation("Workspace file must be strict UTF-8 text.");
            }

            var previousHash = ComputeSha256Hex(originalBytes);
            if (!string.Equals(previousHash, expectedSha256Hex, StringComparison.Ordinal))
            {
                throw AgentCoreErrors.Conflict("Workspace file hash does not match expectedSha256.");
            }

            var updated = text;
            foreach (var edit in edits)
            {
                var occurrences = CountOccurrences(updated, edit.OldText);
                if (occurrences != 1)
                {
                    throw AgentCoreErrors.Conflict("Each edit oldText must match exactly once in the current file content.");
                }

                updated = updated.Replace(edit.OldText, edit.NewText, StringComparison.Ordinal);
            }

            var newBytes = Encoding.UTF8.GetBytes(updated);
            var used = Measure(SessionWorkspaceDir(sessionId));
            var existing = originalBytes.Length;
            if (used - existing + newBytes.Length > _maxWritableBytes)
            {
                throw AgentCoreErrors.WorkspaceQuotaExceeded();
            }

            await CommitBytesAtomicallyAsync(physical, newBytes, linked.Token).ConfigureAwait(false);

            return new WorkspacePatchResult(
                path,
                previousHash,
                ComputeSha256Hex(newBytes),
                newBytes.Length,
                edits.Count);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        _deleted[sessionId] = 1;
        if (_writers.TryRemove(sessionId, out var writers))
        {
            await writers.CancelAsync().ConfigureAwait(false);
            writers.Dispose();
        }

        var gate = Gate(sessionId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var physical = SessionRoot(sessionId);
            for (var attempt = 0; attempt < 5; attempt++)
            {
                if (!Directory.Exists(physical))
                {
                    return;
                }

                try
                {
                    Directory.Delete(physical, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private IReadOnlyList<WorkspaceNode> ListAgent(AgentDefinition definition, string path)
    {
        var env = RoleEnvironments.Of(definition);
        if (path is "/agent")
        {
            var nodes = new List<WorkspaceNode> { new("/agent/definition.json", false, 0, false) };
            if (env.HarnessList.Count > 0)
            {
                nodes.Add(new WorkspaceNode("/agent/harness", true, 0, false));
            }

            if (env.KnowledgeList.Count > 0)
            {
                nodes.Add(new WorkspaceNode("/agent/knowledge", true, 0, false));
            }

            return nodes;
        }

        if (path == "/agent/definition.json")
        {
            return [new WorkspaceNode(path, false, ReadAgent(definition, path).Bytes.Length, false)];
        }

        if (path is "/agent/harness")
        {
            return env.HarnessList
                .Select(name => new WorkspaceNode($"/agent/harness/{name}", false, 0, false))
                .ToArray();
        }

        if (path is "/agent/knowledge")
        {
            return env.KnowledgeList
                .Select(item => new WorkspaceNode($"/agent/knowledge/{item.Identity}", false, 0, false))
                .ToArray();
        }

        return [new WorkspaceNode(path, false, ReadAgent(definition, path).Bytes.Length, false)];
    }

    private WorkspaceContent ReadAgent(AgentDefinition definition, string path)
    {
        var env = RoleEnvironments.Of(definition);
        if (path == "/agent/definition.json")
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    definition.Id,
                    definition.Version,
                    name = definition.Identity.Name,
                    role = definition.Identity.Role
                },
                JsonOptions);
            return new WorkspaceContent(path, "application/json", bytes);
        }

        if (path.StartsWith("/agent/harness/", StringComparison.Ordinal))
        {
            var name = path["/agent/harness/".Length..];
            if (!env.HarnessList.Contains(name, StringComparer.Ordinal))
            {
                throw AgentCoreErrors.NotFound("Harness entry was not found.");
            }

            var text = Encoding.UTF8.GetBytes($"name={name}\n");
            return new WorkspaceContent(path, "text/plain", text);
        }

        if (path.StartsWith("/agent/knowledge/", StringComparison.Ordinal))
        {
            var identity = path["/agent/knowledge/".Length..];
            var source = env.KnowledgeList.FirstOrDefault(item =>
                string.Equals(item.Identity, identity, StringComparison.Ordinal))
                ?? throw AgentCoreErrors.NotFound("Knowledge entry was not found.");
            var text = Encoding.UTF8.GetBytes($"title={source.Title}\ncitation={source.Citation}\n");
            return new WorkspaceContent(path, "text/plain", text);
        }

        throw AgentCoreErrors.NotFound("Agent path was not found.");
    }

    private async ValueTask<IReadOnlyList<WorkspaceNode>> ListAttachmentsAsync(
        Guid sessionId,
        string path,
        CancellationToken cancellationToken)
    {
        if (_attachments is null)
        {
            return path is "/attachments" ? [] : throw AgentCoreErrors.NotFound("Attachment path was not found.");
        }

        var records = await _attachments.ListForSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (path is "/attachments")
        {
            return records
                .Select(record => new WorkspaceNode($"/attachments/{record.AttachmentId}", false, record.ByteSize, false))
                .ToArray();
        }

        var id = ParseAttachmentId(path);
        var match = records.FirstOrDefault(record => record.AttachmentId == id)
            ?? throw AgentCoreErrors.NotFound("Attachment path was not found.");
        return [new WorkspaceNode($"/attachments/{match.AttachmentId}", false, match.ByteSize, false)];
    }

    private async ValueTask<WorkspaceContent> ReadAttachmentAsync(
        Guid sessionId,
        string path,
        CancellationToken cancellationToken)
    {
        if (_attachments is null)
        {
            throw AgentCoreErrors.NotFound("Attachment path was not found.");
        }

        var id = ParseAttachmentId(path);
        var record = await _attachments.GetAsync(sessionId, id, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Attachment path was not found.");
        await using var stream = await _attachments.OpenContentAsync(sessionId, id, cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return new WorkspaceContent(path, record.ContentType, buffer.ToArray());
    }

    private IReadOnlyList<WorkspaceNode> ListPhysical(Guid sessionId, string path)
    {
        if (path is "/workspace")
        {
            return
            [
                new WorkspaceNode("/workspace/working", true, 0, false),
                new WorkspaceNode("/workspace/artifacts", true, 0, false),
                new WorkspaceNode("/workspace/state", true, 0, false)
            ];
        }

        var physical = MapWorkspacePath(sessionId, path);
        DenyEscapingLinks(physical, SessionRoot(sessionId));
        if (Directory.Exists(physical))
        {
            var nodes = new List<WorkspaceNode>();
            foreach (var dir in Directory.GetDirectories(physical))
            {
                nodes.Add(new WorkspaceNode(ToLogical(sessionId, dir), true, 0, false));
            }

            foreach (var file in Directory.GetFiles(physical))
            {
                DenyEscapingLinks(file, SessionRoot(sessionId));
                nodes.Add(new WorkspaceNode(ToLogical(sessionId, file), false, new FileInfo(file).Length, true));
            }

            return nodes;
        }

        if (File.Exists(physical))
        {
            return [new WorkspaceNode(path, false, new FileInfo(physical).Length, true)];
        }

        throw AgentCoreErrors.NotFound("Workspace path was not found.");
    }

    private string MapWorkspaceFile(Guid sessionId, string path)
    {
        if (!IsWritableFile(path))
        {
            throw AgentCoreErrors.Forbidden("Execution view writes are limited to /workspace.");
        }

        return MapWorkspacePath(sessionId, path);
    }

    private string MapWorkspacePath(Guid sessionId, string path)
    {
        var relative = path["/workspace".Length..].TrimStart('/');
        if (relative.Contains("..", StringComparison.Ordinal))
        {
            throw AgentCoreErrors.Forbidden("Path is not permitted for this role.");
        }

        var combined = Path.GetFullPath(Path.Combine(SessionWorkspaceDir(sessionId), relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnder(SessionWorkspaceDir(sessionId), combined))
        {
            throw AgentCoreErrors.Forbidden("Path is not permitted for this role.");
        }

        return combined;
    }

    private string ToLogical(Guid sessionId, string physical)
    {
        var root = SessionWorkspaceDir(sessionId);
        var relative = Path.GetRelativePath(root, physical).Replace('\\', '/');
        return relative is "." ? "/workspace" : "/workspace/" + relative;
    }

    private void ApplyTemplate(AgentDefinition definition, string physical)
    {
        var templateId = RoleEnvironments.Of(definition).WorkspacePolicy.TemplateId;
        if (string.IsNullOrWhiteSpace(templateId))
        {
            return;
        }

        var source = Path.GetFullPath(Path.Combine(_templateRoot, templateId));
        if (!IsUnder(_templateRoot, source) || !Directory.Exists(source))
        {
            return;
        }

        var working = Path.Combine(physical, "workspace", "working");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            DenyEscapingLinks(file, source);
            var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            if (relative.Contains("..", StringComparison.Ordinal)
                || relative.StartsWith("knowledge/", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("harness/", StringComparison.OrdinalIgnoreCase)
                || IsForbiddenPersist("/workspace/working/" + relative))
            {
                continue;
            }

            var dest = Path.GetFullPath(Path.Combine(working, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsUnder(working, dest))
            {
                continue;
            }

            var destParent = Path.GetDirectoryName(dest)!;
            DenyEscapingLinks(dest, physical);
            DenyEscapingLinks(destParent, physical);
            Directory.CreateDirectory(destParent);
            File.Copy(file, dest, overwrite: false);
        }
    }

    private static bool IsWritableFile(string path)
    {
        if (path.EndsWith('/'))
        {
            return false;
        }

        return WritableTrees.Any(tree =>
            path.StartsWith(tree + "/", StringComparison.Ordinal) && path.Length > tree.Length + 1);
    }

    private static bool IsForbiddenPersist(string path)
    {
        var lower = path.ToLowerInvariant();
        var name = Path.GetFileName(lower);
        return name.StartsWith(".env", StringComparison.Ordinal)
            || name.Contains("user-secrets", StringComparison.Ordinal)
            || name.Contains("secrets.json", StringComparison.Ordinal)
            || lower.Contains("appsettings.", StringComparison.Ordinal) && lower.Contains("secret")
            || lower.Contains("cancellationtokensource", StringComparison.Ordinal)
            || lower.Contains("dbcontext", StringComparison.Ordinal)
            || lower.Contains("providerstream", StringComparison.Ordinal)
            || name.EndsWith(".sock", StringComparison.Ordinal);
    }

    private static Guid ParseAttachmentId(string path)
    {
        var token = path["/attachments/".Length..].Split('/', 2)[0];
        if (!Guid.TryParse(token, out var id))
        {
            throw AgentCoreErrors.NotFound("Attachment path was not found.");
        }

        return id;
    }

    private static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        var normalized = path.Replace('\\', '/').Trim();
        if (!normalized.StartsWith('/'))
        {
            normalized = "/" + normalized;
        }

        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        }

        if (normalized.Length > 1)
        {
            normalized = normalized.TrimEnd('/');
        }

        return normalized;
    }

    private static string ContentType(string physical) =>
        Path.GetExtension(physical).ToLowerInvariant() switch
        {
            ".json" => "application/json",
            ".md" => "text/markdown",
            ".txt" => "text/plain",
            _ => "application/octet-stream"
        };

    private string SessionRoot(Guid sessionId) => Path.Combine(_root, sessionId.ToString("N"));

    public string PhysicalWorkingDirectory(Guid sessionId)
    {
        ThrowIfDeleted(sessionId);
        return Path.Combine(SessionRoot(sessionId), "workspace", "working");
    }

    private string SessionWorkspaceDir(Guid sessionId) => Path.Combine(SessionRoot(sessionId), "workspace");

    private SemaphoreSlim Gate(Guid sessionId) => _locks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));

    private CancellationTokenSource Writer(Guid sessionId) =>
        _writers.GetOrAdd(sessionId, _ => new CancellationTokenSource());

    private void ThrowIfDeleted(Guid sessionId)
    {
        if (_deleted.ContainsKey(sessionId))
        {
            throw AgentCoreErrors.NotFound("Workspace was deleted.");
        }
    }

    private static long Measure(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(file => (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
            .Select(file => new FileInfo(file).Length)
            .Sum();
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool IsUnder(string root, string candidate)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(candidate);
        return full.StartsWith(prefix, PathComparison)
            || string.Equals(Path.GetFullPath(root), full, PathComparison);
    }

    private static void DenyEscapingLinks(string path, string root)
    {
        var rootFull = Path.GetFullPath(root);
        var current = string.IsNullOrEmpty(path) ? path : Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                var attrs = File.GetAttributes(current);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                {
                    var target = File.ResolveLinkTarget(current, returnFinalTarget: true);
                    if (target is null || !IsUnder(rootFull, target.FullName))
                    {
                        throw AgentCoreErrors.Forbidden("Symlink targets outside the session workspace are denied.");
                    }
                }
            }

            if (string.Equals(current, rootFull, PathComparison))
            {
                break;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, PathComparison))
            {
                break;
            }

            current = parent;
        }

        if (!IsUnder(rootFull, path) && (File.Exists(path) || Directory.Exists(path)))
        {
            throw AgentCoreErrors.Forbidden("Path is not permitted for this role.");
        }
    }

    private static bool IsLowerHexSha256(string value) =>
        value.Length == 64 && value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool TryDecodeStrictUtf8(byte[] bytes, out string text)
    {
        text = "";
        try
        {
            text = Encoding.GetEncoding(
                    "utf-8",
                    EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback)
                .GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static string ComputeSha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task CommitBytesAtomicallyAsync(
        string physicalPath,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(physicalPath)!;
        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(physicalPath)}.{Guid.NewGuid():N}.patchtmp");
        try
        {
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, physicalPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (needle.Length == 0)
        {
            return 0;
        }

        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
