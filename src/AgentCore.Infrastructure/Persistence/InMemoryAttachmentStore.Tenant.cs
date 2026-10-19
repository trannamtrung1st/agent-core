using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryAttachmentStore : ITenantAttachmentStore
{
    private InMemoryMemoryStore? _tenantSessions;
    internal object TenantGate { get; } = new();
    private readonly HashSet<Guid> _cleaned = [];
    internal InMemoryAttachmentStore BindTenantAuthority(InMemoryMemoryStore sessions)
    {
        if (_tenantSessions is not null && !ReferenceEquals(sessions, _tenantSessions)) throw new InvalidOperationException("Attachments must share Session ownership.");
        _tenantSessions = sessions; return this;
    }
    private TenantSession RequireTenantOwner(TenantSessionRoot owner)
    {
        var saved = _tenantSessions?.ReadTenantSession(owner.TenantId, owner.SessionId);
        if (saved is null || saved.Owner != owner || saved.Snapshot.DurablyDeletedAt is not null) throw new AccessDeniedException("resource_unavailable");
        return saved;
    }
    private void RequireUnowned(Guid session)
    {
        if (_tenantSessions?.RetainedTenantSessionOwnerSnapshot(session) is not null || _items.Values.Any(i => i.SessionId == session && i.TenantId is not null))
            throw new AccessDeniedException("tenant_context_required");
    }
    internal bool OwnsTenantAttachment(Guid tenant, Guid id) => _items.TryGetValue(id, out var row) && row.TenantId == tenant
        && _tenantSessions?.OwnsTenantSession(tenant, row.SessionId) == true;
    internal AttachmentRecord? FindUpload(Guid tenant, Guid session, Guid request) => _items.Values.SingleOrDefault(a => a.TenantId == tenant && a.SessionId == session && a.UploadRequestId == request);
    internal AttachmentRecord PublishTenant(TenantSessionRoot owner, TenantAttachmentUpload upload, CancellationToken ct)
    {
        lock (TenantGate)
        {
            RequireTenantOwner(owner); var item = upload.Record; TenantAttachmentRules.Content(item, upload.Bytes);
            if (item != TenantAttachmentRules.Pending(owner, item.AttachmentId, item.UploadRequestId!.Value, item.UploadedByPrincipalId!.Value,
                item.DisplayName, item.ContentType, item.Readable, upload.Bytes, item.CreatedAt)) throw new SecurityConflictException("attachment_upload_mismatch");
            if (FindUpload(owner.TenantId, owner.SessionId, item.UploadRequestId.Value) is { } prior)
                return TenantAttachmentRules.SameUpload(prior, item) ? prior : throw new SecurityConflictException("attachment_upload_mismatch");
            if (_items.ContainsKey(item.AttachmentId)) throw new SecurityConflictException("identity_conflict");
            if (_items.Values.Where(a => a.TenantId == owner.TenantId && a.SessionId == owner.SessionId && _blobs.ContainsKey(a.AttachmentId)).Sum(a => a.ByteSize) + item.ByteSize > AttachmentLimits.MaxBytesSession)
                throw TenantAttachmentRules.Quota();
            ct.ThrowIfCancellationRequested(); _blobs[item.AttachmentId] = upload.Bytes.ToArray(); _items[item.AttachmentId] = item; return item;
        }
    }
    internal AttachmentRecord? RetainedTenant(Guid tenant, Guid session, Guid id) => _items.TryGetValue(id, out var item) && item.TenantId == tenant && item.SessionId == session ? item : null;
    internal void AbortTenant(AttachmentRecord item, CancellationToken ct)
    {
        lock (TenantGate) { ct.ThrowIfCancellationRequested(); _items[item.AttachmentId] = item with { ExpiresAt = time.GetUtcNow(), StageForNextTurn = false }; }
    }
    public async ValueTask<TenantAttachmentUpload> PrepareUploadTenantAsync(TenantSessionRoot owner, Guid id, Guid request, Guid actor, string name, string type, Stream content, CancellationToken ct = default)
    {
        RequireTenantOwner(owner); var result = await TenantAttachmentIntake.Read(owner, id, request, actor, name, type, content, time, ct).ConfigureAwait(false);
        RequireTenantOwner(owner); return result;
    }
    public ValueTask<bool> OwnsTenantAsync(Guid tenant, Guid session, Guid attachment, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(RetainedTenant(tenant, session, attachment) is not null && OwnsTenantAttachment(tenant, attachment)); }
    public ValueTask<AttachmentRecord?> FindTenantAsync(Guid tenant, Guid session, Guid attachment, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); var item = RetainedTenant(tenant, session, attachment);
        return ValueTask.FromResult(item is not null && OwnsTenantAttachment(tenant, attachment) && TenantAttachmentRules.Available(item, time.GetUtcNow()) ? item : null);
    }
    public ValueTask<TenantAttachmentPage> ListTenantAsync(Guid tenant, Guid session, IReadOnlyList<Guid>? allowed, Guid? before, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); TenantAttachmentRules.Query(tenant, session, limit); var scope = allowed?.ToHashSet();
        var rows = _items.Values.Where(a => a.TenantId == tenant && a.SessionId == session && (scope is null || scope.Contains(a.AttachmentId))
            && OwnsTenantAttachment(tenant, a.AttachmentId) && TenantAttachmentRules.Available(a, time.GetUtcNow()))
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.AttachmentId.ToString("D"), StringComparer.Ordinal).ToArray();
        var offset = before is null ? 0 : Array.FindIndex(rows, a => a.AttachmentId == before) + 1;
        if (before is not null && offset == 0) throw new AccessDeniedException("resource_unavailable");
        var page = rows.Skip(offset).Take(limit + 1).ToArray(); return ValueTask.FromResult(new TenantAttachmentPage(page.Take(limit).ToArray(), page.Length > limit ? page[limit - 1].AttachmentId : null, page.Length > limit));
    }
    public async ValueTask<byte[]> ReadTenantContentAsync(Guid tenant, Guid session, Guid attachment, CancellationToken ct = default)
    {
        var item = await FindTenantAsync(tenant, session, attachment, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        if (!_blobs.TryGetValue(attachment, out var bytes)) throw new AccessDeniedException("attachment_content_unavailable");
        var frozen = bytes.ToArray(); TenantAttachmentRules.Content(item, frozen); ct.ThrowIfCancellationRequested(); return frozen;
    }
}
