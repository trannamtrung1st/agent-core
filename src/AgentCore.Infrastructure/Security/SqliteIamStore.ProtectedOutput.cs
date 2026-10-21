using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private static async Task SettleRefusedTenantOutput(AgentCoreDbContext db, AgentRun before, AgentRun failed, CancellationToken ct)
    {
        if (before.ResponseId is null || failed.Status != AgentRunStatus.Failed || failed.Owner != before.Owner || failed.SessionId != before.SessionId)
            throw new AccessDeniedException("tenant_runtime_scope_mismatch");
        var session = await db.Set<TenantSessionRecord>().SingleAsync(s => s.TenantId == before.Owner.TenantId!.Value && s.SessionId == before.SessionId.ToString("D")
            && s.ExecutorPrincipalId == before.Owner.ExecutorPrincipalId && s.AgentInstanceId == before.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false);
        var rows = await db.Entries.Where(e => e.SessionId == session.SessionId && e.ResponseId == before.ResponseId.Value.ToString("D")
            && e.Role == nameof(ConversationRole.Assistant) && e.Status == nameof(EntryStatus.Streaming)).ToArrayAsync(ct).ConfigureAwait(false);
        if (rows.Length == 0) return;
        foreach (var entry in rows)
        {
            entry.Status = nameof(EntryStatus.Failed); entry.CompletedAtUtc ??= failed.UpdatedAtUtc.ToUnixTimeMilliseconds();
            entry.FinishReason = null; entry.InterruptReason = null;
        }
        var metadata = await db.Sessions.SingleAsync(s => s.SessionId == session.SessionId, ct).ConfigureAwait(false);
        metadata.Revision++; metadata.UpdatedAtUtc = failed.UpdatedAtUtc.ToUnixTimeMilliseconds();
    }
}
