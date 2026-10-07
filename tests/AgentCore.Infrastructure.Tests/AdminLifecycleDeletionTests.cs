using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Memory;
using AgentCore.Domain.Triggers;
using AgentCore.Domain.Work;
using AgentCore.Infrastructure.Admin;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed class InMemoryAdminLifecycleDeletionTests : AdminLifecycleDeletionTests
{
    protected override Task ForEachProfileAsync(Func<DeletionFixture, Task> exercise)
    {
        var clock = TimeProvider.System;
        var ids = new SystemIdGenerator(clock);
        var events = new InMemoryAdminEventStore(ids);
        var state = new InMemoryDurableState();
        var instances = new InMemoryAgentInstanceStore { EventStore = events };
        var sessions = new InMemoryMemoryStore();
        var memories = new InMemoryStructuredMemoryStore();
        var triggers = new InMemoryTriggerStore(state);
        var work = new InMemoryWorkItemStore(state);
        var executions = new InMemoryConversationTurnExecutionStore(state);
        var definitions = new InMemoryAgentDefinitionAdminStore(ids) { EventStore = events };
        var resources = new InMemoryAgentDefinitionResourceAdminStore(
            definitions,
            new InMemoryDefinitionResourceContentStore(),
            ids);
        definitions.ResourceStore = resources;
        _ = new InMemoryDefinitionDraftEvaluationStore(definitions);
        var deletion = new InMemoryAdminLifecycleDeletion(
            instances,
            sessions,
            memories,
            triggers,
            work,
            executions,
            definitions,
            events);
        return exercise(new DeletionFixture(
            definitions,
            instances,
            sessions,
            memories,
            triggers,
            work,
            executions,
            events,
            deletion,
            clock,
            ids));
    }
}

public sealed class SqliteAdminLifecycleDeletionTests : AdminLifecycleDeletionTests
{
    protected override async Task ForEachProfileAsync(Func<DeletionFixture, Task> exercise)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-lifecycle-delete-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new SqliteContextFactory(options);
        try
        {
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            var clock = TimeProvider.System;
            var ids = new SystemIdGenerator(clock);
            var events = new SqliteAdminEventStore(factory, ids);
            var instances = new SqliteAgentInstanceStore(factory, ids);
            var sessions = new SqliteMemoryStore(factory, clock);
            var memories = new SqliteStructuredMemoryStore(factory);
            var triggers = new SqliteTriggerStore(factory);
            var work = new SqliteWorkItemStore(factory);
            var executions = new SqliteConversationTurnExecutionStore(factory);
            var definitions = new SqliteAgentDefinitionAdminStore(
                factory,
                ids,
                new InMemoryDefinitionResourceContentStore());
            var deletion = new SqliteAdminLifecycleDeletion(factory, ids);
            await exercise(new DeletionFixture(
                definitions,
                instances,
                sessions,
                memories,
                triggers,
                work,
                executions,
                events,
                deletion,
                clock,
                ids));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private sealed class SqliteContextFactory(DbContextOptions<AgentCoreDbContext> options)
        : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public ValueTask<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CreateDbContext());
    }
}

public abstract class AdminLifecycleDeletionTests
{
    protected sealed record DeletionFixture(
        IAgentDefinitionAdminStore Definitions,
        IAgentInstanceStore Instances,
        IMemoryStore Sessions,
        IStructuredMemoryStore Memories,
        ITriggerStore Triggers,
        IWorkItemStore WorkItems,
        IConversationTurnExecutionStore Executions,
        IAdminEventStore Events,
        IAdminLifecycleDeletion Deletion,
        TimeProvider Clock,
        IIdGenerator Ids);

    protected abstract Task ForEachProfileAsync(Func<DeletionFixture, Task> exercise);

