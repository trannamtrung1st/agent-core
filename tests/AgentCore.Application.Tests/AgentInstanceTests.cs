using AgentCore.Application.Context;
using AgentCore.Application.Agents;
using AgentCore.Application.Identity;
using AgentCore.Application.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Memory;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class AgentInstanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 5, 40, 0, TimeSpan.Zero);
    private static readonly Guid LegacyLow = Guid.Parse("019944af-0008-7000-8000-0000000000c1");
    private static readonly Guid LegacyHigh = Guid.Parse("019944af-0008-7000-8000-0000000000c2");
    private static readonly Guid TieOlder = Guid.Parse("019944af-0008-7000-8000-0000000000d1");
    private static readonly Guid TieWinner = Guid.Parse("019944af-0008-7000-8000-0000000000d2");
    private static readonly Guid TieLoser = Guid.Parse("019944af-0008-7000-8000-0000000000d3");

    [Fact]
    public async Task List_chat_eligible_returns_active_managed_instances_only()
    {
        var sessions = new InMemoryMemoryStore();
        var instances = new InMemoryAgentInstanceStore();
        var clock = new FakeTimeProvider(Now);
        var definitions = new VersionedDefinitions(V1());
        var service = Service(instances, definitions, sessions, clock, 4);
        var active = await service.CreateAsync("examiner", 1);
        var toArchive = await service.CreateAsync("examiner", 1);
        var archived = await service.SetLifecycleAsync(
            toArchive.InstanceId,
            AgentInstanceLifecycle.Archived,
            toArchive.Revision);
        Assert.Equal(AgentInstanceLifecycle.Archived, archived.Lifecycle);


        var eligible = await service.ListChatEligibleAsync();
        Assert.Contains(eligible, item => item.InstanceId == active.InstanceId);
        Assert.DoesNotContain(eligible, item => item.InstanceId == toArchive.InstanceId);
        Assert.All(eligible, item => Assert.Equal(AgentInstanceLifecycle.Active, item.Lifecycle));
    }

    [Fact]
    public async Task Persona_update_bumps_persona_revision()
    {
        var sessions = new InMemoryMemoryStore();
        var instances = new InMemoryAgentInstanceStore();
        var clock = new FakeTimeProvider(Now);
        var definitions = new VersionedDefinitions(V1());
        var service = Service(instances, definitions, sessions, clock, 4);
        var managed = await service.CreateAsync("examiner", 1);
        var persona = managed.Persona with { Tone = "edited" };
        var updated = await service.UpdatePersonaAsync(
            managed.InstanceId,
            persona,
            managed.Revision,
            managed.PersonaRevision);
        Assert.Equal("edited", updated.Persona.Tone);
        Assert.Equal(2, updated.PersonaRevision);
        Assert.Equal(2, updated.Revision);
    }

    private static async Task ForEachStore(
        Func<IMemoryStore, IAgentInstanceStore, TimeProvider, Task> exercise)
    {
        var clock = new FakeTimeProvider(Now);
        await exercise(new InMemoryMemoryStore(), new InMemoryAgentInstanceStore(), clock);

        var path = Path.Combine(Path.GetTempPath(), $"agent-core-instance-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new SqliteFactory(options);
        var sessions = new SqliteMemoryStore(factory, clock);
        try
        {
            await sessions.EnsureCreatedAsync();
            await exercise(sessions, new SqliteAgentInstanceStore(factory, new SystemIdGenerator(clock)), clock);
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
        TimeProvider clock,
        int count) =>
        new(instances, definitions, Ids(count, "019944af-0017-7000-8000-"), clock);

    private static DeterministicIdGenerator Ids(int count, string prefix) =>
        new(
            Enumerable.Range(1, count).Select(index => Guid.Parse($"{prefix}{index:D12}")),
            Enumerable.Range(1, count).Select(index => Guid.Parse($"019944af-001a-7000-8000-{index:D12}")));

    private static AgentDefinition V1() => SampleDefinitions.Examiner;

    private static AgentDefinition V2() =>
        SampleDefinitions.Examiner with
        {
            Version = 2,
            AgentInstructions = SampleDefinitions.Examiner.AgentInstructions + "\nV2_MARKER",
            Identity = SampleDefinitions.Examiner.Identity with { Tone = "v2 tone" }
        };

    private static AgentDefinition V3() =>
        V2() with
        {
            Version = 3,
            AgentInstructions = V2().AgentInstructions + "\nV3_MARKER",
            Identity = V2().Identity with { Tone = "v3 tone" }
        };

    private static AgentDefinition SupportPersona(string tone) =>
        SampleDefinitions.Support with { Identity = SampleDefinitions.Support.Identity with { Tone = tone } };

    private sealed class VersionedDefinitions(params AgentDefinition[] definitions) : IAgentDefinitionStore
    {
        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AgentDefinition>>(definitions);

        public ValueTask<AgentDefinition?> GetAsync(
            string id,
            int? version = null,
            CancellationToken cancellationToken = default)
        {
            var matches = definitions.Where(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            var found = version is { } exact
                ? matches.FirstOrDefault(item => item.Version == exact)
                : matches.OrderByDescending(item => item.Version).FirstOrDefault();
            return ValueTask.FromResult(found);
        }
    }

    private sealed class SqliteFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
