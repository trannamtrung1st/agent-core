using AgentCore.Application.Identity;
using AgentCore.Application.Ports;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class TriggerInstancePolicyReconciliationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid ProfileId = Guid.Parse("019944af-00f1-7000-8000-0000000000b1");

    [Fact]
    public async Task Version_downgrade_suspends_active_registration_and_upgrade_reactivates()
    {
        await ForEachTriggerStore(async store => await VersionChangeMatrixAsync(store));
    }

    [Fact]
    public async Task Archive_suspends_active_registration_and_unarchive_reactivates_when_eligible()
    {
        await ForEachTriggerStore(async store => await ArchiveLifecycleMatrixAsync(store));
    }

    private static async Task VersionChangeMatrixAsync(ITriggerStore store)
    {
        var permissive = SampleDefinitions.Examiner with
        {
            Version = 1,
            TriggerPolicy = SchedulingPolicy()
        };
        var restrictive = permissive with
        {
            Version = 2,
            TriggerPolicy = new TriggerPolicy(
                false,
                false,
                false,
                false,
                false,
                false,
                0,
                0,
                1,
                [])
        };
        var definitions = new TwoVersionDefinitions(permissive, restrictive);
        var instances = new InMemoryAgentInstanceStore();
        var memory = new InMemoryMemoryStore();
        var clock = new FakeTimeProvider(Now);
        await memory.SaveProfileAsync(
            new UserProfile(
                ProfileId,
                1,
                new Dictionary<string, UserProfileValue>
                {
                    ["timeZone"] = new("UTC", UserProfileValueSource.UserSet, Now)
                },
                Now),
            0);

        var guard = new TriggerAdmissionGuard(instances, definitions, memory);
        var reconciliation = new TriggerInstancePolicyReconciliationService(store, guard);
        var service = new AgentInstanceService(
            instances,
            definitions,
            memory,
            Ids(8),
            clock,
            reconciliation);

        var managed = await service.CreateAsync("examiner", 1);
        var owner = new TriggerOwner(managed.InstanceId, ProfileId);
        var due = Now.AddHours(1);
        var registration = new TriggerRegistration(
            Guid.Parse("019944af-00e2-7000-8000-000000000001"),
            owner,
            TriggerRegistrationStatus.Active,
            "Reminder",
            new OneShotSchedule(due, "UTC", null, null),
            due,
            null,
            0,
            1,
            1,
            new TriggerProvenance(TriggerAuthorizationOrigin.CurrentUserTurn, null, null, Now, Now),
            null);
        await store.CreateAsync(registration);

        await service.UpgradeAsync(managed.InstanceId, 2, managed.Revision);
        var suspended = (await store.GetAsync(owner, registration.RegistrationId))!;
        Assert.Equal(TriggerRegistrationStatus.SuspendedPolicy, suspended.Status);
        Assert.NotNull(suspended.SuspensionReason);

        var upgraded = await instances.FindAsync(managed.InstanceId);
        Assert.NotNull(upgraded);
        await service.UpgradeAsync(upgraded!.InstanceId, 1, upgraded.Revision);
        var reactivated = (await store.GetAsync(owner, registration.RegistrationId))!;
        Assert.Equal(TriggerRegistrationStatus.Active, reactivated.Status);
        Assert.Null(reactivated.SuspensionReason);
    }

    private static async Task ArchiveLifecycleMatrixAsync(ITriggerStore store)
    {
        var definition = SampleDefinitions.Examiner with { Version = 1, TriggerPolicy = SchedulingPolicy() };
        var definitions = new TwoVersionDefinitions(definition, definition);
        var instances = new InMemoryAgentInstanceStore();
        var memory = new InMemoryMemoryStore();
        var clock = new FakeTimeProvider(Now);
        await memory.SaveProfileAsync(
            new UserProfile(
                ProfileId,
                1,
                new Dictionary<string, UserProfileValue> { ["timeZone"] = new("UTC", UserProfileValueSource.UserSet, Now) },
                Now),
            0);

        var guard = new TriggerAdmissionGuard(instances, definitions, memory);
        var reconciliation = new TriggerInstancePolicyReconciliationService(store, guard);
        var service = new AgentInstanceService(instances, definitions, memory, Ids(8), clock, reconciliation);
        var managed = await service.CreateAsync("examiner", 1);
        var owner = new TriggerOwner(managed.InstanceId, ProfileId);
        var due = Now.AddMinutes(1);
        var activeId = Guid.Parse("019944af-00e3-7000-8000-000000000001");
        var completedId = Guid.Parse("019944af-00e3-7000-8000-000000000002");
        await store.CreateAsync(new TriggerRegistration(
            activeId,
            owner,
            TriggerRegistrationStatus.Active,
            "Future reminder",
            new OneShotSchedule(due, "UTC", null, null),
            due,
            null,
            0,
            1,
            1,
            new TriggerProvenance(TriggerAuthorizationOrigin.CurrentUserTurn, null, null, Now, Now),
            null));
        await store.CreateAsync(new TriggerRegistration(
            completedId,
            owner,
            TriggerRegistrationStatus.Completed,
            "Done",
            new OneShotSchedule(Now.AddHours(-1), "UTC", null, null),
            null,
            null,
            1,
            1,
            1,
            new TriggerProvenance(TriggerAuthorizationOrigin.CurrentUserTurn, null, null, Now, Now),
            null));

        var archived = await service.SetLifecycleAsync(managed.InstanceId, AgentInstanceLifecycle.Archived, managed.Revision);
        Assert.Equal(AgentInstanceLifecycle.Archived, archived.Lifecycle);
        var suspended = (await store.GetAsync(owner, activeId))!;
        Assert.Equal(TriggerRegistrationStatus.SuspendedPolicy, suspended.Status);
        Assert.Equal(TriggerRegistrationStatus.Completed, (await store.GetAsync(owner, completedId))!.Status);

        clock.Advance(TimeSpan.FromMinutes(2));
        var scheduler = new TriggerScheduler(store, NullLogger<TriggerScheduler>.Instance, guard);
        var pass = await scheduler.RunOnceAsync(clock.GetUtcNow());
        Assert.Equal(0, pass.Admitted);
        Assert.Equal(TriggerRegistrationStatus.SuspendedPolicy, (await store.GetAsync(owner, activeId))!.Status);

        var restored = await service.SetLifecycleAsync(archived.InstanceId, AgentInstanceLifecycle.Active, archived.Revision);
        Assert.Equal(TriggerRegistrationStatus.Active, (await store.GetAsync(owner, activeId))!.Status);
        Assert.Equal(TriggerRegistrationStatus.Completed, (await store.GetAsync(owner, completedId))!.Status);
        _ = restored;
    }

    private static async Task ForEachTriggerStore(Func<ITriggerStore, Task> exercise)
    {
        await exercise(new InMemoryTriggerStore());
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-p7e-reconcile-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new SqliteFactory(options);
        try
        {
            await new SqliteMemoryStore(factory, new FakeTimeProvider(Now)).EnsureCreatedAsync();
            await exercise(new SqliteTriggerStore(factory));
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

    private static TriggerPolicy SchedulingPolicy() =>
        new(
            true,
            true,
            true,
            true,
            true,
            true,
            32,
            365,
            1,
            [OccurrenceCompatibility.Schedule]);

    private sealed class TwoVersionDefinitions(AgentDefinition permissive, AgentDefinition restrictive) : IAgentDefinitionStore
    {
        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AgentDefinition>>([permissive, restrictive]);

        public ValueTask<AgentDefinition?> GetAsync(string id, int? version = null, CancellationToken cancellationToken = default)
        {
            if (!string.Equals(id, permissive.Id, StringComparison.Ordinal))
            {
                return ValueTask.FromResult<AgentDefinition?>(null);
            }

            return version switch
            {
                1 => ValueTask.FromResult<AgentDefinition?>(permissive),
                2 => ValueTask.FromResult<AgentDefinition?>(restrictive),
                null => ValueTask.FromResult<AgentDefinition?>(permissive),
                _ => ValueTask.FromResult<AgentDefinition?>(null)
            };
        }
    }

    private static DeterministicIdGenerator Ids(int count) =>
        new(
            Enumerable.Range(1, count).Select(index => Guid.Parse($"019944af-00e2-7000-8000-{index:D12}")),
            [Guid.Parse("019944af-00e2-7000-8000-0000000000ff")]);

    private sealed class SqliteFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
