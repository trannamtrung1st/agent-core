using AgentCore.Application.Admin;
using AgentCore.Application.Identity;
using AgentCore.Application.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Memory;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Admin;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Infrastructure.Tests;

public sealed class InMemoryAdminP7eHistoryMutatorTests : AdminP7eHistoryMutatorTests
{
    protected override async Task ForEachProfileAsync(Func<P7eMutatorFixture, Task> exercise)
    {
        var clock = new FakeTimeProvider(Now);
        var ids = new SystemIdGenerator(clock);
        await exercise(await CreateInMemoryFixtureAsync(clock, ids, new InMemoryAdminEventStore(ids)));
    }

    protected override async Task ForEachFailingAppendProfileAsync(Func<P7eMutatorFixture, Task> exercise)
    {
        var clock = new FakeTimeProvider(Now);
        var ids = new SystemIdGenerator(clock);
        await exercise(await CreateInMemoryFixtureAsync(clock, ids, new ThrowingAdminEventStore(ids)));
    }
}

public sealed class SqliteAdminP7eHistoryMutatorTests : AdminP7eHistoryMutatorTests
{
    protected override Task ForEachProfileAsync(Func<P7eMutatorFixture, Task> exercise) =>
        WithSqliteAsync(async factory =>
        {
            var fixture = await CreateSqliteFixtureAsync(factory, new FakeTimeProvider(Now), new SystemIdGenerator(TimeProvider.System));
            await exercise(fixture);
        });

    protected override Task ForEachFailingAppendProfileAsync(Func<P7eMutatorFixture, Task> exercise) =>
        WithSqliteAsync(async factory =>
        {
            var fixture = await CreateSqliteFixtureAsync(factory, new FakeTimeProvider(Now), new SystemIdGenerator(TimeProvider.System));
            await exercise(fixture);
        });

    [Fact]
    public async Task Reset_rolls_back_when_history_append_fails_on_sqlite()
    {
        await WithSqliteAsync(async factory =>
        {
            var clock = new FakeTimeProvider(Now);
            var ids = new FailingOnNextNewIdGenerator(new SystemIdGenerator(clock));
            var fixture = await CreateSqliteFixtureAsync(factory, clock, ids);
            ids.FailNextNewId = true;

            var placeholder = AdminEventFactory.MemoryScopeReset(
                Guid.Parse("019944af-00f3-7000-8000-000000000001"),
                Now,
                fixture.InstanceId,
                AdminLearnedMemoryScope.IdentityUser,
                itemsRemoved: 0,
                sessionId: null);

            await Assert.ThrowsAnyAsync<Exception>(() =>
                fixture.Mutator.ResetLearnedMemoryScopeWithHistoryAsync(
                    fixture.InstanceId,
                    AdminLearnedMemoryScope.IdentityUser,
                    sessionId: null,
                    placeholder,
                    (_, _, _) => { },
                    CancellationToken.None).AsTask());

            var restored = await fixture.MemoryService.FindActiveIdentityUserBySubjectAsync(
                new TrustedIdentityUserOwner(fixture.InstanceId, ProfileId),
                MemoryKind.Fact,
                "Subject");
            Assert.NotNull(restored);
        });
    }

    private static async Task WithSqliteAsync(Func<IDbContextFactory<AgentCoreDbContext>, Task> exercise)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-p7e-history-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new SqliteContextFactory(options);
        try
        {
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            await exercise(factory);
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
}

public abstract class AdminP7eHistoryMutatorTests
{
    protected static readonly DateTimeOffset Now = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
    protected static readonly Guid ProfileId = LocalUserProfile.Id;

    protected sealed record P7eMutatorFixture(
        IAdminP7eHistoryMutator Mutator,
        AdminAutomationHistoryService AutomationHistory,
        StructuredMemoryService MemoryService,
        AgentInstanceService InstanceService,
        TriggerRegistrationService Triggers,
        Guid InstanceId,
        Guid IdentityMemoryId,
        Guid RegistrationId,
        long RegistrationRevision,
        bool SqliteProfile,
        IDbContextFactory<AgentCoreDbContext>? DbFactory);

    protected abstract Task ForEachProfileAsync(Func<P7eMutatorFixture, Task> exercise);

    protected abstract Task ForEachFailingAppendProfileAsync(Func<P7eMutatorFixture, Task> exercise);

