using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed class SqliteAgentInstanceStore(IDbContextFactory<AgentCoreDbContext> contexts) : IAgentInstanceStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public async ValueTask<IReadOnlyList<AgentInstance>> ListAsync(int limit, CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.AgentInstances.AsNoTracking()
            .OrderBy(item => item.DefinitionId)
            .ThenBy(item => item.InstanceId)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(Map).ToArray();
    }

    public async ValueTask<AgentInstance?> FindAsync(Guid instanceId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.AgentInstances.AsNoTracking()
            .SingleOrDefaultAsync(item => item.InstanceId == instanceId.ToString("D"), cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async ValueTask<AgentInstance?> FindCompatibilityAsync(
        string definitionId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.AgentInstances.AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Compatibility && item.DefinitionId == definitionId,
                cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async ValueTask InsertAsync(AgentInstance instance, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.AgentInstances.Add(Map(instance));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            throw new AgentCoreException("Conflict", "Agent instance already exists.", 409);
        }
    }

    public async ValueTask UpdateActiveVersionAsync(
        Guid instanceId,
        int activeVersion,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.AgentInstances
            .SingleOrDefaultAsync(item => item.InstanceId == instanceId.ToString("D"), cancellationToken)
            .ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Agent instance was not found.");
        row.ActiveVersion = activeVersion;
        row.UpdatedAtUtc = updatedAt.ToUnixTimeMilliseconds();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static AgentInstance Map(AgentInstanceRecord row) =>
        new(
            Guid.Parse(row.InstanceId),
            row.DefinitionId,
            row.ActiveVersion,
            JsonSerializer.Deserialize<AgentIdentity>(row.PersonaJson, Json)
                ?? throw AgentCoreErrors.Persistence("Stored agent persona was empty."),
            Enum.Parse<AgentInstanceLifecycle>(row.Lifecycle),
            DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAtUtc),
            DateTimeOffset.FromUnixTimeMilliseconds(row.UpdatedAtUtc),
            row.Compatibility);

    private static AgentInstanceRecord Map(AgentInstance instance) =>
        new()
        {
            InstanceId = instance.InstanceId.ToString("D"),
            DefinitionId = instance.DefinitionId,
            ActiveVersion = instance.ActiveVersion,
            PersonaJson = JsonSerializer.Serialize(instance.Persona, Json),
            Lifecycle = instance.Lifecycle.ToString(),
            CreatedAtUtc = instance.CreatedAt.ToUnixTimeMilliseconds(),
            UpdatedAtUtc = instance.UpdatedAt.ToUnixTimeMilliseconds(),
            Compatibility = instance.Compatibility
        };
}