    [Fact]
    public async Task Safe_definition_delete_removes_authoring_state_keeps_history_and_allows_id_reuse()
    {
        await ForEachProfileAsync(async fixture =>
        {
            var now = DateTimeOffset.Parse("2026-09-29T02:00:00Z");
            var draft = await fixture.Definitions.CreateDraftAsync(
                new AgentDefinitionDraftCreate(
                    "field-guide",
                    AdminPublicationHistoryTests.SampleCandidateStatic("field-guide"),
                    DefinitionDraftSourceKind.New,
                    null,
                    now,
                    fixture.Ids.NewId()),
                CancellationToken.None);
            var published = await fixture.Definitions.PublishDraftAsync(
                new AgentDefinitionDraftPublish(
                    draft.DraftId,
                    draft.Revision,
                    [],
                    now.AddMinutes(1),
                    fixture.Ids.NewId(),
                    ChangedSectionIds: ["instructions"]),
                CancellationToken.None);
            var reloaded = await fixture.Definitions.GetDraftAsync(draft.DraftId, CancellationToken.None);
            Assert.NotNull(reloaded);

            await fixture.Deletion.DeleteDefinitionAsync(
                new AdminDefinitionDeleteCommand(
                    "field-guide",
                    new AdminDefinitionDeleteWitness(
                        [new AdminDraftRevisionWitness(reloaded!.DraftId, reloaded.Revision)],
                        [new AdminPublicationRevisionWitness(published.Version, published.MetadataRevision)]),
                    fixture.Ids.NewId(),
                    now.AddMinutes(2)),
                CancellationToken.None);

            Assert.Empty(await fixture.Definitions.ListDraftsAsync(CancellationToken.None));
            Assert.Empty(await fixture.Definitions.ListPublicationsAsync("field-guide", CancellationToken.None));
            Assert.Null(await fixture.Definitions.GetPublicationAsync("field-guide", published.Version, CancellationToken.None));
            var history = await fixture.Events.ListAsync(new AdminEventListQuery(), CancellationToken.None);
            Assert.Contains(history, item => item.Operation == AdminEventOperationKind.DraftCreated);
            Assert.Contains(history, item => item.Operation == AdminEventOperationKind.PublicationCreated);
            Assert.Contains(history, item => item.Operation == AdminEventOperationKind.DefinitionDeleted);

            var reused = await fixture.Definitions.CreateDraftAsync(
                new AgentDefinitionDraftCreate(
                    "field-guide",
                    AdminPublicationHistoryTests.SampleCandidateStatic("field-guide"),
                    DefinitionDraftSourceKind.New,
                    null,
                    now.AddMinutes(3),
                    fixture.Ids.NewId()),
                CancellationToken.None);
            Assert.Equal("field-guide", reused.DefinitionId);
        });
    }

