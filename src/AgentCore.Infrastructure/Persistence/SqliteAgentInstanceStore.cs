using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed class SqliteAgentInstanceStore(
    IDbContextFactory<AgentCoreDbContext> contexts,
    IIdGenerator ids) : IAgentInstanceStore
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

    public async ValueTask<AgentInstance> InsertManagedWithHistoryAsync(
        AgentInstance instance,
        AdminEventAppend historyAppend,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existingEvent = await AdminEventPersistence.TryGetByOperationIdAsync(
                db,
                historyAppend.OperationId,
                cancellationToken)
            .ConfigureAwait(false);
        if (existingEvent is not null)
        {
            return await ResolveManagedInstanceCreatedByOperationEventAsync(
                    existingEvent,
                    historyAppend,
                    instance,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        db.AgentInstances.Add(Map(instance));
        AdminEventPersistence.StageAppend(db, historyAppend, ids.NewId());
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            var raced = await AdminEventPersistence.TryGetByOperationIdAsync(
                    db,
                    historyAppend.OperationId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (raced is not null)
            {
                return await ResolveManagedInstanceCreatedByOperationEventAsync(
                        raced,
                        historyAppend,
                        instance,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            throw new AgentCoreException("Conflict", "Agent instance already exists.", 409);
        }

        return instance;
    }

    private async ValueTask<AgentInstance> ResolveManagedInstanceCreatedByOperationEventAsync(
        AdminEvent existingEvent,
        AdminEventAppend historyAppend,
        AgentInstance instance,
        CancellationToken cancellationToken)
    {
        if (existingEvent.Operation != AdminEventOperationKind.ManagedInstanceCreated)
        {
            throw AgentCoreErrors.Conflict("Operation id is already used for a different admin event.");
        }

        EnsureAgentInstanceHistoryTargetMatches(existingEvent, historyAppend, instance.InstanceId);

        if (!Guid.TryParse(existingEvent.TargetId, out var instanceId))
        {
            throw AgentCoreErrors.Conflict("Managed instance history is missing an instance target id.");
        }

        return await FindAsync(instanceId, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.Conflict("Managed instance history references a missing instance.");
    }

    public async ValueTask<AgentInstance> UpdateActiveVersionWithHistoryAsync(
        AgentInstanceRevisionUpdate update,
        DateTimeOffset updatedAt,
        AdminEventAppend historyAppend,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existingEvent = await AdminEventPersistence.TryGetByOperationIdAsync(
                db,
                historyAppend.OperationId,
                cancellationToken)
            .ConfigureAwait(false);
        if (existingEvent is not null)
        {
            return await ResolveInstanceDefinitionVersionChangedByOperationEventAsync(
                    existingEvent,
                    historyAppend,
                    update,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var row = await db.AgentInstances
            .SingleOrDefaultAsync(item => item.InstanceId == update.InstanceId.ToString("D"), cancellationToken)
            .ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Agent instance was not found.");
        if (row.Revision != update.ExpectedRevision)
        {
            throw new AgentCoreException("Conflict", "Agent instance revision is stale.", 409);
        }

        if (row.Compatibility)
        {
            throw new AgentCoreException("Validation", "Compatibility instances cannot change active version.", 400);
        }

        if (update.ActiveVersion is not int activeVersion)
        {
            throw AgentCoreErrors.Validation("Active version is required.");
        }

        row.ActiveVersion = activeVersion;
        row.UpdatedAtUtc = updatedAt.ToUnixTimeMilliseconds();
        row.Revision++;
        AdminEventPersistence.StageAppend(db, historyAppend, ids.NewId());
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            var raced = await AdminEventPersistence.TryGetByOperationIdAsync(
                    db,
                    historyAppend.OperationId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (raced is not null)
            {
                return await ResolveInstanceDefinitionVersionChangedByOperationEventAsync(
                        raced,
                        historyAppend,
                        update,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            throw new AgentCoreException("Conflict", "Agent instance revision is stale.", 409);
        }

        return Map(row);
    }

    public async ValueTask<AgentInstance> UpdatePersonaWithHistoryAsync(
        AgentInstanceRevisionUpdate update,
        DateTimeOffset updatedAt,
        AdminEventAppend historyAppend,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existingEvent = await AdminEventPersistence.TryGetByOperationIdAsync(
                db,
                historyAppend.OperationId,
                cancellationToken)
            .ConfigureAwait(false);
        if (existingEvent is not null)
        {
            return await ResolvePersonaChangedByOperationEventAsync(
                    existingEvent,
                    historyAppend,
                    update,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var row = await db.AgentInstances
            .SingleOrDefaultAsync(item => item.InstanceId == update.InstanceId.ToString("D"), cancellationToken)
            .ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Agent instance was not found.");
        if (row.Revision != update.ExpectedRevision)
        {
            throw new AgentCoreException("Conflict", "Agent instance revision is stale.", 409);
        }

        if (row.Compatibility)
        {
            throw new AgentCoreException("Validation", "Compatibility instances cannot change persona or lifecycle.", 400);
        }

        if (update.Persona is not AgentIdentity persona)
        {
            throw AgentCoreErrors.Validation("Persona is required.");
        }

        if (update.ExpectedPersonaRevision is not long expectedPersonaRevision)
        {
            throw new AgentCoreException(
                "Validation",
                "Expected persona revision is required for persona edits.",
                400);
        }

        if (expectedPersonaRevision != row.PersonaRevision)
        {
            throw new AgentCoreException("Conflict", "Agent instance persona revision is stale.", 409);
        }

        row.PersonaJson = JsonSerializer.Serialize(persona, Json);
        row.PersonaRevision++;
        row.UpdatedAtUtc = updatedAt.ToUnixTimeMilliseconds();
        row.Revision++;
        AdminEventPersistence.StageAppend(db, historyAppend, ids.NewId());
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            var raced = await AdminEventPersistence.TryGetByOperationIdAsync(
                    db,
                    historyAppend.OperationId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (raced is not null)
            {
                return await ResolvePersonaChangedByOperationEventAsync(
                        raced,
                        historyAppend,
                        update,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            throw new AgentCoreException("Conflict", "Agent instance revision is stale.", 409);
        }

        return Map(row);
    }

    private async ValueTask<AgentInstance> ResolvePersonaChangedByOperationEventAsync(
        AdminEvent existingEvent,
        AdminEventAppend historyAppend,
        AgentInstanceRevisionUpdate update,
        CancellationToken cancellationToken)
    {
        if (existingEvent.Operation != AdminEventOperationKind.PersonaChanged)
        {
            throw AgentCoreErrors.Conflict("Operation id is already used for a different admin event.");
        }

        EnsureAgentInstanceHistoryTargetMatches(existingEvent, historyAppend, update.InstanceId);
        AdminEventReplayPolicy.EnsurePersonaChangedReplayMatches(existingEvent, historyAppend, update);

        if (!Guid.TryParse(existingEvent.TargetId, out var targetId) || targetId != update.InstanceId)
        {
            throw AgentCoreErrors.Conflict("Managed instance history is missing an instance target id.");
        }

        return await FindAsync(update.InstanceId, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.Conflict("Managed instance history references a missing instance.");
    }

    private async ValueTask<AgentInstance> ResolveInstanceDefinitionVersionChangedByOperationEventAsync(
        AdminEvent existingEvent,
        AdminEventAppend historyAppend,
        AgentInstanceRevisionUpdate update,
        CancellationToken cancellationToken)
    {
        if (existingEvent.Operation != AdminEventOperationKind.InstanceDefinitionVersionChanged)
        {
            throw AgentCoreErrors.Conflict("Operation id is already used for a different admin event.");
        }

        EnsureAgentInstanceHistoryTargetMatches(existingEvent, historyAppend, update.InstanceId);
        AdminEventReplayPolicy.EnsureInstanceDefinitionVersionChangedReplayMatches(
            existingEvent,
            historyAppend,
            update);

        if (!Guid.TryParse(existingEvent.TargetId, out var targetId) || targetId != update.InstanceId)
        {
            throw AgentCoreErrors.Conflict("Managed instance history is missing an instance target id.");
        }

        return await FindAsync(update.InstanceId, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.Conflict("Managed instance history references a missing instance.");
    }

    private static void EnsureAgentInstanceHistoryTargetMatches(
        AdminEvent existingEvent,
        AdminEventAppend historyAppend,
        Guid instanceId)
    {
        var expectedTargetId = instanceId.ToString("D");
        if (!string.Equals(historyAppend.TargetId, expectedTargetId, StringComparison.Ordinal)
            || !string.Equals(existingEvent.TargetId, expectedTargetId, StringComparison.Ordinal))
        {
            throw AgentCoreErrors.Conflict("Managed instance history target does not match the retried command.");
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
        if (activeVersion <= row.ActiveVersion)
        {
            return;
        }

        row.ActiveVersion = activeVersion;
        row.UpdatedAtUtc = updatedAt.ToUnixTimeMilliseconds();
        row.Revision++;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new AgentCoreException("Conflict", "Agent instance revision is stale.", 409);
        }
    }

    public async ValueTask<AgentInstance> UpdateWithExpectedRevisionAsync(
        AgentInstanceRevisionUpdate update,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.AgentInstances
            .SingleOrDefaultAsync(item => item.InstanceId == update.InstanceId.ToString("D"), cancellationToken)
            .ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Agent instance was not found.");
        if (row.Revision != update.ExpectedRevision)
        {
            throw new AgentCoreException("Conflict", "Agent instance revision is stale.", 409);
        }

        if (row.Compatibility && (update.Persona is not null || update.Lifecycle is not null))
        {
            throw new AgentCoreException("Validation", "Compatibility instances cannot change persona or lifecycle.", 400);
        }

        var current = Map(row);
        var persona = update.Persona ?? current.Persona;
        var personaRevision = row.PersonaRevision;
        var personaChanging = update.Persona is not null && !update.Persona.Equals(current.Persona);
        if (personaChanging)
        {
            if (update.ExpectedPersonaRevision is null)
            {
                throw new AgentCoreException(
                    "Validation",
                    "Expected persona revision is required for persona edits.",
                    400);
            }

            if (update.ExpectedPersonaRevision != row.PersonaRevision)
            {
                throw new AgentCoreException("Conflict", "Agent instance persona revision is stale.", 409);
            }

            personaRevision++;
            row.PersonaJson = JsonSerializer.Serialize(persona, Json);
        }

        if (update.ActiveVersion is int activeVersion)
        {
            row.ActiveVersion = activeVersion;
        }

        if (update.Lifecycle is AgentInstanceLifecycle lifecycle)
        {
            row.Lifecycle = lifecycle.ToString();
        }

        row.UpdatedAtUtc = updatedAt.ToUnixTimeMilliseconds();
        row.Revision++;
        row.PersonaRevision = personaRevision;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new AgentCoreException("Conflict", "Agent instance revision is stale.", 409);
        }

        return Map(row);
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
            row.Compatibility,
            row.Revision,
            row.PersonaRevision);

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
            Compatibility = instance.Compatibility,
            Revision = instance.Revision,
            PersonaRevision = instance.PersonaRevision
        };
}
