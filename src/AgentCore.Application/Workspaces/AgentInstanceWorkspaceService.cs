using System.Security.Cryptography;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Workspaces;

public sealed class AgentInstanceWorkspaceService(
    IAgentInstanceStore instances,
    IAgentInstanceWorkspaceStore store,
    IMemoryStore sessions,
    ISessionWorkspace scratch,
    AdminLifecycleCoordinator lifecycle)
{
    public ValueTask<AgentWorkspacePage> ListAsync(Guid owner, string prefix = "/home", string? afterPath = null,
        int limit = AgentWorkspaceLimits.MaxPageItems, CancellationToken cancellationToken = default) =>
        lifecycle.WithInstanceAsync(owner, async ct =>
        {
            await RequireAsync(owner, false, ct);
            if (limit < 1 || limit > AgentWorkspaceLimits.MaxPageItems) throw AgentCoreErrors.Validation("Invalid workspace page size.");
            return await store.ListAsync(owner, AgentHomePath.Normalize(prefix, false), afterPath is null ? null : AgentHomePath.Normalize(afterPath), limit, ct);
        }, cancellationToken);

    public ValueTask<AgentWorkspaceContent> ReadAsync(Guid owner, Guid? itemId = null, string? path = null, CancellationToken cancellationToken = default) =>
        lifecycle.WithInstanceAsync(owner, async ct =>
        {
            await RequireAsync(owner, false, ct);
            return await store.ReadAsync(owner, itemId, path is null ? null : AgentHomePath.Normalize(path), ct);
        }, cancellationToken);

    public ValueTask DeleteAsync(Guid owner, Guid itemId, long expectedRevision, CancellationToken cancellationToken = default) =>
        lifecycle.WithInstanceAsync(owner, async ct =>
        {
            await RequireAsync(owner, true, ct);
            if (expectedRevision < 1) throw AgentCoreErrors.Validation("expectedRevision is required.");
            await store.DeleteAsync(owner, itemId, expectedRevision, ct);
        }, cancellationToken);

    public async ValueTask<Guid> SessionOwnerAsync(Guid sessionId, CancellationToken ct = default)
    {
        var snapshot = await SessionAsync(sessionId, false, ct);
        var owner = snapshot.AgentInstanceId ?? throw AgentCoreErrors.Validation("Durable home is unavailable for this execution.");
        await RequireAsync(owner, false, ct);
        return owner;
    }

    public async ValueTask<AgentWorkspaceItem> RetainAsync(Guid sessionId, string source, string destination,
        long? expectedRevision = null, string? expectedSha256 = null, CancellationToken cancellationToken = default)
    {
        var snapshot = await SessionAsync(sessionId, true, cancellationToken);
        var owner = snapshot.AgentInstanceId ?? throw AgentCoreErrors.Validation("Durable home is unavailable for this execution.");
        source = WorkspaceLogicalPath.Resolve(source, sessionId);
        if (!source.StartsWith("/workspace/", StringComparison.Ordinal)) throw AgentCoreErrors.Forbidden("Retain requires a session scratch source.");
        destination = AgentHomePath.Normalize(destination);
        ValidateExpected(expectedRevision, expectedSha256);
        return await lifecycle.WithInstanceAsync(owner, async ct =>
        {
            await RequireAsync(owner, true, ct);
            await SessionAsync(sessionId, true, ct);
            var content = await scratch.ReadAsync(sessionId, snapshot.Definition, source, ct);
            return await store.RetainAsync(owner, destination, content.ContentType, content.Bytes, sessionId, expectedRevision, expectedSha256, ct);
        }, cancellationToken);
    }

    public async ValueTask<AgentWorkspaceCheckout> CheckoutAsync(Guid sessionId, string source, string? destination = null,
        long? expectedRevision = null, string? expectedSha256 = null, CancellationToken cancellationToken = default)
    {
        var snapshot = await SessionAsync(sessionId, true, cancellationToken);
        var owner = snapshot.AgentInstanceId ?? throw AgentCoreErrors.Validation("Durable home is unavailable for this execution.");
        source = AgentHomePath.Normalize(source);
        destination = WorkspaceLogicalPath.Resolve(destination ?? Path.GetFileName(source), sessionId);
        if (!destination.StartsWith("/workspace/working/", StringComparison.Ordinal)) throw AgentCoreErrors.Forbidden("Checkout requires a destination under /workspace/working.");
        ValidateExpected(expectedRevision, expectedSha256);
        return await lifecycle.WithInstanceAsync(owner, async ct =>
        {
            await RequireAsync(owner, true, ct);
            await SessionAsync(sessionId, true, ct);
            var content = await store.ReadAsync(owner, null, source, ct);
            if ((expectedRevision.HasValue && expectedRevision != content.Item.Revision)
                || (expectedSha256 is not null && expectedSha256 != content.Item.Sha256Hex))
                throw AgentCoreErrors.Conflict("Home source changed. Read its current metadata before checkout.");
            await scratch.EnsureAsync(sessionId, snapshot.Definition, ct);
            await scratch.WriteNewAsync(sessionId, destination, content.Bytes, ct);
            return new AgentWorkspaceCheckout(content.Item, destination, content.Bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(content.Bytes)).ToLowerInvariant());
        }, cancellationToken);
    }

    // Detached executions may only retrieve when Core supplies a proven managed owner. Scratch boundary actions require a session.
    public async ValueTask<IReadOnlyList<WorkspaceNode>> ListNodesAsync(Guid owner, string prefix, CancellationToken ct)
    {
        var page = await ListAsync(owner, prefix, cancellationToken: ct);
        var nodes = new Dictionary<string, WorkspaceNode>(StringComparer.Ordinal);
        foreach (var item in page.Items)
        {
            if (item.LogicalPath == prefix) nodes[prefix] = new(prefix, false, item.ByteSize, false);
            else
            {
                var rest = item.LogicalPath[(prefix.Length + 1)..];
                var slash = rest.IndexOf('/');
                var path = slash < 0 ? item.LogicalPath : prefix + "/" + rest[..slash];
                nodes[path] = new(path, slash >= 0, slash >= 0 ? 0 : item.ByteSize, false);
            }
        }
        return nodes.Values.OrderBy(n => n.LogicalPath, StringComparer.Ordinal).ToArray();
    }

    private async ValueTask<AgentInstance> RequireAsync(Guid owner, bool mutation, CancellationToken ct)
    {
        var instance = await instances.FindAsync(owner, ct) ?? throw AgentCoreErrors.NotFound("Agent instance was not found.");
        if (instance.Compatibility) throw AgentCoreErrors.Validation("Durable home is unavailable for compatibility instances.");
        if (mutation && instance.Lifecycle != AgentInstanceLifecycle.Active) throw AgentCoreErrors.Conflict("Archived workspace is read-only.");
        return instance;
    }

    private async ValueTask<SessionSnapshot> SessionAsync(Guid id, bool mutation, CancellationToken ct)
    {
        var snapshot = await sessions.LoadMetadataAsync(id, ct) ?? throw AgentCoreErrors.NotFound("Session was not found.");
        if (snapshot.DurablyDeletedAt is not null) throw AgentCoreErrors.NotFound("Session was not found.");
        if (mutation && (snapshot.Status == SessionStatus.Ended || snapshot.ArchivedAt is not null))
            throw AgentCoreErrors.Conflict("Ended or archived sessions cannot change workspace state.");
        return snapshot;
    }

    private static void ValidateExpected(long? revision, string? sha)
    {
        if (revision is < 1 || sha is not null && (sha.Length != 64 || !sha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw AgentCoreErrors.Validation("Expected revision/hash is invalid.");
    }
}