    [Fact]
    public async Task Delete_rolls_back_when_history_append_fails()
    {
        await ForEachFailingAppendProfileAsync(async fixture =>
        {
            var append = BuildDeleteAppend(fixture, fixture.IdentityMemoryId);
            if (fixture.SqliteProfile)
            {
                append = append with { SummaryJson = """{"unexpected":true}""" };
            }

            await Assert.ThrowsAnyAsync<Exception>(() =>
                fixture.Mutator.DeleteLearnedMemoryWithHistoryAsync(
                    fixture.InstanceId,
                    AdminLearnedMemoryScope.IdentityUser,
                    fixture.IdentityMemoryId,
                    sessionId: null,
                    append,
                    (_, _) => { },
                    CancellationToken.None).AsTask());

            var restored = await fixture.MemoryService.FindActiveIdentityUserBySubjectAsync(
                new TrustedIdentityUserOwner(fixture.InstanceId, ProfileId),
                MemoryKind.Fact,
                "Subject");
            Assert.NotNull(restored);
        });
    }

    [Fact]
    public async Task Reset_rolls_back_when_history_append_fails()
    {
        await ForEachFailingAppendProfileAsync(async fixture =>
        {
            if (fixture.SqliteProfile)
            {
                return;
            }

            var placeholder = AdminEventFactory.MemoryScopeReset(
                Guid.Parse("019944af-00f3-7000-8000-000000000001"),
                Now,
                fixture.InstanceId,
                AdminLearnedMemoryScope.IdentityUser,
                itemsRemoved: 0,
                sessionId: null);

            await Assert.ThrowsAnyAsync<Exception>(() =>
                fixture.Mutator.ResetLearnedMemoryScopeWithHistoryAsync(
                    fixture.InstanceId,
                    AdminLearnedMemoryScope.IdentityUser,
                    sessionId: null,
                    placeholder,
                    (_, _, _) => { },
                    CancellationToken.None).AsTask());

            var restored = await fixture.MemoryService.FindActiveIdentityUserBySubjectAsync(
                new TrustedIdentityUserOwner(fixture.InstanceId, ProfileId),
                MemoryKind.Fact,
                "Subject");
            Assert.NotNull(restored);
        });
    }

    [Fact]
    public async Task Revoke_rolls_back_when_history_append_fails()
    {
        await ForEachFailingAppendProfileAsync(async fixture =>
        {
            var append = AdminEventFactory.TriggerRegistrationRevoked(
                Guid.Parse("019944af-00f4-7000-8000-000000000003"),
                Now,
                fixture.InstanceId,
                fixture.RegistrationId,
                fixture.RegistrationRevision);
            if (fixture.SqliteProfile)
            {
                append = append with { SummaryJson = """{"unexpected":true}""" };
            }

            await Assert.ThrowsAnyAsync<Exception>(() =>
                fixture.Mutator.CancelTriggerRegistrationWithHistoryAsync(
                    fixture.InstanceId,
                    fixture.RegistrationId,
                    fixture.RegistrationRevision,
                    append,
                    (_, _) => { },
                    CancellationToken.None).AsTask());

            var registration = await fixture.Triggers.GetAsync(
                new TriggerOwner(fixture.InstanceId, ProfileId),
                fixture.RegistrationId);
            Assert.NotNull(registration);
            Assert.Equal(TriggerRegistrationStatus.Active, registration!.Status);
        });
    }

    [Fact]
    public async Task Delete_rejects_memory_outside_requested_instance_identity_scope()
    {
        await ForEachProfileAsync(async fixture =>
        {
            var otherInstance = await fixture.InstanceService.CreateAsync("examiner", 1);
            var append = AdminEventFactory.MemoryItemDeleted(
                Guid.Parse("019944af-00f8-7000-8000-000000000001"),
                Now,
                otherInstance.InstanceId,
                AdminLearnedMemoryScope.IdentityUser,
                fixture.IdentityMemoryId,
                sessionId: null);

            var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
                fixture.Mutator.DeleteLearnedMemoryWithHistoryAsync(
                    otherInstance.InstanceId,
                    AdminLearnedMemoryScope.IdentityUser,
                    fixture.IdentityMemoryId,
                    sessionId: null,
                    append,
                    (_, _) => { },
                    CancellationToken.None).AsTask());
            Assert.Equal(404, error.StatusCode);

            var retained = await fixture.MemoryService.FindActiveIdentityUserBySubjectAsync(
                new TrustedIdentityUserOwner(fixture.InstanceId, ProfileId),
                MemoryKind.Fact,
                "Subject");
            Assert.NotNull(retained);
        });
    }

    [Fact]
    public async Task Delete_rejects_identity_memory_when_scope_does_not_match()
    {
        await ForEachProfileAsync(async fixture =>
        {
            var append = AdminEventFactory.MemoryItemDeleted(
                Guid.Parse("019944af-00f9-7000-8000-000000000001"),
                Now,
                fixture.InstanceId,
                AdminLearnedMemoryScope.User,
                fixture.IdentityMemoryId,
                sessionId: null);

            _ = await Assert.ThrowsAsync<AgentCoreException>(() =>
                fixture.Mutator.DeleteLearnedMemoryWithHistoryAsync(
                    fixture.InstanceId,
                    AdminLearnedMemoryScope.User,
                    fixture.IdentityMemoryId,
                    sessionId: null,
                    append,
                    (_, _) => { },
                    CancellationToken.None).AsTask());

            var retained = await fixture.MemoryService.FindActiveIdentityUserBySubjectAsync(
                new TrustedIdentityUserOwner(fixture.InstanceId, ProfileId),
                MemoryKind.Fact,
                "Subject");
            Assert.NotNull(retained);
        });
    }

