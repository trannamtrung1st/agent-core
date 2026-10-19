using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private static IQueryable<AgentRunRecord> UnblockedTenantInput(AgentCoreDbContext db)
    {
        var ordered = db.AgentRuns.Select(row => new { Row = row, IsReport = db.BackgroundCompletionReceipts.Any(receipt => receipt.ProfileId == null && receipt.ParentActivationId == row.ActivationId),
            Ordinal = db.ActivationSourceEntries.Where(source => source.ActivationId == row.ActivationId)
                .Join(db.Entries, source => source.EntryId, entry => entry.EntryId, (source, entry) => (long?)entry.EntrySequence).Min() ?? long.MaxValue });
        return ordered.Where(candidate => candidate.Row.Status == (int)AgentRunStatus.Running || !ordered.Any(other =>
            other.Row.TenantId == candidate.Row.TenantId && other.Row.SessionId == candidate.Row.SessionId && other.Row.AgentRunId != candidate.Row.AgentRunId
            && other.Row.Status != (int)AgentRunStatus.Completed && other.Row.Status != (int)AgentRunStatus.Failed && other.Row.Status != (int)AgentRunStatus.Cancelled
            && (other.Row.Status == (int)AgentRunStatus.Running || other.Row.Status == (int)AgentRunStatus.WaitingForSignal
                || candidate.IsReport && !other.IsReport
                || candidate.IsReport == other.IsReport && other.Row.Status != (int)AgentRunStatus.WaitingForApproval && (other.Row.CreatedAtUtc < candidate.Row.CreatedAtUtc
                    || other.Row.CreatedAtUtc == candidate.Row.CreatedAtUtc && (other.Ordinal < candidate.Ordinal
                        || other.Ordinal == candidate.Ordinal && other.Row.AgentRunId.CompareTo(candidate.Row.AgentRunId) < 0)))))
            .Select(candidate => candidate.Row);
    }
}
