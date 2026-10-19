using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Security;

// IMemoryStore compatibility is confined to one actual tenant Run lease, never a global Profile.
public sealed class TenantRuntimeMemoryStore(ITenantRuntimeStore storage, TenantRuntimeFrame frame, IIdGenerator? ids = null) : IMemoryStore, ITenantRuntimeReceiptWriter, ITenantRuntimeForegroundWriter, ITenantRuntimeInputWriter
{
    private void Scope(Guid session) { TenantRuntimeRules.RequireOwner(frame.Owner); if (session != frame.SessionId) throw new AccessDeniedException("tenant_runtime_scope_mismatch"); }
    public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default)
    { Scope(sessionId); return storage.LoadTenantRuntimeAsync(frame, cancellationToken); }
    public async ValueTask<SessionSnapshot?> LoadMetadataAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        (await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false)) is { } snapshot ? snapshot with { Entries = [] } : null;
    public ValueTask SaveAsync(SessionSnapshot snapshot, long expectedRevision, CancellationToken cancellationToken = default)
    { Scope(snapshot.SessionId); return storage.SaveTenantRuntimeAsync(frame, snapshot, expectedRevision, cancellationToken); }
    public ValueTask SaveReceiptAsync(SessionSnapshot snapshot, long expectedRevision, TenantDeliveryReceipt receipt, CancellationToken ct)
    { Scope(snapshot.SessionId); return ((ITenantDeliveryReceiptStore)storage).SaveTenantRuntimeReceiptAsync(frame, snapshot, expectedRevision, receipt, ct); }
    public ValueTask SaveForegroundAsync(SessionSnapshot snapshot, long expectedRevision, TenantSessionForeground request, CancellationToken ct)
    { Scope(snapshot.SessionId); return ((ITenantSessionForegroundStore)storage).SaveTenantRuntimeForegroundAsync(frame, snapshot, expectedRevision, request, (ids ?? throw new AccessDeniedException("tenant_runtime_unavailable")).NewId(), ct); }
    public ValueTask<TenantHumanInputCommit> SaveInputAsync(SessionSnapshot snapshot, long expectedRevision, TenantHumanInput input, CancellationToken ct)
    { Scope(snapshot.SessionId); return ((ITenantRuntimeInputStore)storage).SaveTenantRuntimeInputAsync(frame, snapshot, expectedRevision, input,
        (ids ?? throw new AccessDeniedException("tenant_runtime_unavailable")).NewId(), ct); }
    public async ValueTask<IReadOnlyList<ConversationEntry>> ReadHistoryAsync(Guid sessionId, long afterEntrySequence, int limit, CancellationToken cancellationToken = default) =>
        (await ReadHistoryPageAsync(sessionId, afterEntrySequence, null, limit, cancellationToken).ConfigureAwait(false))?.Items ?? [];
    public ValueTask<ConversationHistoryPage?> ReadHistoryPageAsync(Guid sessionId, long? afterEntrySequence, long? beforeEntrySequence, int limit, CancellationToken cancellationToken = default)
    { Scope(sessionId); return storage.ReadTenantRuntimeHistoryAsync(frame, afterEntrySequence, beforeEntrySequence, limit, cancellationToken); }
    public ValueTask<UserProfile?> LoadProfileAsync(Guid profileId, CancellationToken cancellationToken = default) => throw new AccessDeniedException("tenant_profile_unavailable");
    public ValueTask SaveProfileAsync(UserProfile profile, long expectedRevision, CancellationToken cancellationToken = default) => throw new AccessDeniedException("tenant_profile_unavailable");
    public ValueTask RecoverCrashedSessionsAsync(CancellationToken cancellationToken = default) => throw new AccessDeniedException("tenant_recovery_requires_run_context");
}
