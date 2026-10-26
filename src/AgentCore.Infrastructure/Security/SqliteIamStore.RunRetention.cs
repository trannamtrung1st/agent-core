using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private static async Task RetireRunContent(AgentCoreDbContext db, Guid tenant, string session, DateTimeOffset at, CancellationToken ct)
    {
        var rows = await db.AgentRuns.Where(r => r.TenantId == tenant && r.SessionId == session).ToArrayAsync(ct).ConfigureAwait(false);
        var ids = rows.Select(r => r.AgentRunId).ToArray();
        var reports = await db.BackgroundCompletionReceipts.Where(r => ids.Contains(r.ChildAgentRunId)
            && r.Status != (int)CompletionInboxStatus.Handled && r.Status != (int)CompletionInboxStatus.Delivered && r.Status != (int)CompletionInboxStatus.Skipped)
            .Select(r => r.ChildAgentRunId).ToArrayAsync(ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            var run = AgentRunStoreMapping.ToDomain(row);
            var minimized = run.RetireConversationContent(at, reports.Contains(row.AgentRunId));
            if (!ReferenceEquals(run, minimized)) AgentRunStoreMapping.Apply(row, minimized);
        }
    }
}
