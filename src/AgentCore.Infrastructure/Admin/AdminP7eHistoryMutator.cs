using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Memory;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Admin;

internal sealed class InMemoryAdminP7eHistoryMutator(
    AdminMemoryService memory,
    InMemoryAdminEventStore events,
    InMemoryStructuredMemoryStore structured,
    AdminAutomationService automation,
    InMemoryDurableState triggerState) : IAdminP7eHistoryMutator
{
    public ValueTask DeleteLearnedMemoryWithHistoryAsync(
        Guid instanceId,
        AdminLearnedMemoryScope scope,
        Guid memoryId,
        Guid? sessionId,
        AdminEventAppend append,
        Action<AdminEvent, AdminEventAppend> ensureReplay,
        CancellationToken cancellationToken = default)
    {
        var operationGate = AdminOperationLockRegistry.For(append.OperationId);
        lock (operationGate)
        lock (structured.SyncRoot)
        {
            var existing = events.TryGetByOperationIdAsync(append.OperationId, cancellationToken)
                .AsTask().GetAwaiter().GetResult();
            if (existing is not null)
            {
                if (existing.Operation != append.Operation)
                {
                    throw AgentCoreErrors.Conflict("Operation id is already used for a different admin event.");
                }

                ensureReplay(existing, append);
                return ValueTask.CompletedTask;
            }

            var prior = structured.TryGetCopy(memoryId);
            try
            {
                memory.EnsureDeleteAllowedAsync(instanceId, scope, sessionId, cancellationToken)
                    .AsTask().GetAwaiter().GetResult();
                memory.DeleteAsync(instanceId, scope, memoryId, sessionId, cancellationToken)
                    .AsTask().GetAwaiter().GetResult();
                events.AppendWithinLock(append);
            }
            catch
            {
                structured.RestoreItem(memoryId, prior);
                throw;
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<int> ResetLearnedMemoryScopeWithHistoryAsync(
        Guid instanceId,
        AdminLearnedMemoryScope scope,
        Guid? sessionId,
        AdminEventAppend append,
        Action<AdminEvent, AdminEventAppend, int> ensureReplay,
        CancellationToken cancellationToken = default)
    {
        var operationGate = AdminOperationLockRegistry.For(append.OperationId);
        lock (operationGate)
        lock (structured.SyncRoot)
        {
            var existing = events.TryGetByOperationIdAsync(append.OperationId, cancellationToken)
                .AsTask().GetAwaiter().GetResult();
            if (existing is not null)
            {
                if (existing.Operation != AdminEventOperationKind.MemoryScopeReset)
                {
                    throw AgentCoreErrors.Conflict("Operation id is already used for a different admin event.");
                }

                var itemsRemoved = ReadItemsRemoved(existing.SummaryJson);
                var replayAppend = AdminEventFactory.MemoryScopeReset(
                    append.OperationId,
                    append.OccurredAt,
                    instanceId,
                    scope,
                    itemsRemoved,
                    sessionId);
                ensureReplay(existing, replayAppend, itemsRemoved);
                return ValueTask.FromResult(itemsRemoved);
            }

            var snapshot = SnapshotScope(instanceId, scope, sessionId);
            try
            {
                memory.EnsureResetAllowedAsync(instanceId, scope, sessionId, cancellationToken)
                    .AsTask().GetAwaiter().GetResult();
                var result = memory.ResetScopeAsync(instanceId, scope, sessionId, cancellationToken)
                    .AsTask().GetAwaiter().GetResult();
                var recordedAppend = AdminEventFactory.MemoryScopeReset(
                    append.OperationId,
                    append.OccurredAt,
                    instanceId,
                    scope,
                    result.ItemsRemoved,
                    sessionId);
                events.AppendWithinLock(recordedAppend);
                return ValueTask.FromResult(result.ItemsRemoved);
            }
            catch
            {
                structured.RestoreItems(snapshot);
                throw;
            }
        }
    }

    public ValueTask CancelTriggerRegistrationWithHistoryAsync(
        Guid instanceId,
        Guid registrationId,
        long expectedRevision,
        AdminEventAppend append,
        Action<AdminEvent, AdminEventAppend> ensureReplay,
        CancellationToken cancellationToken = default)
    {
        var operationGate = AdminOperationLockRegistry.For(append.OperationId);
        lock (operationGate)
        {
            var existing = events.TryGetByOperationIdAsync(append.OperationId, cancellationToken)
                .AsTask().GetAwaiter().GetResult();
            if (existing is not null)
            {
                if (existing.Operation != AdminEventOperationKind.TriggerRegistrationRevoked)
                {
                    throw AgentCoreErrors.Conflict("Operation id is already used for a different admin event.");
                }

                ensureReplay(existing, append);
                return ValueTask.CompletedTask;
            }

            TriggerRegistration? prior = null;
            lock (triggerState.Gate)
            {
                if (triggerState.Registrations.TryGetValue(registrationId, out var current))
                {
                    prior = current;
                }
            }

            try
            {
                _ = automation
                    .CancelRegistrationAsync(instanceId, registrationId, expectedRevision, cancellationToken)
                    .AsTask().GetAwaiter().GetResult();
                events.AppendWithinLock(append);
            }
            catch
            {
                if (prior is not null)
                {
                    lock (triggerState.Gate)
                    {
                        triggerState.Registrations[registrationId] = prior;
                    }
                }

                throw;
            }
        }

        return ValueTask.CompletedTask;
    }

    private Dictionary<Guid, StructuredMemoryItem> SnapshotScope(
        Guid instanceId,
        AdminLearnedMemoryScope scope,
        Guid? sessionId) =>
        scope switch
        {
            AdminLearnedMemoryScope.Session when sessionId is Guid resolved =>
                structured.SnapshotActive(item => item.Scope == MemoryScope.Session && item.SessionId == resolved),
            AdminLearnedMemoryScope.IdentityUser =>
                structured.SnapshotActive(item =>
                    item.Scope == MemoryScope.IdentityUser && item.OwnerInstanceId == instanceId),
            AdminLearnedMemoryScope.User =>
                structured.SnapshotActive(item => item.Scope == MemoryScope.User),
            _ => []
        };

    private static int ReadItemsRemoved(string summaryJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(summaryJson);
        if (!document.RootElement.TryGetProperty("itemsRemoved", out var value)
            || value.ValueKind != System.Text.Json.JsonValueKind.Number)
        {
            throw AgentCoreErrors.Conflict("Memory scope reset history is missing itemsRemoved metadata.");
        }

        return value.GetInt32();
    }
}

internal sealed class SqliteAdminP7eHistoryMutator(
    AdminMemoryService memory,
    AdminAutomationService automation,
    ILocalUserProfileService localProfiles,
    IDbContextFactory<AgentCoreDbContext> contexts,
    IIdGenerator ids,
    TimeProvider time) : IAdminP7eHistoryMutator
{
    public ValueTask DeleteLearnedMemoryWithHistoryAsync(
        Guid instanceId,
        AdminLearnedMemoryScope scope,
        Guid memoryId,
        Guid? sessionId,
        AdminEventAppend append,
        Action<AdminEvent, AdminEventAppend> ensureReplay,
        CancellationToken cancellationToken = default) =>
        RunAppendHistoryAsync(
            append,
            ensureReplay,
            async (db, ct) =>
            {
                await memory.EnsureDeleteAllowedAsync(instanceId, scope, sessionId, ct).ConfigureAwait(false);
                var profile = await localProfiles.GetLocalProfileAsync(ct).ConfigureAwait(false);
                await SqliteAdminP7eHistoryPersistence.TombstoneActiveMemoryAsync(
                        db,
                        scope,
                        memoryId,
                        instanceId,
                        profile.ProfileId,
                        sessionId,
                        ct)
                    .ConfigureAwait(false);
            },
            cancellationToken);

    public async ValueTask<int> ResetLearnedMemoryScopeWithHistoryAsync(
        Guid instanceId,
        AdminLearnedMemoryScope scope,
        Guid? sessionId,
        AdminEventAppend append,
        Action<AdminEvent, AdminEventAppend, int> ensureReplay,
        CancellationToken cancellationToken = default)
    {
        await memory.EnsureResetAllowedAsync(instanceId, scope, sessionId, cancellationToken).ConfigureAwait(false);
        var profile = await localProfiles.GetLocalProfileAsync(cancellationToken).ConfigureAwait(false);
        return await RunResetHistoryAsync(
            instanceId,
            scope,
            sessionId,
            append,
            replay => ensureReplay(replay.Existing, replay.Incoming, replay.ItemsRemoved),
            async (db, ct) => scope switch
            {
                AdminLearnedMemoryScope.Session when sessionId is Guid resolved =>
                    await SqliteAdminP7eHistoryPersistence.ResetActiveSessionScopeAsync(
                        db,
                        resolved,
                        time.GetUtcNow(),
                        ct).ConfigureAwait(false),
                AdminLearnedMemoryScope.IdentityUser =>
                    await SqliteAdminP7eHistoryPersistence.ResetActiveIdentityUserScopeAsync(
                        db,
                        instanceId,
                        profile.ProfileId,
                        time.GetUtcNow(),
                        ct).ConfigureAwait(false),
                AdminLearnedMemoryScope.User =>
                    await SqliteAdminP7eHistoryPersistence.ResetActiveUserScopeAsync(
                        db,
                        profile.ProfileId,
                        time.GetUtcNow(),
                        ct).ConfigureAwait(false),
                _ => throw AgentCoreErrors.Validation("scope is invalid.")
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask CancelTriggerRegistrationWithHistoryAsync(
        Guid instanceId,
        Guid registrationId,
        long expectedRevision,
        AdminEventAppend append,
        Action<AdminEvent, AdminEventAppend> ensureReplay,
        CancellationToken cancellationToken = default)
    {
        var profile = await localProfiles.GetLocalProfileAsync(cancellationToken).ConfigureAwait(false);
        var owner = new TriggerOwner(instanceId, profile.ProfileId);
        _ = await automation.GetRegistrationAsync(instanceId, registrationId, cancellationToken).ConfigureAwait(false);
        await RunAppendHistoryAsync(
            append,
            ensureReplay,
            async (db, ct) =>
            {
                _ = await SqliteAdminP7eHistoryPersistence.CancelRegistrationAsync(
                        db,
                        owner,
                        registrationId,
                        expectedRevision,
                        time.GetUtcNow(),
                        ct)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask RunAppendHistoryAsync(
        AdminEventAppend append,
        Action<AdminEvent, AdminEventAppend> ensureReplay,
        Func<AgentCoreDbContext, CancellationToken, ValueTask> mutate,
        CancellationToken cancellationToken)
    {
        var operationGate = AdminOperationLockRegistry.For(append.OperationId);
        lock (operationGate)
        {
            RunAppendHistoryCore(append, ensureReplay, mutate, cancellationToken).AsTask().GetAwaiter().GetResult();
        }
    }

    private async ValueTask RunAppendHistoryCore(
        AdminEventAppend append,
        Action<AdminEvent, AdminEventAppend> ensureReplay,
        Func<AgentCoreDbContext, CancellationToken, ValueTask> mutate,
        CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await AdminEventPersistence.TryGetByOperationIdAsync(db, append.OperationId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.Operation != append.Operation)
            {
                throw AgentCoreErrors.Conflict("Operation id is already used for a different admin event.");
            }

            ensureReplay(existing, append);
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await mutate(db, cancellationToken).ConfigureAwait(false);
        AdminEventPersistence.StageAppend(db, append, ids.NewId());
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private readonly record struct ResetReplay(AdminEvent Existing, AdminEventAppend Incoming, int ItemsRemoved);

    private async ValueTask<int> RunResetHistoryAsync(
        Guid instanceId,
        AdminLearnedMemoryScope scope,
        Guid? sessionId,
        AdminEventAppend append,
        Action<ResetReplay> ensureReplay,
        Func<AgentCoreDbContext, CancellationToken, ValueTask<int>> mutate,
        CancellationToken cancellationToken)
    {
        var operationGate = AdminOperationLockRegistry.For(append.OperationId);
        lock (operationGate)
        {
            return RunResetHistoryCore(instanceId, scope, sessionId, append, ensureReplay, mutate, cancellationToken)
                .AsTask().GetAwaiter().GetResult();
        }
    }

    private async ValueTask<int> RunResetHistoryCore(
        Guid instanceId,
        AdminLearnedMemoryScope scope,
        Guid? sessionId,
        AdminEventAppend append,
        Action<ResetReplay> ensureReplay,
        Func<AgentCoreDbContext, CancellationToken, ValueTask<int>> mutate,
        CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await AdminEventPersistence.TryGetByOperationIdAsync(db, append.OperationId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.Operation != AdminEventOperationKind.MemoryScopeReset)
            {
                throw AgentCoreErrors.Conflict("Operation id is already used for a different admin event.");
            }

            var itemsRemoved = ReadItemsRemoved(existing.SummaryJson);
            var replayAppend = AdminEventFactory.MemoryScopeReset(
                append.OperationId,
                append.OccurredAt,
                instanceId,
                scope,
                itemsRemoved,
                sessionId);
            ensureReplay(new ResetReplay(existing, replayAppend, itemsRemoved));
            return itemsRemoved;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var removed = await mutate(db, cancellationToken).ConfigureAwait(false);
        var recordedAppend = AdminEventFactory.MemoryScopeReset(
            append.OperationId,
            append.OccurredAt,
            instanceId,
            scope,
            removed,
            sessionId);
        AdminEventPersistence.StageAppend(db, recordedAppend, ids.NewId());
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return removed;
    }

    private static int ReadItemsRemoved(string summaryJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(summaryJson);
        if (!document.RootElement.TryGetProperty("itemsRemoved", out var value)
            || value.ValueKind != System.Text.Json.JsonValueKind.Number)
        {
            throw AgentCoreErrors.Conflict("Memory scope reset history is missing itemsRemoved metadata.");
        }

        return value.GetInt32();
    }
}
