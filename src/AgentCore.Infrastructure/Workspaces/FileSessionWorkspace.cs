using System.Collections.Concurrent;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Application.Workspaces;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Infrastructure.Workspaces;

public sealed partial class FileSessionWorkspace : ISessionWorkspace, ITenantSandboxWorkspacePreparer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _root;
    private readonly string _templateRoot;
    private readonly IAttachmentStore? _attachments;
    private readonly DefinitionPublicationResourceReader? _publicationResources;
    private readonly long _maxWritableBytes;
    private readonly IMemoryStore _sessions;
    private readonly IAgentInstanceStore _instances;
    private readonly ISandboxStore _sandboxes;
    private readonly AgentCore.Application.Security.TenantSandboxExecutionAuthority? _tenantAuthority;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _provisioning = new();
    private readonly ConcurrentDictionary<Guid, byte> _deleted = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _ioLifetimes = new();
    internal Func<CancellationToken, ValueTask>? BeforeFilesystemRead { get; set; }

    public FileSessionWorkspace(string workspaceRoot, string templateRoot, IAttachmentStore? attachments = null,
        long maxWritableBytes = WorkspaceLimits.MaxWritableBytes, DefinitionPublicationResourceReader? publicationResources = null,
        IMemoryStore? sessions = null, IAgentInstanceStore? instances = null, ISandboxStore? sandboxes = null,
        AgentCore.Application.Security.TenantSandboxExecutionAuthority? tenantAuthority = null)
    {
        _tenantAuthority = tenantAuthority;
        _root = AgentWorkspacePhysicalPaths.ValidateRoot(workspaceRoot);
        _templateRoot = Path.GetFullPath(templateRoot);
        _attachments = attachments; _maxWritableBytes = maxWritableBytes; _publicationResources = publicationResources;
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _instances = instances ?? throw new ArgumentNullException(nameof(instances));
        _sandboxes = sandboxes ?? throw new ArgumentNullException(nameof(sandboxes));
    }

    private async ValueTask<(SessionSnapshot Session, AgentInstance Instance)> OwnerAsync(Guid sessionId, bool write, CancellationToken ct)
    {
        if (_deleted.ContainsKey(sessionId)) throw AgentCoreErrors.NotFound("Session workspace was deleted.");
        var snapshot = await _sessions.LoadMetadataAsync(sessionId, ct) ?? throw AgentCoreErrors.NotFound("Session was not found.");
        if (snapshot.DurablyDeletedAt is not null) throw AgentCoreErrors.NotFound("Session was not found.");
        var instance = await _instances.FindAsync(snapshot.AgentInstanceId, ct) ?? throw AgentCoreErrors.NotFound("Agent Instance was not found.");
        if (write && !CanWrite(snapshot, instance))
            throw AgentCoreErrors.Conflict("Ended or archived workspace is read-only.");
        return (snapshot, instance);
    }
    private static bool CanWrite(SessionSnapshot session, AgentInstance instance) =>
        session.Status != SessionStatus.Ended && session.ArchivedAt is null && instance.Lifecycle == AgentInstanceLifecycle.Active;
    private CanonicalWorkspaceFilesystem Working(Guid owner, Guid sessionId) =>
        new(AgentWorkspacePhysicalPaths.WorkingDirectory(_root, owner, sessionId), "/working");

    private async ValueTask<T> WithIoAsync<T>(Guid sessionId, bool write, Func<CancellationToken, ValueTask<T>> action, CancellationToken ct)
    {
        var gate = _provisioning.GetOrAdd(sessionId, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await OwnerAsync(sessionId, write, ct);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _ioLifetimes.GetOrAdd(sessionId, _ => new()).Token);
            lifetime.Token.ThrowIfCancellationRequested();
            return await action(lifetime.Token);
        }
        finally { gate.Release(); }
    }

    public async ValueTask EnsureAsync(Guid sessionId, AgentDefinition definition, CancellationToken cancellationToken = default)
    {
        var owner = await OwnerAsync(sessionId, false, cancellationToken);
        var gate = _provisioning.GetOrAdd(sessionId, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            owner = await OwnerAsync(sessionId, false, cancellationToken);
            var sessionRoot = AgentWorkspacePhysicalPaths.SessionRoot(_root, owner.Instance.InstanceId, sessionId);
            var files = Working(owner.Instance.InstanceId, sessionId);
            var marker = Path.Combine(sessionRoot, ".provisioned");
            if (File.Exists(marker)) { files.Stat("/working"); return; }
            if (!CanWrite(owner.Session, owner.Instance))
                throw AgentCoreErrors.NotFound("Session workspace has no retained files.");
            await OwnerAsync(sessionId, true, cancellationToken);
            files.Ensure();
            await ApplyTemplateAsync(files, definition, cancellationToken);
            await File.WriteAllTextAsync(marker, DefinitionExecutionPolicies.Of(definition).WorkspacePolicy.TemplateId ?? "", cancellationToken);
        }
        finally { gate.Release(); }
    }

    public async ValueTask<IReadOnlyList<WorkspaceNode>> ListAsync(Guid sessionId, AgentDefinition definition, string prefix, CancellationToken cancellationToken = default)
    {
        var owner = await OwnerAsync(sessionId, false, cancellationToken);
        var path = WorkspaceLogicalPath.Resolve(prefix, sessionId);
        if (path == "/") return [new("/agent", true, 0, false, "directory"), new("/attachments", true, 0, false, "directory"), new("/working", true, 0, CanWrite(owner.Session, owner.Instance), "directory")];
        if (path == "/agent" || path.StartsWith("/agent/", StringComparison.Ordinal)) return await ListAgentAsync(definition, path, cancellationToken);
        if (path == "/attachments" || path.StartsWith("/attachments/", StringComparison.Ordinal)) return await ListAttachmentsAsync(sessionId, path, cancellationToken);
        await EnsureAsync(sessionId, definition, cancellationToken);
        owner = await OwnerAsync(sessionId, false, cancellationToken);
        var files = Working(owner.Instance.InstanceId, sessionId);
        var entry = files.Stat(path);
        var writable = CanWrite(owner.Session, owner.Instance);
        if (!entry.Directory) return [Node(entry, writable)];
        return files.List(path, limit: WorkspaceLimits.MaxListEntries + 1).Entries.Select(node => Node(node, writable)).ToArray();
    }

    public async ValueTask<WorkspaceContent> ReadAsync(Guid sessionId, AgentDefinition definition, string logicalPath, CancellationToken cancellationToken = default)
    {
        var owner = await OwnerAsync(sessionId, false, cancellationToken);
        var path = WorkspaceLogicalPath.Resolve(logicalPath, sessionId);
        if (path == "/agent" || path.StartsWith("/agent/", StringComparison.Ordinal)) return await ReadAgentAsync(definition, path, cancellationToken);
        if (path.StartsWith("/attachments/", StringComparison.Ordinal)) return await ReadAttachmentAsync(sessionId, path, cancellationToken);
        await EnsureAsync(sessionId, definition, cancellationToken);
        return await WithIoAsync<WorkspaceContent>(sessionId, false, async token =>
        {
            if (BeforeFilesystemRead is not null) await BeforeFilesystemRead(token);
            return new(path, WorkspaceContentTypes.Of(path), await Working(owner.Instance.InstanceId, sessionId).ReadAsync(path, AgentWorkspaceLimits.MaxFileBytes, token));
        }, cancellationToken);
    }

    public async ValueTask<WorkspaceNodePage> ListPageAsync(Guid sessionId, AgentDefinition definition, string prefix, string? cursor, int limit, CancellationToken ct = default)
    {
        if (limit is < 1 or > WorkspaceLimits.MaxListEntries) throw AgentCoreErrors.Validation("List limit must be between 1 and 256.");
        var path = WorkspaceLogicalPath.Resolve(prefix, sessionId);
        if (cursor is not null && !cursor.StartsWith(path.TrimEnd('/') + "/", StringComparison.Ordinal)) throw AgentCoreErrors.Validation("Cursor must belong to the listed directory.");
        if (path == "/working" || path.StartsWith("/working/", StringComparison.Ordinal))
        {
            await EnsureAsync(sessionId, definition, ct);
            var owner = await OwnerAsync(sessionId, false, ct);
            var files = Working(owner.Instance.InstanceId, sessionId);
            var entry = files.Stat(path);
            if (!entry.Directory) return new([Node(entry, CanWrite(owner.Session, owner.Instance))], null, false);
            var page = files.List(path, cursor, limit);
            return new(page.Entries.Select(node => Node(node, CanWrite(owner.Session, owner.Instance))).ToArray(), page.NextPath, page.Truncated);
        }
        var nodes = (await ListAsync(sessionId, definition, path, ct)).OrderBy(node => node.LogicalPath, StringComparer.Ordinal)
            .Where(node => cursor is null || string.CompareOrdinal(node.LogicalPath, cursor) > 0).Take(limit + 1).ToArray();
        return new(nodes.Take(limit).ToArray(), nodes.Length > limit ? nodes[limit - 1].LogicalPath : null, nodes.Length > limit);
    }

    public async ValueTask RequireDirectoryAsync(Guid sessionId, AgentDefinition definition, string path, CancellationToken ct = default)
    {
        await EnsureAsync(sessionId, definition, ct);
        var owner = await OwnerAsync(sessionId, false, ct);
        Working(owner.Instance.InstanceId, sessionId).RequireDirectory(path);
    }

    public async ValueTask<bool> TryWriteNewAsync(Guid sessionId, string logicalPath, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        var owner = await OwnerAsync(sessionId, true, cancellationToken);
        await EnsureAsync(sessionId, owner.Session.Definition, cancellationToken);
        return await WithIoAsync(sessionId, true, token => Working(owner.Instance.InstanceId, sessionId)
            .TryWriteNewAsync(logicalPath, bytes, AgentWorkspaceLimits.MaxFileBytes, _maxWritableBytes, token), cancellationToken);
    }

    public async ValueTask WriteAsync(Guid sessionId, string logicalPath, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        var owner = await OwnerAsync(sessionId, true, cancellationToken);
        await EnsureAsync(sessionId, owner.Session.Definition, cancellationToken);
        await WithIoAsync(sessionId, true, async token =>
        {
            await Working(owner.Instance.InstanceId, sessionId).WriteAsync(logicalPath, bytes, AgentWorkspaceLimits.MaxFileBytes, _maxWritableBytes, token);
            return true;
        }, cancellationToken);
    }

    public async ValueTask<WorkspacePatchResult> PatchTextAsync(Guid sessionId, AgentDefinition definition, string logicalPath,
        IReadOnlyList<WorkspaceTextEdit> edits, CancellationToken cancellationToken = default)
    {
        var owner = await OwnerAsync(sessionId, true, cancellationToken);
        await EnsureAsync(sessionId, definition, cancellationToken);
        return await WithIoAsync<WorkspacePatchResult>(sessionId, true, async token =>
        {
            var files = Working(owner.Instance.InstanceId, sessionId);
            var current = await files.ReadAsync(logicalPath, AgentWorkspaceLimits.MaxFileBytes, token);
            var bytes = WorkspaceTextPatches.Apply(current, edits);
            await files.WriteAsync(logicalPath, bytes, AgentWorkspaceLimits.MaxFileBytes, _maxWritableBytes, token);
            return new(logicalPath, bytes.LongLength, edits.Count);
        }, cancellationToken);
    }

    public async ValueTask<WorkspaceStructureResult> StructureAsync(Guid sessionId, IReadOnlyList<WorkspaceStructuralOperation> operations,
        CancellationToken cancellationToken = default)
    {
        var owner = await OwnerAsync(sessionId, true, cancellationToken);
        await EnsureAsync(sessionId, owner.Session.Definition, cancellationToken);
        operations = WorkspaceStructuralPaths.Normalize(sessionId, operations);
        return await WithIoAsync(sessionId, true, token => ValueTask.FromResult(
            Working(owner.Instance.InstanceId, sessionId).Structure(operations, AgentWorkspaceLimits.MaxFileBytes, _maxWritableBytes, token)), cancellationToken);
    }

    public async ValueTask DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var snapshot = await _sessions.LoadMetadataAsync(sessionId, cancellationToken) ?? throw AgentCoreErrors.NotFound("Session was not found.");
        if (await _sandboxes.GetLeaseAsync(sessionId, cancellationToken) is { State: not SandboxLeaseState.Terminated })
            throw AgentCoreErrors.Conflict("Live or uncertain sandbox mounts block Session filesystem cleanup.");
        _deleted[sessionId] = 1;
        await _ioLifetimes.GetOrAdd(sessionId, _ => new()).CancelAsync();
        var gate = _provisioning.GetOrAdd(sessionId, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { CanonicalWorkspaceFilesystem.PurgeOwner(AgentWorkspacePhysicalPaths.SessionRoot(_root, snapshot.AgentInstanceId, sessionId), cancellationToken); }
        finally { gate.Release(); }
    }

    public async ValueTask<string> PhysicalWorkingDirectoryAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var owner = await OwnerAsync(sessionId, false, cancellationToken);
        return Working(owner.Instance.InstanceId, sessionId).PhysicalRoot;
    }

    private async ValueTask ApplyTemplateAsync(CanonicalWorkspaceFilesystem files, AgentDefinition definition, CancellationToken ct)
    {
        var template = DefinitionExecutionPolicies.Of(definition).WorkspacePolicy.TemplateId;
        if (string.IsNullOrEmpty(template)) return;
        var source = Path.GetFullPath(Path.Combine(_templateRoot, template));
        if (source.StartsWith(_templateRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) && Directory.Exists(source))
        {
            // Use the same descriptor-relative reader for trusted template resources;
            // a linked template is never followed into arbitrary host data.
            var reader = new CanonicalWorkspaceFilesystem(source, "/working");
            async ValueTask Copy(string prefix)
            {
                string? cursor = null;
                do
                {
                    var page = reader.List(prefix, cursor);
                    foreach (var entry in page.Entries)
                    {
                        if (prefix == "/working" && Path.GetFileName(entry.Path) is "knowledge" or "harness") continue;
                        if (entry.Directory) { files.Mkdir(entry.Path); await Copy(entry.Path); }
                        else if (entry.Kind == "file") await files.WriteAsync(entry.Path, await reader.ReadAsync(entry.Path, AgentWorkspaceLimits.MaxFileBytes, ct), AgentWorkspaceLimits.MaxFileBytes, _maxWritableBytes, ct);
                    }
                    cursor = page.NextPath;
                } while (cursor is not null);
            }
            await Copy("/working");
        }
        // Explicit published resources take precedence during first provisioning.
        if (_publicationResources is not null)
            foreach (var item in await _publicationResources.ListAsync(definition.Id, definition.Version, ct))
            {
                if (item.Kind != AgentDefinitionResourceKind.Template || !item.LogicalPath.StartsWith(template + "/", StringComparison.Ordinal)) continue;
                var relative = item.LogicalPath[(template.Length + 1)..];
                var bytes = await _publicationResources.ReadContentAsync(item, ct);
                if (bytes is not null) await files.WriteAsync("/working/" + relative, bytes, AgentWorkspaceLimits.MaxFileBytes, _maxWritableBytes, ct);
            }
    }
    private static WorkspaceNode Node(CanonicalWorkspaceFilesystem.Entry entry, bool writable) =>
        new(entry.Path, entry.Directory, entry.ByteSize, writable, entry.Kind, entry.ModifiedAt, entry.LinkTarget, entry.Mode);
}
