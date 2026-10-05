using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Admin;

public sealed class InMemoryAdminLifecycleDeletion(
    InMemoryAgentInstanceStore instances,
    InMemoryMemoryStore sessions,
    InMemoryStructuredMemoryStore memories,
    InMemoryTriggerStore triggers,
    InMemoryWorkItemStore workItems,
    InMemoryConversationTurnExecutionStore executions,
    InMemoryAgentDefinitionAdminStore definitions,
    InMemoryAdminEventStore events,
    InMemoryExperienceStore? experience = null) : IAdminLifecycleDeletion
{
    internal Func<CancellationToken, ValueTask>? BeforeCommit { get; set; }

    public async ValueTask DeleteInstanceAsync(
        AdminInstanceDeleteCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (await events.TryGetByOperationIdAsync(command.OperationId, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw AgentCoreErrors.Conflict("Operation id is already used for a different admin event.");
        }

        var instance = await instances.FindAsync(command.InstanceId, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Agent instance was not found.");
        EnsureDeletableInstance(instance, command.ExpectedRevision);
        var counts = CountInstance(command.InstanceId);
        if (counts.HasReferences)
        {
            throw AgentCoreErrors.Conflict(AdminDeletionMessages.InstanceBlocked(counts));
        }

        if (BeforeCommit is not null)
        {
            await BeforeCommit(cancellationToken).ConfigureAwait(false);
        }

        var removed = instances.RemoveForDeletion(command.InstanceId, command.ExpectedRevision);
        try
        {
            events.AppendWithinLock(AdminEventFactory.InstanceDeleted(
                command.OperationId,
                command.OccurredAt,
                removed.DefinitionId,
                removed.InstanceId,
                removed.Revision,
                command.ActorKind));
        }
        catch
        {
            instances.Restore(removed);
            throw;
        }
        experience?.Purge(command.InstanceId);
        triggers.PurgeDeletedThoughts(command.InstanceId);
    }

    public async ValueTask DeleteDefinitionAsync(
        AdminDefinitionDeleteCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (await events.TryGetByOperationIdAsync(command.OperationId, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw AgentCoreErrors.Conflict("Operation id is already used for a different admin event.");
        }

        var counts = CountDefinition(command.DefinitionId);
        if (counts.HasReferences)
        {
            throw AgentCoreErrors.Conflict(AdminDeletionMessages.DefinitionBlocked(counts));
        }

        if (BeforeCommit is not null)
        {
            await BeforeCommit(cancellationToken).ConfigureAwait(false);
        }

        definitions.PurgeLogicalDefinition(command);
    }

    private AdminDeletionReferenceCounts CountInstance(Guid instanceId)
    {
        var triggersForInstance = triggers.CountForInstance(instanceId);
        var work = workItems.CountForInstance(instanceId);
        return new AdminDeletionReferenceCounts(
            sessions.CountLiveByInstance(instanceId),
            memories.CountActiveIdentityUser(instanceId),
            triggersForInstance.Registrations,
            triggersForInstance.Occurrences,
            work.Items,
            work.Approvals,
            executions.CountForInstance(instanceId),
            Instances: 0);
    }

    private AdminDeletionReferenceCounts CountDefinition(string definitionId)
    {
        var work = workItems.CountForDefinition(definitionId);
        return new AdminDeletionReferenceCounts(
            sessions.CountLiveByDefinition(definitionId),
            LearnedMemoryItems: 0,
            TriggerRegistrations: 0,
            TriggerOccurrences: 0,
            work.Items,
            work.Approvals,
            executions.CountForDefinition(definitionId),
            instances.CountByDefinition(definitionId));
    }

    private static void EnsureDeletableInstance(AgentInstance instance, long expectedRevision)
    {
        if (instance.Compatibility)
        {
            throw AgentCoreErrors.Validation(
                "Compatibility instances cannot be deleted through the managed Admin path.");
        }

        if (instance.Lifecycle != AgentInstanceLifecycle.Archived)
        {
            throw AgentCoreErrors.Validation("Archive this instance before deleting it.");
        }

        if (instance.Revision != expectedRevision)
        {
            throw AgentCoreErrors.Conflict("Agent instance revision is stale.");
        }
    }
}

public sealed class SqliteAdminLifecycleDeletion(
    IDbContextFactory<AgentCoreDbContext> contexts,
    IIdGenerator ids) : IAdminLifecycleDeletion
{
    public async ValueTask DeleteInstanceAsync(
        AdminInstanceDeleteCommand command,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var key = command.InstanceId.ToString("D");
        var row = await db.AgentInstances
            .SingleOrDefaultAsync(item => item.InstanceId == key, cancellationToken)
            .ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Agent instance was not found.");
        if (row.Compatibility)
        {
            throw AgentCoreErrors.Validation(
                "Compatibility instances cannot be deleted through the managed Admin path.");
        }

        if (!string.Equals(row.Lifecycle, nameof(AgentInstanceLifecycle.Archived), StringComparison.Ordinal))
        {
            throw AgentCoreErrors.Validation("Archive this instance before deleting it.");
        }

        if (row.Revision != command.ExpectedRevision)
        {
            throw AgentCoreErrors.Conflict("Agent instance revision is stale.");
        }

        var counts = await CountInstanceAsync(db, key, cancellationToken).ConfigureAwait(false);
        if (counts.HasReferences)
        {
            throw AgentCoreErrors.Conflict(AdminDeletionMessages.InstanceBlocked(counts));
        }

        await db.Experiences.Where(r => r.AgentInstanceId == key).ExecuteDeleteAsync(cancellationToken);
        await db.ExperienceSettings.Where(r => r.AgentInstanceId == key).ExecuteDeleteAsync(cancellationToken);
        var deletedThoughts = DeletedThoughts(db, key).Select(r => r.RegistrationId);
        await db.TriggerOccurrences.Where(o => o.AgentInstanceId == key && deletedThoughts.Contains(o.RegistrationId!)
            && o.SourceKind == (int)TriggerSourceKind.ThoughtActivation && o.Disposition == (int)OccurrenceRoutingDisposition.Rejected
            && o.DurableWorkItemId == null).ExecuteDeleteAsync(cancellationToken);
        await DeletedThoughts(db, key).ExecuteDeleteAsync(cancellationToken);
        db.AgentInstances.Remove(row);
        AdminEventPersistence.StageAppend(
            db,
            AdminEventFactory.InstanceDeleted(
                command.OperationId,
                command.OccurredAt,
                row.DefinitionId,
                command.InstanceId,
                row.Revision,
                command.ActorKind),
            ids.NewId());
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DeleteDefinitionAsync(
        AdminDefinitionDeleteCommand command,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var drafts = await db.AgentDefinitionDrafts
            .Where(item => item.DefinitionId == command.DefinitionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var publications = await db.AgentDefinitionPublications
            .Where(item => item.DefinitionId == command.DefinitionId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (drafts.Count == 0 && publications.Count == 0)
        {
            throw AgentCoreErrors.NotFound("Definition was not found.");
        }

        if (!WitnessMatches(command.Witness, drafts, publications))
        {
            throw AgentCoreErrors.Conflict(AdminDeletionMessages.DefinitionChanged);
        }

        var counts = await CountDefinitionAsync(db, command.DefinitionId, cancellationToken).ConfigureAwait(false);
        if (counts.HasReferences)
        {
            throw AgentCoreErrors.Conflict(AdminDeletionMessages.DefinitionBlocked(counts));
        }

        var draftIds = drafts.Select(item => item.DraftId).ToArray();
        await db.AgentDefinitionDraftResources
            .Where(item => draftIds.Contains(item.DraftId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        await db.AgentDefinitionDraftEvaluationScenarios
            .Where(item => draftIds.Contains(item.DraftId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        await db.AgentDefinitionDraftEvaluationResults
            .Where(item => draftIds.Contains(item.DraftId))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        await db.AgentDefinitionPublicationResources
            .Where(item => item.DefinitionId == command.DefinitionId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        db.AgentDefinitionDrafts.RemoveRange(drafts);
        db.AgentDefinitionPublications.RemoveRange(publications);
        AdminEventPersistence.StageAppend(
            db,
            AdminEventFactory.DefinitionDeleted(
                command.OperationId,
                command.OccurredAt,
                command.DefinitionId,
                drafts.Count,
                publications.Count,
                command.ActorKind),
            ids.NewId());
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<AdminDeletionReferenceCounts> CountInstanceAsync(
        AgentCoreDbContext db,
        string instanceId,
        CancellationToken cancellationToken)
    {
        var workItemIds = db.WorkItems.Where(item => item.AgentInstanceId == instanceId).Select(item => item.WorkItemId);
        var deletedThoughts = DeletedThoughts(db, instanceId).Select(r => r.RegistrationId);
        return new AdminDeletionReferenceCounts(
            await db.Sessions.CountAsync(
                item => item.AgentInstanceId == instanceId && item.DurablyDeletedAtUtc == null,
                cancellationToken).ConfigureAwait(false),
            await db.StructuredMemories.CountAsync(
                item => item.OwnerInstanceId == instanceId && item.Scope == 1 && item.Status == 0,
                cancellationToken).ConfigureAwait(false),
            await db.TriggerRegistrations.CountAsync(
                item => item.AgentInstanceId == instanceId && !deletedThoughts.Contains(item.RegistrationId),
                cancellationToken).ConfigureAwait(false),
            await db.TriggerOccurrences.CountAsync(
                item => item.AgentInstanceId == instanceId && !(deletedThoughts.Contains(item.RegistrationId!)
                    && item.SourceKind == (int)TriggerSourceKind.ThoughtActivation
                    && item.Disposition == (int)OccurrenceRoutingDisposition.Rejected && item.DurableWorkItemId == null),
                cancellationToken).ConfigureAwait(false),
            await db.WorkItems.CountAsync(item => item.AgentInstanceId == instanceId, cancellationToken)
                .ConfigureAwait(false),
            await db.WorkApprovals.CountAsync(item => workItemIds.Contains(item.WorkItemId), cancellationToken)
                .ConfigureAwait(false),
            await db.ConversationTurnExecutions.CountAsync(
                item => item.AgentInstanceId == instanceId,
                cancellationToken).ConfigureAwait(false),
            Instances: 0);
    }

    private static IQueryable<TriggerRegistrationRecord> DeletedThoughts(AgentCoreDbContext db, string instanceId) =>
        db.TriggerRegistrations.Where(r => r.AgentInstanceId == instanceId && r.AuthorizationOrigin == (int)TriggerAuthorizationOrigin.AdminThought
            && r.Status == (int)TriggerRegistrationStatus.Cancelled);

    private static async Task<AdminDeletionReferenceCounts> CountDefinitionAsync(
        AgentCoreDbContext db,
        string definitionId,
        CancellationToken cancellationToken)
    {
        var workItemIds = db.WorkItems.Where(item => item.DefinitionId == definitionId).Select(item => item.WorkItemId);
        return new AdminDeletionReferenceCounts(
            await db.Sessions.CountAsync(
                item => item.AgentId == definitionId && item.DurablyDeletedAtUtc == null,
                cancellationToken).ConfigureAwait(false),
            LearnedMemoryItems: 0,
            TriggerRegistrations: 0,
            TriggerOccurrences: 0,
            await db.WorkItems.CountAsync(item => item.DefinitionId == definitionId, cancellationToken)
                .ConfigureAwait(false),
            await db.WorkApprovals.CountAsync(item => workItemIds.Contains(item.WorkItemId), cancellationToken)
                .ConfigureAwait(false),
            await db.ConversationTurnExecutions.CountAsync(
                item => item.DefinitionId == definitionId,
                cancellationToken).ConfigureAwait(false),
            await db.AgentInstances.CountAsync(item => item.DefinitionId == definitionId, cancellationToken)
                .ConfigureAwait(false));
    }

    private static bool WitnessMatches(
        AdminDefinitionDeleteWitness witness,
        IReadOnlyList<AgentDefinitionDraftRecord> drafts,
        IReadOnlyList<AgentDefinitionPublicationRecord> publications)
    {
        var expectedDrafts = witness.Drafts.OrderBy(item => item.DraftId).ToArray();
        var actualDrafts = drafts
            .Select(item => new AdminDraftRevisionWitness(Guid.Parse(item.DraftId), item.Revision))
            .OrderBy(item => item.DraftId)
            .ToArray();
        if (expectedDrafts.Length != actualDrafts.Length)
        {
            return false;
        }

        for (var index = 0; index < actualDrafts.Length; index++)
        {
            if (expectedDrafts[index].DraftId != actualDrafts[index].DraftId
                || expectedDrafts[index].Revision != actualDrafts[index].Revision)
            {
                return false;
            }
        }

        var expectedPublications = witness.Publications.OrderBy(item => item.Version).ToArray();
        var actualPublications = publications.OrderBy(item => item.Version).ToArray();
        if (expectedPublications.Length != actualPublications.Length)
        {
            return false;
        }

        for (var index = 0; index < actualPublications.Length; index++)
        {
            if (expectedPublications[index].Version != actualPublications[index].Version
                || expectedPublications[index].MetadataRevision != actualPublications[index].MetadataRevision)
            {
                return false;
            }
        }

        return true;
    }
}
