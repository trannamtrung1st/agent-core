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
    public async Task Two_instances_keep_history_when_one_definition_moves_forward()
    {
        var sessions = new InMemoryMemoryStore();
        var instances = new InMemoryAgentInstanceStore();
        var memories = new InMemoryStructuredMemoryStore();
        var clock = new FakeTimeProvider(Now);
        var definitions = new VersionedDefinitions(V1(), V2());
        var service = Service(instances, definitions, sessions, clock, 8);
        var alice = await service.CreateAsync("examiner", 1);
        var bob = await service.CreateAsync("examiner", 1);
        var fresh = await service.CreateAsync("examiner", 1);
        Assert.NotEqual(alice.InstanceId, bob.InstanceId);
        Assert.NotEqual(alice.InstanceId, fresh.InstanceId);
        Assert.Equal(alice.Persona, fresh.Persona);
        Assert.Equal(1, alice.ActiveVersion);

        var manager = Manager(definitions, sessions, service, clock, memories);
        var first = await manager.CreateForInstanceAsync(alice.InstanceId, SessionMode.Text);
        Assert.Equal(alice.InstanceId, first.AgentInstanceId);
        Assert.Equal(1, first.Definition.Version);
        Assert.Equal(alice.Persona, first.PinnedPersona);
        Assert.DoesNotContain("V2_MARKER", first.Definition.SystemInstructions, StringComparison.Ordinal);
        var memory = new StructuredMemoryService(memories, Ids(4, "019944af-0018-7000-8000-"), clock);
        var admission = new MemoryAdmissionContext("application", [], new HashSet<string>(StringComparer.Ordinal));
        await memory.WriteAsync(
            new TrustedMemoryOwner(first.SessionId),
            new MemoryWriteProposal(MemoryKind.Fact, "Ship the report", "by Monday", []),
            admission);

        var upgraded = await service.UpgradeAsync(alice.InstanceId, 2, alice.Revision);
        Assert.Equal(alice.InstanceId, upgraded.InstanceId);
        Assert.Equal(2, upgraded.ActiveVersion);
        Assert.Equal(alice.Persona, upgraded.Persona);
        var historical = (await sessions.LoadAsync(first.SessionId))!;
        Assert.Equal(1, historical.Definition.Version);
        Assert.Equal(alice.InstanceId, historical.AgentInstanceId);
        Assert.Equal(alice.Persona, historical.PinnedPersona);
        Assert.DoesNotContain("V2_MARKER", historical.Definition.SystemInstructions, StringComparison.Ordinal);
        Assert.Equal("by Monday", Assert.Single(await memory.SearchAsync(
            new TrustedMemoryOwner(first.SessionId),
            new MemorySearchQuery(null, null),
            admission)).Content);

        var later = await manager.CreateForInstanceAsync(alice.InstanceId, SessionMode.Text);
        Assert.Equal(alice.InstanceId, later.AgentInstanceId);
        Assert.Equal(2, later.Definition.Version);
        Assert.Contains("V2_MARKER", later.Definition.SystemInstructions, StringComparison.Ordinal);
        Assert.Equal("v2 tone", later.Definition.Identity.Tone);
        Assert.Equal(alice.Persona, later.PinnedPersona);
        Assert.NotEqual(first.SessionId, later.SessionId);


    }

    [Fact]
    public async Task Upgraded_definition_keeps_pinned_persona_in_conversation_prompt()
    {
        var sessions = new InMemoryMemoryStore();
        var instances = new InMemoryAgentInstanceStore();
        var clock = new FakeTimeProvider(Now);
        var definitions = new VersionedDefinitions(V1(), V2());
        var service = Service(instances, definitions, sessions, clock, 8);
        var alice = await service.CreateAsync("examiner", 1);
        await service.UpgradeAsync(alice.InstanceId, 2, alice.Revision);
        var manager = Manager(definitions, sessions, service, clock, new InMemoryStructuredMemoryStore());
        var session = await manager.CreateForInstanceAsync(alice.InstanceId, SessionMode.Text);
        var model = new RecordingLanguageModel(new ScriptedLanguageModel());
        await using var runtime = SessionRuntimeFixture.Create(
            session,
            model,
            new DefaultAgentBrain(new PromptContextBuilder()),
            sessions,
            new CapturingSessionOutput(),
            Ids(16, "019944af-0019-7000-8000-"),
            clock,
            NullLogger<SessionRuntime>.Instance);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("hello");
        await runtime.WaitUntilIdleAsync();

        var identity = model.LastRequest!.Messages[0].Text;
        Assert.Contains($"Tone: {alice.Persona.Tone}", identity, StringComparison.Ordinal);
        Assert.DoesNotContain("Tone: v2 tone", identity, StringComparison.Ordinal);
        Assert.Contains("V2_MARKER", identity, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_and_empty_instance_owners_cannot_create_sessions()
    {
        var sessions = new InMemoryMemoryStore();
        var instances = new InMemoryAgentInstanceStore();
        var clock = new FakeTimeProvider(Now);
        var definitions = new VersionedDefinitions(V1());
        var service = Service(instances, definitions, sessions, clock, 4);
        var manager = Manager(definitions, sessions, service, clock, new InMemoryStructuredMemoryStore());
        await Assert.ThrowsAsync<AgentCoreException>(() => manager.CreateForInstanceAsync(Guid.Empty, SessionMode.Text));
        await Assert.ThrowsAsync<AgentCoreException>(() => manager.CreateForInstanceAsync(Guid.NewGuid(), SessionMode.Text));
        Assert.Empty((await sessions.ListCatalogAsync(null, 50, false)).Items);
    }

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
    public async Task Archived_managed_instance_rejects_new_session()
    {
        var sessions = new InMemoryMemoryStore();
        var instances = new InMemoryAgentInstanceStore();
        var clock = new FakeTimeProvider(Now);
        var definitions = new VersionedDefinitions(V1());
        var service = Service(instances, definitions, sessions, clock, 4);
        var managed = await service.CreateAsync("examiner", 1);
        var archived = await service.SetLifecycleAsync(
            managed.InstanceId,
            AgentInstanceLifecycle.Archived,
            managed.Revision);
        Assert.Equal(AgentInstanceLifecycle.Archived, archived.Lifecycle);

        var manager = Manager(definitions, sessions, service, clock, new InMemoryStructuredMemoryStore());
        var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
            manager.CreateForInstanceAsync(managed.InstanceId, SessionMode.Text));
        Assert.Equal("ValidationError", error.Code);
    }

    [Fact]
    public async Task Managed_session_pins_persona_revision_across_later_edits()
    {
        await ForEachStore(async (sessions, instances, clock) =>
        {
            var definitions = new VersionedDefinitions(V1());
            var service = Service(instances, definitions, sessions, clock, 4);
            var managed = await service.CreateAsync("examiner", 1);
            var manager = Manager(definitions, sessions, service, clock, new InMemoryStructuredMemoryStore());
            var first = await manager.CreateForInstanceAsync(managed.InstanceId, SessionMode.Text);
            Assert.Equal(1, first.PinnedPersonaRevision);
            Assert.Equal(managed.Persona, first.PinnedPersona);

            var persona = managed.Persona with { Tone = "edited" };
            _ = await service.UpdatePersonaAsync(
                managed.InstanceId,
                persona,
                managed.Revision,
                managed.PersonaRevision);

            var historical = (await sessions.LoadAsync(first.SessionId))!;
            Assert.Equal(1, historical.PinnedPersonaRevision);
            Assert.Equal(managed.Persona.Tone, historical.PinnedPersona!.Tone);

            var second = await manager.CreateForInstanceAsync(managed.InstanceId, SessionMode.Text);
            Assert.Equal(2, second.PinnedPersonaRevision);
            Assert.Equal("edited", second.PinnedPersona!.Tone);
        });
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

    private static SessionManager Manager(
        IAgentDefinitionStore definitions,
        IMemoryStore sessions,
        IAgentInstanceService instances,
        TimeProvider clock,
        IStructuredMemoryStore memories) =>
        new(
            definitions,
            sessions,
            Ids(16, "019944af-0019-7000-8000-"),
            clock,
            new VoiceAvailability { SpeechAdaptersResolved = true },
            structuredMemory: memories,
            instances: instances);

    private static DeterministicIdGenerator Ids(int count, string prefix) =>
        new(
            Enumerable.Range(1, count).Select(index => Guid.Parse($"{prefix}{index:D12}")),
            Enumerable.Range(1, count).Select(index => Guid.Parse($"019944af-001a-7000-8000-{index:D12}")));

    private static AgentDefinition V1() => SampleDefinitions.Examiner;

    private static AgentDefinition V2() =>
        SampleDefinitions.Examiner with
        {
            Version = 2,
            SystemInstructions = SampleDefinitions.Examiner.SystemInstructions + "\nV2_MARKER",
            Identity = SampleDefinitions.Examiner.Identity with { Tone = "v2 tone" }
        };

    private static AgentDefinition V3() =>
        V2() with
        {
            Version = 3,
            SystemInstructions = V2().SystemInstructions + "\nV3_MARKER",
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
