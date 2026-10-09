using AgentCore.Application.Ports;
using AgentCore.Domain.Memory;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class SqliteStructuredMemoryStore
{
    public async ValueTask<MemoryResolutionResult> ResolveOpenLoopsAsync(
        TrustedMemoryOwner session, TrustedIdentityUserOwner? identity, TrustedUserOwner? user,
        string subjectKey, DateTimeOffset resolvedAt, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var eligible = db.StructuredMemories.Where(row => row.Kind == (int)MemoryKind.OpenLoop
            && row.SubjectKey == subjectKey
            && (row.Status == (int)MemoryItemStatus.Active || row.Status == (int)MemoryItemStatus.Resolved));
        var matches = new List<StructuredMemoryRecord>(3);
        async Task AddAsync(IQueryable<StructuredMemoryRecord> scope)
        {
            var row = await scope.OrderByDescending(row => row.Status == (int)MemoryItemStatus.Active)
                .ThenByDescending(row => row.UpdatedAtUtc).ThenByDescending(row => row.MemoryId)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (row is not null) matches.Add(row);
        }
        await AddAsync(eligible.Where(row => row.Scope == (int)MemoryScope.Session
            && row.SessionId == session.SessionId.ToString("D"))).ConfigureAwait(false);
        if (identity is not null)
            await AddAsync(eligible.Where(row => row.Scope == (int)MemoryScope.IdentityUser
                && row.OwnerInstanceId == identity.InstanceId.ToString("D")
                && row.OwnerProfileId == identity.ProfileId.ToString("D"))).ConfigureAwait(false);
        if (user is not null)
            await AddAsync(eligible.Where(row => row.Scope == (int)MemoryScope.User
                && row.OwnerProfileId == user.ProfileId.ToString("D"))).ConfigureAwait(false);
        var changed = false;
        foreach (var row in matches)
        {
            if (row.Status != (int)MemoryItemStatus.Active) continue;
            changed = true;
            row.Status = (int)MemoryItemStatus.Resolved;
            row.UpdatedAtUtc = resolvedAt.ToUnixTimeMilliseconds();
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new MemoryResolutionResult(changed, matches.Select(Map).ToArray());
    }
}