    [Fact]
    public async Task Stale_definition_witness_deletes_nothing()
    {
        await ForEachProfileAsync(async fixture =>
        {
            var now = DateTimeOffset.Parse("2026-09-29T02:10:00Z");
            var draft = await fixture.Definitions.CreateDraftAsync(
                new AgentDefinitionDraftCreate(
                    "field-guide",
                    AdminPublicationHistoryTests.SampleCandidateStatic("field-guide"),
                    DefinitionDraftSourceKind.New,
                    null,
                    now,
                    fixture.Ids.NewId()),
                CancellationToken.None);
            var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
                fixture.Deletion.DeleteDefinitionAsync(
                    new AdminDefinitionDeleteCommand(
                        "field-guide",
                        new AdminDefinitionDeleteWitness(
                            [new AdminDraftRevisionWitness(draft.DraftId, draft.Revision + 9)],
                            []),
                        fixture.Ids.NewId(),
                        now),
                    CancellationToken.None).AsTask());

            Assert.Equal("Conflict", error.Code);
            Assert.NotNull(await fixture.Definitions.GetDraftAsync(draft.DraftId, CancellationToken.None));
            var history = await fixture.Events.ListAsync(new AdminEventListQuery(), CancellationToken.None);
            Assert.DoesNotContain(history, item => item.Operation == AdminEventOperationKind.DefinitionDeleted);
        });
    }

    [Fact]
    public async Task Definition_delete_is_blocked_by_an_instance_and_leaves_drafts()
    {
        await ForEachProfileAsync(async fixture =>
        {
            var now = DateTimeOffset.Parse("2026-09-29T02:20:00Z");
            var draft = await fixture.Definitions.CreateDraftAsync(
                new AgentDefinitionDraftCreate(
                    "field-guide",
                    AdminPublicationHistoryTests.SampleCandidateStatic("field-guide"),
                    DefinitionDraftSourceKind.New,
                    null,
                    now,
                    fixture.Ids.NewId()),
                CancellationToken.None);
            await fixture.Instances.InsertAsync(new AgentInstance(
                Guid.Parse("019944af-00d1-7000-8000-000000000091"),
                "field-guide",
                1,
                new AgentIdentity("Guide", "role", "desc", "tone"),
                AgentInstanceLifecycle.Archived,
                now,
                now));

            var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
                fixture.Deletion.DeleteDefinitionAsync(
                    new AdminDefinitionDeleteCommand(
                        "field-guide",
                        new AdminDefinitionDeleteWitness(
                            [new AdminDraftRevisionWitness(draft.DraftId, draft.Revision)],
                            []),
                        fixture.Ids.NewId(),
                        now),
                    CancellationToken.None).AsTask());

            Assert.Equal("Conflict", error.Code);
            Assert.Contains("1 agent instance", error.Message, StringComparison.Ordinal);
            Assert.NotNull(await fixture.Definitions.GetDraftAsync(draft.DraftId, CancellationToken.None));
        });
    }

    [Fact]
    public async Task Archived_instance_without_dependencies_deletes_and_keeps_history()
    {
        await ForEachProfileAsync(async fixture =>
        {
            var now = DateTimeOffset.Parse("2026-09-29T02:30:00Z");
            var instanceId = Guid.Parse("019944af-00d1-7000-8000-000000000092");
            await fixture.Instances.InsertAsync(new AgentInstance(
                instanceId,
                "field-guide",
                1,
                new AgentIdentity("Guide", "role", "desc", "tone"),
                AgentInstanceLifecycle.Active,
                now,
                now,
                Revision: 2), initialSkills: [new SkillSpec("review", "Review", "Review", "Review", SkillProjection.OnDemand, true, [], [])]);

            await fixture.Instances.MutateSkillsAsync(new(instanceId, 2, InstanceSkill: new(fixture.Ids.NewId(), instanceId,
                "Local", "Local guidance", "Local procedure", SkillProjection.OnDemand, true, [], 1, now, now, SkillAuthor.Agent)));
            await fixture.Instances.UpdateWithExpectedRevisionAsync(new(instanceId, 3, Lifecycle: AgentInstanceLifecycle.Archived), now);
            var ownedSkills = await fixture.Instances.ReadSkillsAsync(instanceId);
            Assert.Single(ownedSkills.DefinitionStates); Assert.Single(ownedSkills.InstanceSkills);

            await fixture.Deletion.DeleteInstanceAsync(
                new AdminInstanceDeleteCommand(instanceId, 4, fixture.Ids.NewId(), now),
                CancellationToken.None);

            Assert.Null(await fixture.Instances.FindAsync(instanceId, CancellationToken.None));
            var deletedSkills = await fixture.Instances.ReadSkillsAsync(instanceId);
            Assert.Empty(deletedSkills.DefinitionStates); Assert.Empty(deletedSkills.InstanceSkills);
            var history = await fixture.Events.ListAsync(new AdminEventListQuery(), CancellationToken.None);
            Assert.Contains(history, item => item.Operation == AdminEventOperationKind.InstanceDeleted);
        });
    }

    [Fact]
    public async Task Active_and_stale_instances_are_not_deleted()
    {
        await ForEachProfileAsync(async fixture =>
        {
            var now = DateTimeOffset.Parse("2026-09-29T02:40:00Z");
            var activeId = Guid.Parse("019944af-00d1-7000-8000-000000000093");
            var staleId = Guid.Parse("019944af-00d1-7000-8000-000000000095");
            await fixture.Instances.InsertAsync(Instance(activeId, AgentInstanceLifecycle.Active, now));
            await fixture.Instances.InsertAsync(Instance(staleId, AgentInstanceLifecycle.Archived, now));

            var active = await Assert.ThrowsAsync<AgentCoreException>(() =>
                fixture.Deletion.DeleteInstanceAsync(
                    new AdminInstanceDeleteCommand(activeId, 1, fixture.Ids.NewId(), now),
                    CancellationToken.None).AsTask());
            var stale = await Assert.ThrowsAsync<AgentCoreException>(() =>
                fixture.Deletion.DeleteInstanceAsync(
                    new AdminInstanceDeleteCommand(staleId, 4, fixture.Ids.NewId(), now),
                    CancellationToken.None).AsTask());

            Assert.Equal("ValidationError", active.Code);
            Assert.Equal("Conflict", stale.Code);
            Assert.NotNull(await fixture.Instances.FindAsync(activeId, CancellationToken.None));
            Assert.NotNull(await fixture.Instances.FindAsync(staleId, CancellationToken.None));
        });
    }

    [Fact]
    public async Task Referenced_instance_is_not_deleted_and_names_the_blockers()
    {
        await ForEachProfileAsync(async fixture =>
        {
            var now = DateTimeOffset.Parse("2026-09-29T02:50:00Z");
            var instanceId = Guid.Parse("019944af-00d1-7000-8000-000000000096");
            var profileId = Guid.Parse("019944af-00d1-7000-8000-000000000097");
            await fixture.Instances.InsertAsync(Instance(instanceId, AgentInstanceLifecycle.Archived, now));
            await fixture.Sessions.SaveAsync(Snapshot(instanceId), 0, CancellationToken.None);
            await fixture.Memories.InsertAsync(new StructuredMemoryItem(
                Guid.Parse("019944af-00d1-7000-8000-000000000098"),
                Guid.Parse("019944af-00d1-7000-8000-000000000099"),
                MemoryKind.Fact,
                MemoryItemStatus.Active,
                "Prefers refunds",
                "The caller prefers refunds.",
                "prefers refunds",
                new MemoryProvenance("admin", [], null, now),
                now,
                now,
                MemoryScope.IdentityUser,
                instanceId,
                profileId), CancellationToken.None);
            await fixture.Triggers.CreateAsync(new Automation(
                Guid.Parse("019944af-00d1-7000-8000-00000000009a"),
                new TriggerOwner(instanceId, profileId),
                AutomationStatus.Active,
                "Remind",
                new OneShotSchedule(now.AddDays(1), "Asia/Ho_Chi_Minh", new DateOnly(2026, 9, 30), new TimeOnly(9, 0)),
                now.AddDays(1),
                null,
                0,
                1,
                1,
                new TriggerProvenance(TriggerAuthorizationOrigin.CurrentUserTurn, null, null, now, now),
                null), CancellationToken.None);
            await fixture.WorkItems.CreateAsync(WorkItem.Create(
                Guid.Parse("019944af-00d1-7000-8000-00000000009b"),
                new WorkOwner(instanceId, profileId),
                new WorkProvenance(
                    Guid.Parse("019944af-00d1-7000-8000-00000000009c"),
                    WorkSourceKind.Schedule,
                    null,
                    null,
                    null,
                    "source|delete",
                    now,
                    now,
                    """{"instruction":"ping"}""",
                    "field-guide",
                    1,
                    "Guide"),
                new WorkModelPin("scripted-alpha", "primary-llm", "scripted-alpha", null),
                3,
                now), CancellationToken.None);
            await fixture.Executions.CreateAsync(ConversationTurnExecution.AcceptNew(
                Guid.Parse("019944af-00d1-7000-8000-00000000009d"),
                Guid.Parse("019944af-00d1-7000-8000-00000000009e"),
                Guid.Parse("019944af-00d1-7000-8000-00000000009f"),
                Guid.Parse("019944af-00d1-7000-8000-0000000000a0"),
                Guid.Parse("019944af-00d1-7000-8000-0000000000a1"),
                instanceId,
                profileId,
                "field-guide",
                1,
                null,
                new WorkModelPin("scripted-alpha", "primary-llm", "scripted-alpha", null),
                now), CancellationToken.None);

            var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
                fixture.Deletion.DeleteInstanceAsync(
                    new AdminInstanceDeleteCommand(instanceId, 1, fixture.Ids.NewId(), now),
                    CancellationToken.None).AsTask());

            Assert.Equal("Conflict", error.Code);
            Assert.Contains("1 session", error.Message, StringComparison.Ordinal);
            Assert.Contains("1 learned memory item", error.Message, StringComparison.Ordinal);
            Assert.Contains("1 trigger registration", error.Message, StringComparison.Ordinal);
            Assert.Contains("1 background work item", error.Message, StringComparison.Ordinal);
            Assert.Contains("1 conversation execution", error.Message, StringComparison.Ordinal);
            Assert.Contains("Archive keeps it inactive", error.Message, StringComparison.Ordinal);
            Assert.NotNull(await fixture.Instances.FindAsync(instanceId, CancellationToken.None));
            var history = await fixture.Events.ListAsync(new AdminEventListQuery(), CancellationToken.None);
            Assert.DoesNotContain(history, item => item.Operation == AdminEventOperationKind.InstanceDeleted);
        });
    }

    [Theory]
    [InlineData(OccurrenceRoutingDisposition.Rejected, true)]
    [InlineData(OccurrenceRoutingDisposition.Pending, false)]
    [InlineData(OccurrenceRoutingDisposition.AwaitingDurableWork, false)]
    public async Task Cancelled_automation_allows_deletion_only_without_pending_or_durable_work(OccurrenceRoutingDisposition disposition, bool deletable)
    {
        await ForEachProfileAsync(async fixture =>
        {
            var now = DateTimeOffset.UtcNow;
            var id = Guid.NewGuid(); var automationId = Guid.NewGuid();
            var owner = new TriggerOwner(id, Guid.NewGuid());
            await fixture.Instances.InsertAsync(Instance(id, AgentInstanceLifecycle.Archived, now));
            await fixture.Triggers.CreateAsync(new Automation(automationId, owner,
                AutomationStatus.Cancelled, "Review experience",
                new OneShotSchedule(now.AddDays(1), "UTC", DateOnly.FromDateTime(now.UtcDateTime), new TimeOnly(9, 0)),
                null, null, 0, 1, 1, new(TriggerAuthorizationOrigin.AdminOwner, null, null, now, now), null));
            var occurrence = new TriggerOccurrence(Guid.NewGuid(), $"automation:{Guid.NewGuid()}", automationId, owner,
                TriggerSourceKind.ManualInvocation, now, now, now, "{}", null, 1, OccurrenceRoutingDisposition.Pending, null, 0, null, null, null);
            await fixture.Triggers.AdmitOccurrenceAsync(occurrence);
            if (disposition == OccurrenceRoutingDisposition.Rejected)
                Assert.NotNull(await fixture.Triggers.TryRejectPendingAsync(occurrence.OccurrenceId, "Automation deleted", now));
            if (disposition == OccurrenceRoutingDisposition.AwaitingDurableWork)
            {
                var claim = Guid.NewGuid();
                Assert.NotNull(await fixture.Triggers.TryClaimOccurrenceAsync(occurrence.OccurrenceId, claim, now.AddMinutes(1), now));
                Assert.NotNull(await fixture.Triggers.MarkAwaitingDurableWorkAsync(occurrence.OccurrenceId, claim, "Queue work", now));
            }
            var command = new AdminInstanceDeleteCommand(id, 1, fixture.Ids.NewId(), now);
            if (deletable)
            {
                await fixture.Deletion.DeleteInstanceAsync(command);
                Assert.Null(await fixture.Instances.FindAsync(id));
                Assert.Null(await fixture.Triggers.GetAsync(owner, automationId));
                Assert.Null(await fixture.Triggers.GetOccurrenceAsync(owner, occurrence.OccurrenceId));
                Assert.Contains(await fixture.Events.ListAsync(new AdminEventListQuery()), e => e.Operation == AdminEventOperationKind.InstanceDeleted);
            }
            else
            {
                await Assert.ThrowsAsync<AgentCoreException>(() => fixture.Deletion.DeleteInstanceAsync(command).AsTask());
                Assert.NotNull(await fixture.Instances.FindAsync(id));
                Assert.NotNull(await fixture.Triggers.GetOccurrenceAsync(owner, occurrence.OccurrenceId));
            }
        });
    }

    private static AgentInstance Instance(
        Guid instanceId,
        AgentInstanceLifecycle lifecycle,
        DateTimeOffset now) =>
        new(
            instanceId,
            "field-guide",
            1,
            new AgentIdentity("Guide", "role", "desc", "tone"),
            lifecycle,
            now,
            now);

    private static SessionSnapshot Snapshot(Guid instanceId)
    {
        var now = DateTimeOffset.Parse("2026-09-29T02:50:00Z");
        var definition = new AgentDefinition(
            1,
            "field-guide",
            1,
            new AgentIdentity("Guide", "role", "desc", "tone"),
            ["Help"],
            "instructions",
            new BehaviorPolicy("acknowledgeThenContinue", true, true),
            new ConversationPolicy("concise", true, "en", 256),
            new InitiativePolicy(false, 30000, 60000, 1, []),
            new VoiceConfiguration(false, "default", 1.0),
            new ProviderPreferences("primary-llm", null, null),
            new Dictionary<string, string>());
        return new SessionSnapshot(
            1,
            Guid.Parse("019944af-00d1-7000-8000-0000000000b1"),
            1,
            definition,
            SessionMode.Text,
            null,
            SessionStatus.Created,
            [],
            string.Empty,
            0,
            null,
            null,
            now,
            now, AgentInstanceId: instanceId) with
        {
            AgentInstanceId = instanceId
        };
    }
}
