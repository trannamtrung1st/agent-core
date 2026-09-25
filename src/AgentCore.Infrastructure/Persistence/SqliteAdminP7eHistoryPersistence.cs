using AgentCore.Application.Admin;
using AgentCore.Application.Sessions;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Memory;
using AgentCore.Domain.Triggers;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

internal static class SqliteAdminP7eHistoryPersistence
{
    internal static async Task TombstoneActiveMemoryAsync(
        AgentCoreDbContext db,
        AdminLearnedMemoryScope scope,
        Guid memoryId,
        Guid instanceId,
        Guid profileId,
        Guid? sessionId,
        CancellationToken cancellationToken)
    {
        var memoryIdText = memoryId.ToString("D");
        IQueryable<StructuredMemoryRecord> query = db.StructuredMemories.Where(item =>
            item.MemoryId == memoryIdText && item.Status == (int)MemoryItemStatus.Active);
        query = scope switch
        {
            AdminLearnedMemoryScope.Session when sessionId is Guid resolvedSessionId =>
                query.Where(item =>
                    item.Scope == (int)MemoryScope.Session
                    && item.SessionId == resolvedSessionId.ToString("D")),
            AdminLearnedMemoryScope.IdentityUser =>
                query.Where(item =>
                    item.Scope == (int)MemoryScope.IdentityUser
                    && item.OwnerInstanceId == instanceId.ToString("D")
                    && item.OwnerProfileId == profileId.ToString("D")),
            AdminLearnedMemoryScope.User =>
                query.Where(item =>
                    item.Scope == (int)MemoryScope.User
                    && item.OwnerProfileId == profileId.ToString("D")),
            AdminLearnedMemoryScope.Session => throw AgentCoreErrors.Validation("sessionId is required for session scope."),
            _ => throw AgentCoreErrors.Validation("scope is invalid.")
        };

        var row = await query.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            throw AgentCoreErrors.NotFound("Memory was not found.");
        }

        row.Status = (int)MemoryItemStatus.Deleted;
        row.Subject = string.Empty;
        row.Content = string.Empty;
        row.SubjectKey = string.Empty;
    }

    internal static async Task<int> ResetActiveIdentityUserScopeAsync(
        AgentCoreDbContext db,
        Guid instanceId,
        Guid profileId,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken) =>
        await ResetScopeAsync(
            db,
            db.StructuredMemories.Where(item =>
                item.Scope == (int)MemoryScope.IdentityUser
                && item.OwnerInstanceId == instanceId.ToString("D")
                && item.OwnerProfileId == profileId.ToString("D")
                && item.Status == (int)MemoryItemStatus.Active),
            updatedAtUtc,
            cancellationToken).ConfigureAwait(false);

    internal static async Task<int> ResetActiveSessionScopeAsync(
        AgentCoreDbContext db,
        Guid sessionId,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken) =>
        await ResetScopeAsync(
            db,
            db.StructuredMemories.Where(item =>
                item.SessionId == sessionId.ToString("D")
                && item.Scope == (int)MemoryScope.Session
                && item.Status == (int)MemoryItemStatus.Active),
            updatedAtUtc,
            cancellationToken).ConfigureAwait(false);

    internal static async Task<int> ResetActiveUserScopeAsync(
        AgentCoreDbContext db,
        Guid profileId,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken) =>
        await ResetScopeAsync(
            db,
            db.StructuredMemories.Where(item =>
                item.Scope == (int)MemoryScope.User
                && item.OwnerProfileId == profileId.ToString("D")
                && item.Status == (int)MemoryItemStatus.Active),
            updatedAtUtc,
            cancellationToken).ConfigureAwait(false);

    internal static async Task<TriggerRegistration> CancelRegistrationAsync(
        AgentCoreDbContext db,
        TriggerOwner owner,
        Guid registrationId,
        long expectedRevision,
        DateTimeOffset cancelledAt,
        CancellationToken cancellationToken)
    {
        var currentRow = await db.TriggerRegistrations
            .AsNoTracking()
            .FirstOrDefaultAsync(
                row => row.RegistrationId == registrationId.ToString("D")
                    && row.AgentInstanceId == owner.AgentInstanceId.ToString("D")
                    && row.ProfileId == owner.ProfileId.ToString("D"),
                cancellationToken)
            .ConfigureAwait(false);
        if (currentRow is null)
        {
            throw AgentCoreErrors.NotFound("Trigger registration was not found.");
        }

        var current = TriggerStoreMapping.ToRegistration(currentRow);
        var cancelled = TriggerRegistrationMutations.Cancel(current, expectedRevision, cancelledAt);
        if (cancelled.Revision == current.Revision)
        {
            return current;
        }

        var rows = await db.TriggerRegistrations
            .Where(row => row.RegistrationId == registrationId.ToString("D")
                && row.AgentInstanceId == owner.AgentInstanceId.ToString("D")
                && row.ProfileId == owner.ProfileId.ToString("D")
                && row.Revision == expectedRevision
                && (row.Status == (int)TriggerRegistrationStatus.Active
                    || row.Status == (int)TriggerRegistrationStatus.SuspendedPolicy))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.Status, (int)TriggerRegistrationStatus.Cancelled)
                    .SetProperty(row => row.Revision, cancelled.Revision)
                    .SetProperty(row => row.UpdatedAtUtc, cancelled.Provenance.UpdatedAt.ToUnixTimeMilliseconds()),
                cancellationToken)
            .ConfigureAwait(false);
        if (rows != 1)
        {
            throw AgentCoreErrors.Conflict("Trigger registration revision is stale.");
        }

        return cancelled;
    }

    private static async Task<int> ResetScopeAsync(
        AgentCoreDbContext db,
        IQueryable<StructuredMemoryRecord> query,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        var rows = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        var updatedMs = updatedAtUtc.ToUnixTimeMilliseconds();
        foreach (var row in rows)
        {
            row.Status = (int)MemoryItemStatus.Deleted;
            row.Subject = string.Empty;
            row.Content = string.Empty;
            row.SubjectKey = string.Empty;
            row.UpdatedAtUtc = updatedMs;
        }

        return rows.Count;
    }
}
