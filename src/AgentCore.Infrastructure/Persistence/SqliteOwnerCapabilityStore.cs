using AgentCore.Application.Ports;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed class SqliteOwnerCapabilityStore(IDbContextFactory<AgentCoreDbContext> contexts) : IOwnerCapabilityStore
{
    public async ValueTask SaveHashAsync(string tokenHash, DateTimeOffset createdAt, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        if (await db.OwnerCapabilities.AnyAsync(row => row.TokenHash == tokenHash, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        db.OwnerCapabilities.Add(new OwnerCapabilityRecord
        {
            TokenHash = tokenHash,
            CreatedAtUtc = createdAt.ToUnixTimeMilliseconds()
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> ContainsHashAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.OwnerCapabilities
            .AsNoTracking()
            .AnyAsync(row => row.TokenHash == tokenHash, cancellationToken)
            .ConfigureAwait(false);
    }
}
