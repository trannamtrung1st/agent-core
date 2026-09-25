using AgentCore.Application.Agents;
using AgentCore.Application.Identity;
using AgentCore.Application.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Memory;
using AgentCore.Domain.Triggers;
using AgentCore.Domain.Work;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class ManagedInstanceP7DRegressionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid ProfileId = Guid.Parse("019944af-00f1-7000-8000-0000000000b1");

    [Fact]
    public async Task Two_managed_instances_do_not_share_trigger_registrations()
    {
        var definitions = new VersionedDefinitions(SampleDefinitions.Examiner);
        var instances = new InMemoryAgentInstanceStore();
        var memory = new InMemoryMemoryStore();
        var clock = new FakeTimeProvider(Now);
        var service = Service(instances, definitions, memory, clock);
        var first = await service.CreateAsync("examiner", 1);
        var second = await service.CreateAsync("examiner", 1);
        Assert.NotEqual(first.InstanceId, second.InstanceId);

        var store = new InMemoryTriggerStore();
        var ownerFirst = new TriggerOwner(first.InstanceId, ProfileId);
        var ownerSecond = new TriggerOwner(second.InstanceId, ProfileId);
        var due = Now.AddHours(1);
        await store.CreateAsync(new TriggerRegistration(
            Guid.Parse("019944af-00f1-7000-8000-000000000001"),
            ownerFirst,
            TriggerRegistrationStatus.Completed,
            "First instance reminder",
            new OneShotSchedule(due, "UTC", null, null),
            due,
            due,
            0,
            1,
            1,
            new TriggerProvenance(TriggerAuthorizationOrigin.CurrentUserTurn, null, null, Now, Now),
            null));

        Assert.Single(await store.ListAsync(ownerFirst, null));
        Assert.Empty(await store.ListAsync(ownerSecond, null));
        Assert.Null(await store.GetAsync(ownerSecond, Guid.Parse("019944af-00f1-7000-8000-000000000001")));
    }

    private const string FirstInstanceIdentityMemory = "P7D_FIRST_INSTANCE_IDENTITY_USER_SENTINEL";

    [Fact]
    public async Task Two_managed_instances_do_not_share_identity_user_memory()
    {
        await ForEachDurableStore(async (sessions, instances, structured, triggers, clock) =>
        {
            _ = triggers;
            var definitions = new VersionedDefinitions(SampleDefinitions.Examiner);
            var service = Service(instances, definitions, sessions, clock);
            var manager = Manager(definitions, sessions, service, clock, structured);
            var first = await service.CreateAsync("examiner", 1);
            var second = await service.CreateAsync("examiner", 1);
            var sessionFirst = await manager.CreateForInstanceAsync(first.InstanceId, SessionMode.Text);
            _ = await manager.CreateForInstanceAsync(second.InstanceId, SessionMode.Text);

            var memory = new StructuredMemoryService(structured, Ids(12, "019944af-00f9-7000-8000-"), clock);
            var admission = new MemoryAdmissionContext("application", [], new HashSet<string>(StringComparer.Ordinal));
            var source = await memory.WriteAsync(
                new TrustedMemoryOwner(sessionFirst.SessionId),
                new MemoryWriteProposal(MemoryKind.Fact, "Learned preference", FirstInstanceIdentityMemory, []),
                admission);
            var promoted = await memory.PromoteToIdentityUserAsync(
                new TrustedMemoryOwner(sessionFirst.SessionId),
                source.MemoryId,
                new TrustedIdentityUserOwner(first.InstanceId, ProfileId),
                promotionAllowed: true,
                admission);
            Assert.Equal(MemoryScope.IdentityUser, promoted.Scope);
            Assert.Equal(first.InstanceId, promoted.OwnerInstanceId);

            var onFirst = await memory.SearchIdentityUserAsync(
                new TrustedIdentityUserOwner(first.InstanceId, ProfileId),
                new MemorySearchQuery(null, null),
                retrievalAllowed: true,
                admission);
            Assert.Contains(onFirst, item => item.Content == FirstInstanceIdentityMemory);

            var onSecond = await memory.SearchIdentityUserAsync(
                new TrustedIdentityUserOwner(second.InstanceId, ProfileId),
                new MemorySearchQuery(null, null),
                retrievalAllowed: true,
                admission);
            Assert.DoesNotContain(onSecond, item => item.Content.Contains(FirstInstanceIdentityMemory, StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task Two_managed_instances_do_not_share_session_scoped_memory()
    {
        var definitions = new VersionedDefinitions(SampleDefinitions.Examiner);
        var instances = new InMemoryAgentInstanceStore();
        var sessions = new InMemoryMemoryStore();
        var structured = new InMemoryStructuredMemoryStore();
        var clock = new FakeTimeProvider(Now);
        var service = Service(instances, definitions, sessions, clock);
        var manager = Manager(definitions, sessions, service, clock, structured);
        var first = await service.CreateAsync("examiner", 1);
        var second = await service.CreateAsync("examiner", 1);
        var sessionFirst = await manager.CreateForInstanceAsync(first.InstanceId, SessionMode.Text);
        var sessionSecond = await manager.CreateForInstanceAsync(second.InstanceId, SessionMode.Text);

        var memory = new StructuredMemoryService(structured, Ids(8, "019944af-00f2-7000-8000-"), clock);
        var admission = new MemoryAdmissionContext("application", [], new HashSet<string>(StringComparer.Ordinal));
        await memory.WriteAsync(
            new TrustedMemoryOwner(sessionFirst.SessionId),
            new MemoryWriteProposal(MemoryKind.Fact, "Instance A", "only on first managed instance", []),
            admission);

        var onFirst = await memory.SearchAsync(
            new TrustedMemoryOwner(sessionFirst.SessionId),
            new MemorySearchQuery(null, null),
            admission);
        Assert.Contains(onFirst, item => item.Content == "only on first managed instance");

        var onSecond = await memory.SearchAsync(
            new TrustedMemoryOwner(sessionSecond.SessionId),
            new MemorySearchQuery(null, null),
            admission);
        Assert.DoesNotContain(onSecond, item => item.Content.Contains("only on first managed instance", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Archived_managed_instance_retains_session_memory_and_trigger_history()
    {
        await ForEachDurableStore(async (sessions, instances, structured, triggers, clock) =>
        {
            var definitions = new VersionedDefinitions(SampleDefinitions.Examiner);
            var service = Service(instances, definitions, sessions, clock);
            var manager = Manager(definitions, sessions, service, clock, structured);
            var managed = await service.CreateAsync("examiner", 1);
            var session = await manager.CreateForInstanceAsync(managed.InstanceId, SessionMode.Text);
            await sessions.SaveAsync(
                session with
                {
                    Revision = session.Revision + 1,
                    Entries =
                    [
                        new ConversationEntry(
                            Guid.Parse("019944af-00f3-7000-8000-000000000001"),
                            1,
                            null,
                            ConversationRole.User,
                            "archived history line",
                            null,
                            EntryStatus.Completed,
                            SessionMode.Text,
                            0,
                            21,
                            Now)
                    ],
                    LastEntrySequence = 1
                },
                session.Revision);

            var memory = new StructuredMemoryService(structured, Ids(8, "019944af-00f4-7000-8000-"), clock);
            var admission = new MemoryAdmissionContext("application", [], new HashSet<string>(StringComparer.Ordinal));
            await memory.WriteAsync(
                new TrustedMemoryOwner(session.SessionId),
                new MemoryWriteProposal(MemoryKind.Fact, "Retention", "survives archive", []),
                admission);
            var identitySource = await memory.WriteAsync(
                new TrustedMemoryOwner(session.SessionId),
                new MemoryWriteProposal(MemoryKind.Fact, "Identity retention", FirstInstanceIdentityMemory, []),
                admission);
            await memory.PromoteToIdentityUserAsync(
                new TrustedMemoryOwner(session.SessionId),
                identitySource.MemoryId,
                new TrustedIdentityUserOwner(managed.InstanceId, ProfileId),
                promotionAllowed: true,
                admission);

            var owner = new TriggerOwner(managed.InstanceId, ProfileId);
            var registrationId = Guid.Parse("019944af-00f3-7000-8000-000000000002");
            var due = Now.AddHours(2);
            await triggers.CreateAsync(new TriggerRegistration(
                registrationId,
                owner,
                TriggerRegistrationStatus.Completed,
                "Completed schedule",
                new OneShotSchedule(due, "UTC", null, null),
                due,
                due,
                0,
                1,
                1,
                new TriggerProvenance(TriggerAuthorizationOrigin.CurrentUserTurn, null, null, Now, Now),
                null));

            var archived = await service.SetLifecycleAsync(
                managed.InstanceId,
                AgentInstanceLifecycle.Archived,
                managed.Revision);
            Assert.Equal(AgentInstanceLifecycle.Archived, archived.Lifecycle);

            var reloaded = (await sessions.LoadAsync(session.SessionId))!;
            Assert.Equal("archived history line", Assert.Single(reloaded.Entries).Text);
            Assert.Contains(
                (await memory.SearchAsync(
                    new TrustedMemoryOwner(session.SessionId),
                    new MemorySearchQuery(null, null),
                    admission)).Select(item => item.Content),
                content => content == "survives archive");
            var identityAfterArchive = await memory.SearchIdentityUserAsync(
                new TrustedIdentityUserOwner(managed.InstanceId, ProfileId),
                new MemorySearchQuery(null, null),
                retrievalAllowed: true,
                admission);
            Assert.Contains(identityAfterArchive, item => item.Content == FirstInstanceIdentityMemory);
            var trigger = await triggers.GetAsync(owner, registrationId);
            Assert.NotNull(trigger);
            Assert.Equal(TriggerRegistrationStatus.Completed, trigger.Status);
            Assert.Equal("Completed schedule", trigger.Intent);
        });
    }

    [Fact]
    public async Task Archived_managed_instance_leaves_accepted_running_work_unchanged()
    {
        await ForEachWorkStore(async work =>
        {
            var definitions = new VersionedDefinitions(SampleDefinitions.Examiner);
            var instances = new InMemoryAgentInstanceStore();
            var sessions = new InMemoryMemoryStore();
            var clock = new FakeTimeProvider(Now);
            var service = Service(instances, definitions, sessions, clock);
            var managed = await service.CreateAsync("examiner", 1);
            var owner = new WorkOwner(managed.InstanceId, ProfileId);
            var workItemId = Guid.Parse("019944af-00f5-7000-8000-000000000001");
            var sourceId = Guid.Parse("019944af-00f5-7000-8000-000000000002");
            var created = await work.CreateAsync(NewWorkItem(owner, workItemId, sourceId, Now));
            var generation = Guid.Parse("019944af-00f5-7000-8000-000000000003");
            var running = await work.TryClaimAsync(created.Item.WorkItemId, generation, Now, Now.AddMinutes(5));
            Assert.NotNull(running);
            Assert.Equal(WorkItemStatus.Running, running.Status);

            var archived = await service.SetLifecycleAsync(
                managed.InstanceId,
                AgentInstanceLifecycle.Archived,
                managed.Revision);
            Assert.Equal(AgentInstanceLifecycle.Archived, archived.Lifecycle);

            var after = await work.GetAsync(owner, workItemId);
            Assert.NotNull(after);
            Assert.Equal(WorkItemStatus.Running, after.Status);
            Assert.Equal(running.Revision, after.Revision);
            Assert.Equal(generation, after.Claim!.Generation);
            Assert.False(after.CancellationRequested);
        });
    }

    private static async Task ForEachDurableStore(
        Func<IMemoryStore, IAgentInstanceStore, IStructuredMemoryStore, ITriggerStore, TimeProvider, Task> exercise)
    {
        var clock = new FakeTimeProvider(Now);
        await exercise(
            new InMemoryMemoryStore(),
            new InMemoryAgentInstanceStore(),
            new InMemoryStructuredMemoryStore(),
            new InMemoryTriggerStore(),
            clock);

        var path = Path.Combine(Path.GetTempPath(), $"agent-core-p7d-archive-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new SqliteFactory(options);
        var sessions = new SqliteMemoryStore(factory, clock);
        try
        {
            await sessions.EnsureCreatedAsync();
            await exercise(
                sessions,
                new SqliteAgentInstanceStore(factory),
                new SqliteStructuredMemoryStore(factory),
                new SqliteTriggerStore(factory),
                clock);
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

    private static async Task ForEachWorkStore(Func<IWorkItemStore, Task> exercise)
    {
        await exercise(new InMemoryWorkItemStore());
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-p7d-work-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new SqliteFactory(options);
        try
        {
            await new SqliteMemoryStore(factory, new FakeTimeProvider(Now)).EnsureCreatedAsync();
            await exercise(new SqliteWorkItemStore(factory));
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

    private static AgentInstanceService Service(
        IAgentInstanceStore instances,
        IAgentDefinitionStore definitions,
        IMemoryStore sessions,
        TimeProvider clock) =>
        new(instances, definitions, sessions, Ids(8, "019944af-00f6-7000-8000-"), clock);

    private static SessionManager Manager(
        IAgentDefinitionStore definitions,
        IMemoryStore sessions,
        IAgentInstanceService instances,
        TimeProvider clock,
        IStructuredMemoryStore structured) =>
        new(
            definitions,
            sessions,
            Ids(16, "019944af-00f7-7000-8000-"),
            clock,
            new VoiceAvailability { SpeechAdaptersResolved = true },
            structuredMemory: structured,
            instances: instances);

    private static WorkItem NewWorkItem(WorkOwner owner, Guid workItemId, Guid sourceId, DateTimeOffset createdAt) =>
        WorkItem.Create(
            workItemId,
            owner,
            new WorkProvenance(
                sourceId,
                WorkSourceKind.ApplicationEvent,
                null,
                Guid.Parse("019944af-00f5-7000-8000-000000000099"),
                null,
                $"source|{sourceId:N}",
                createdAt,
                createdAt,
                """{"instruction":"synthetic"}""",
                "examiner",
                1,
                "Alex"),
            new WorkModelPin("synthetic-default", "synthetic", "synthetic-small", "minimal"),
            3,
            createdAt);

    private static DeterministicIdGenerator Ids(int count, string prefix) =>
        new(
            Enumerable.Range(1, count).Select(index => Guid.Parse($"{prefix}{index:D12}")),
            [Guid.Parse("019944af-00f8-7000-8000-0000000000ff")]);

    private sealed class VersionedDefinitions(AgentDefinition definition) : IAgentDefinitionStore
    {
        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AgentDefinition>>([definition]);

        public ValueTask<AgentDefinition?> GetAsync(string id, int? version = null, CancellationToken cancellationToken = default)
        {
            if (!string.Equals(id, definition.Id, StringComparison.Ordinal))
            {
                return ValueTask.FromResult<AgentDefinition?>(null);
            }

            return version is { } exact && exact != definition.Version
                ? ValueTask.FromResult<AgentDefinition?>(null)
                : ValueTask.FromResult<AgentDefinition?>(definition);
        }
    }

    private sealed class SqliteFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
