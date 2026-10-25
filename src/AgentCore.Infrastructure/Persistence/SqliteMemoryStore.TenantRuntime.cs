using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class SqliteMemoryStore
{
    internal async Task StageTenantRuntimeSaveAsync(AgentCoreDbContext db, TenantRuntimeFrame frame, SessionSnapshot snapshot, long expectedRevision, CancellationToken ct, bool foreground = false, bool contextSwitch = false)
    {
        var owner = await db.Set<TenantSessionRecord>().AsNoTracking().SingleOrDefaultAsync(s => s.SessionId == frame.SessionId.ToString("D") && s.TenantId == frame.Owner.TenantId, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("session_unavailable");
        if (owner.AgentInstanceId != frame.Owner.AgentInstanceId.ToString("D") || owner.ExecutorPrincipalId != frame.Owner.ExecutorPrincipalId) throw new AccessDeniedException("tenant_runtime_scope_mismatch");
        var row = await db.Sessions.Include(s => s.Snapshot).SingleAsync(s => s.SessionId == owner.SessionId, ct).ConfigureAwait(false);
        var matched = await LoadMatchingEntryRowsAsync(db, owner.SessionId, snapshot.Entries, ct).ConfigureAwait(false);
        var current = ToSnapshot(row, matched); TenantRuntimeRules.ValidateSnapshot(frame, current, snapshot, foreground, contextSwitch);
        if (row.Revision == snapshot.Revision && MemoryStoreSemantics.SameContent(current, snapshot)) return;
        if (row.Revision != expectedRevision || snapshot.Revision != expectedRevision + 1) throw AgentCoreErrors.Conflict("Stale Session revision.");
        var producerRow = await db.AgentRuns.SingleAsync(r => r.TenantId == frame.Owner.TenantId && r.AgentRunId == frame.RunId.ToString("D"), ct).ConfigureAwait(false);
        ApplySession(row, snapshot); await UpsertEntriesAsync(db, row, snapshot, ct).ConfigureAwait(false);
        await TenantCoreEventProduction.StageSessionAsync(db, current, snapshot, owner.Domain(), AgentRunStoreMapping.ToDomain(producerRow), ct).ConfigureAwait(false);
    }
}
