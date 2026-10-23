using AgentCore.Application.Admin;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

// Metadata queries for the canonical owning deletion transactions; no mutation or cleanup authority.
internal static class TenantDeletionReferenceQueries
{
    internal static async Task<AdminDeletionReferenceCounts> CountInstanceAsync(
        AgentCoreDbContext db,
        string instanceId,
        CancellationToken cancellationToken, Guid tenant)
    {
        var runs = db.AgentRuns.Where(item => item.AgentInstanceId == instanceId);
        var deletedAutomations = DeletedAutomations(db, instanceId, tenant).Select(r => r.AutomationId);
        return new AdminDeletionReferenceCounts(
            await db.Sessions.CountAsync(
                item => item.AgentInstanceId == instanceId && item.DurablyDeletedAtUtc == null,
                cancellationToken).ConfigureAwait(false),
            await db.StructuredMemories.CountAsync(
                item => item.OwnerInstanceId == instanceId && item.Scope == 1 && item.Status == 0,
                cancellationToken).ConfigureAwait(false) + await db.Set<AgentCore.Infrastructure.Security.TenantLearnedMemoryRecord>()
                    .CountAsync(i => i.TenantId == tenant && i.AgentInstanceId == instanceId && i.Status != 2, cancellationToken).ConfigureAwait(false),
            await db.Automations.IgnoreQueryFilters().Where(a => a.TenantId == null || a.TenantId == tenant).CountAsync(
                item => item.AgentInstanceId == instanceId && !deletedAutomations.Contains(item.AutomationId),
                cancellationToken).ConfigureAwait(false),
            await db.TriggerOccurrences.IgnoreQueryFilters().Where(a => a.TenantId == null || a.TenantId == tenant).CountAsync(
                item => item.AgentInstanceId == instanceId && !(deletedAutomations.Contains(item.AutomationId!)

                    && item.Disposition == (int)OccurrenceRoutingDisposition.Rejected && item.AcceptedAgentRunId == null),
                cancellationToken).ConfigureAwait(false),
            await runs.CountAsync(cancellationToken).ConfigureAwait(false),
            await runs.CountAsync(item => item.Status == (int)AgentRunStatus.WaitingForApproval, cancellationToken).ConfigureAwait(false),
            await db.Activations.CountAsync(item => item.AgentInstanceId == instanceId, cancellationToken).ConfigureAwait(false),
            Instances: 0);
    }

    private static IQueryable<AutomationRecord> DeletedAutomations(AgentCoreDbContext db, string instanceId, Guid tenant) =>
        db.Automations.IgnoreQueryFilters().Where(r => (r.TenantId == null || r.TenantId == tenant) && r.AgentInstanceId == instanceId
            && r.Status == (int)AutomationStatus.Cancelled);

    internal static async Task<AdminDeletionReferenceCounts> CountDefinitionAsync(
        AgentCoreDbContext db,
        string definitionId,
        CancellationToken cancellationToken)
    {
        var sessionIds = db.Sessions.Where(item => item.AgentId == definitionId).Select(item => item.SessionId);
        var runs = db.AgentRuns.Where(item => sessionIds.Contains(item.SessionId));
        return new AdminDeletionReferenceCounts(
            await db.Sessions.CountAsync(
                item => item.AgentId == definitionId && item.DurablyDeletedAtUtc == null,
                cancellationToken).ConfigureAwait(false),
            LearnedMemoryItems: 0,
            Automations: 0,
            TriggerOccurrences: 0,
            await runs.CountAsync(cancellationToken).ConfigureAwait(false),
            await runs.CountAsync(item => item.Status == (int)AgentRunStatus.WaitingForApproval, cancellationToken).ConfigureAwait(false),
            await db.Activations.CountAsync(item => sessionIds.Contains(item.SessionId), cancellationToken).ConfigureAwait(false),
            await db.AgentInstances.CountAsync(item => item.DefinitionId == definitionId
                && !db.Set<AgentCore.Infrastructure.Security.IamSystemIdentityRow>().Any(i => i.AgentInstanceId == item.InstanceId && i.TenantId != null && i.Status == AgentCore.Domain.Security.IdentityStatus.Removed), cancellationToken)
                .ConfigureAwait(false));
    }

}