    [Fact]
    public async Task Trigger_revoke_retry_with_same_operation_id_but_different_revision_is_rejected()
    {
        await ForEachProfileAsync(async fixture =>
        {
            var operationId = Guid.Parse("019944af-00f5-7000-8000-000000000004");
            await fixture.AutomationHistory.CancelRegistrationWithHistoryAsync(
                fixture.InstanceId,
                fixture.RegistrationId,
                fixture.RegistrationRevision,
                operationId,
                Now);

            var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
                fixture.AutomationHistory.CancelRegistrationWithHistoryAsync(
                    fixture.InstanceId,
                    fixture.RegistrationId,
                    fixture.RegistrationRevision + 99,
                    operationId,
                    Now).AsTask());
            Assert.Contains("does not match the retried command", error.Message, StringComparison.Ordinal);
        });
    }

    private static AdminEventAppend BuildDeleteAppend(P7eMutatorFixture fixture, Guid memoryId) =>
        AdminEventFactory.MemoryItemDeleted(
            Guid.Parse("019944af-00f2-7000-8000-000000000002"),
            Now,
            fixture.InstanceId,
            AdminLearnedMemoryScope.IdentityUser,
            memoryId,
            sessionId: null);

    protected static async Task<P7eMutatorFixture> CreateInMemoryFixtureAsync(
        FakeTimeProvider clock,
        IIdGenerator ids,
        InMemoryAdminEventStore events)
    {
        var structured = new InMemoryStructuredMemoryStore();
        var instances = new InMemoryAgentInstanceStore();
        var sessions = new InMemoryMemoryStore();
        var definition = SampleExaminerDefinition() with
        {
            MemoryPolicy = new MemoryPolicy(true, true, true, false, false),
            TriggerPolicy = SchedulingPolicy()
        };
        var definitions = new SingleDefinitionStore(definition);
        var instanceService = new AgentInstanceService(instances, definitions, sessions, ids, clock);
        var memoryService = new StructuredMemoryService(structured, ids, clock);
        var profile = new FixedProfile(ProfileId, clock);
        var adminMemory = new AdminMemoryService(instances, definitions, sessions, structured, memoryService, profile);
        var durable = new InMemoryDurableState();
        var triggers = new TriggerRegistrationService(new InMemoryTriggerStore(durable), ids, clock);
        var automation = new AdminAutomationService(instances, triggers, profile);
        var mutator = new InMemoryAdminP7eHistoryMutator(adminMemory, events, structured, automation, durable);
        var automationHistory = new AdminAutomationHistoryService(mutator, automation, ids, clock);
        return await BuildFixtureAsync(
            mutator,
            automationHistory,
            memoryService,
            instanceService,
            triggers,
            automation,
            sqliteProfile: false,
            dbFactory: null);
    }

    protected static async Task<P7eMutatorFixture> CreateSqliteFixtureAsync(
        IDbContextFactory<AgentCoreDbContext> factory,
        FakeTimeProvider clock,
        IIdGenerator ids)
    {
        var structured = new SqliteStructuredMemoryStore(factory);
        var instances = new SqliteAgentInstanceStore(factory, ids);
        var sessions = new SqliteMemoryStore(factory, clock);
        var definition = SampleExaminerDefinition() with
        {
            MemoryPolicy = new MemoryPolicy(true, true, true, false, false),
            TriggerPolicy = SchedulingPolicy()
        };
        var definitions = new SingleDefinitionStore(definition);
        var instanceService = new AgentInstanceService(instances, definitions, sessions, ids, clock);
        var memoryService = new StructuredMemoryService(structured, ids, clock);
        var profile = new FixedProfile(ProfileId, clock);
        var adminMemory = new AdminMemoryService(instances, definitions, sessions, structured, memoryService, profile);
        var triggers = new TriggerRegistrationService(new SqliteTriggerStore(factory), ids, clock);
        var automation = new AdminAutomationService(instances, triggers, profile);
        var mutator = new SqliteAdminP7eHistoryMutator(
            adminMemory,
            automation,
            profile,
            factory,
            ids,
            clock);
        var automationHistory = new AdminAutomationHistoryService(mutator, automation, ids, clock);
        return await BuildFixtureAsync(
            mutator,
            automationHistory,
            memoryService,
            instanceService,
            triggers,
            automation,
            sqliteProfile: true,
            dbFactory: factory);
    }

    private static async Task<P7eMutatorFixture> BuildFixtureAsync(
        IAdminP7eHistoryMutator mutator,
        AdminAutomationHistoryService automationHistory,
        StructuredMemoryService memoryService,
        AgentInstanceService instanceService,
        TriggerRegistrationService triggers,
        AdminAutomationService automation,
        bool sqliteProfile,
        IDbContextFactory<AgentCoreDbContext>? dbFactory)
    {
        var instance = await instanceService.CreateAsync("examiner", 1);
        var admission = new MemoryAdmissionContext("test", [], new HashSet<string>(StringComparer.Ordinal));
        var sessionId = Guid.Parse("019944af-00f1-7000-8000-000000000001");
        var item = await memoryService.WriteAsync(
            new TrustedMemoryOwner(sessionId),
            new MemoryWriteProposal(MemoryKind.Fact, "Subject", "CONTENT", []),
            admission);
        await memoryService.PromoteToIdentityUserAsync(
            new TrustedMemoryOwner(sessionId),
            item.MemoryId,
            new TrustedIdentityUserOwner(instance.InstanceId, ProfileId),
            promotionAllowed: true,
            admission);
        var identity = await memoryService.FindActiveIdentityUserBySubjectAsync(
            new TrustedIdentityUserOwner(instance.InstanceId, ProfileId),
            MemoryKind.Fact,
            "Subject");
        Assert.NotNull(identity);

        var due = Now.AddHours(2);
        var registration = await triggers.CreateAsync(
            new TriggerRegistrationDraft(
                new TriggerOwner(instance.InstanceId, ProfileId),
                "Check in",
                new OneShotSchedule(due, "UTC", null, null),
                due,
                null,
                TriggerAuthorizationOrigin.CurrentUserTurn,
                sessionId,
                null));

        _ = await automation.GetRegistrationAsync(instance.InstanceId, registration.RegistrationId);

        return new P7eMutatorFixture(
            mutator,
            automationHistory,
            memoryService,
            instanceService,
            triggers,
            instance.InstanceId,
            identity!.MemoryId,
            registration.RegistrationId,
            registration.Revision,
            sqliteProfile,
            dbFactory);
    }

    private static AgentDefinition SampleExaminerDefinition() =>
        new(
            1,
            "examiner",
            1,
            new AgentIdentity("Alex", "Examiner", "Practice.", "Calm"),
            ["Conduct practice"],
            "You are Alex.",
            new BehaviorPolicy("acknowledgeThenContinue", true, true),
            new ConversationPolicy("concise", true, "en", 256),
            new InitiativePolicy(true, 8000, 30000, 1, ["longSilence"]),
            new VoiceConfiguration(true, "default", 1.0),
            new ProviderPreferences("primary-llm", "primary-stt", "primary-tts"),
            new Dictionary<string, string>());

    private static TriggerPolicy SchedulingPolicy() =>
        new(true, true, true, true, true, true, 32, 365, 1, [OccurrenceCompatibility.Schedule]);

    private sealed class SingleDefinitionStore(AgentDefinition definition) : IAgentDefinitionStore
    {
        public ValueTask<AgentDefinition?> GetAsync(string id, int? version = null, CancellationToken cancellationToken = default) =>
            string.Equals(id, definition.Id, StringComparison.Ordinal) && (version is null || version == definition.Version)
                ? ValueTask.FromResult<AgentDefinition?>(definition)
                : ValueTask.FromResult<AgentDefinition?>(null);

        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AgentDefinition>>([definition]);
    }

    private sealed class FixedProfile(Guid profileId, TimeProvider clock) : ILocalUserProfileService
    {
        public ValueTask<UserProfile> GetLocalProfileAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new UserProfile(profileId, 1, new Dictionary<string, UserProfileValue>(StringComparer.Ordinal), clock.GetUtcNow()));

        public ValueTask<UserProfile> UpdateLocalProfileAsync(
            long expectedRevision,
            IReadOnlyDictionary<string, string?> values,
            UserProfileValueSource source,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    protected sealed class SqliteContextFactory(DbContextOptions<AgentCoreDbContext> options)
        : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public ValueTask<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CreateDbContext());
    }

}

internal sealed class FailingOnNextNewIdGenerator(IIdGenerator inner) : IIdGenerator
{
    public bool FailNextNewId { get; set; }

    public Guid NewId()
    {
        if (FailNextNewId)
        {
            FailNextNewId = false;
            throw new InvalidOperationException("Admin history append failed.");
        }

        return inner.NewId();
    }

    public Guid NewSessionId() => inner.NewSessionId();
}
