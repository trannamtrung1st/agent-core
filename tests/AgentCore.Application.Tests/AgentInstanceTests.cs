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
        Assert.NotEqual(alice.InstanceId, AgentInstance.CompatibilityFor("examiner"));
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

        var compatibilitySession = await manager.CreateAsync("examiner", 1, SessionMode.Text);
        var compatibility = await service.RequireAsync(compatibilitySession.AgentInstanceId!.Value);
        Assert.True(compatibility.Compatibility);
        Assert.Equal(AgentInstance.CompatibilityFor("examiner"), compatibility.InstanceId);
        Assert.Equal(1, compatibility.ActiveVersion);
        var newer = await manager.CreateAsync("examiner", 2, SessionMode.Text);
        Assert.Equal(2, newer.Definition.Version);
        Assert.Equal("v2 tone", newer.PinnedPersona!.Tone);
        var upgradedCompatibility = await service.RequireAsync(compatibility.InstanceId);
        Assert.Equal(2, upgradedCompatibility.ActiveVersion);
        Assert.Equal(compatibility.Persona, upgradedCompatibility.Persona);
        var fromInstance = await manager.CreateForInstanceAsync(compatibility.InstanceId, SessionMode.Text);
        Assert.Equal(2, fromInstance.Definition.Version);
        Assert.Equal(compatibility.Persona, fromInstance.PinnedPersona);
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
        await using var runtime = new SessionRuntime(
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
    public async Task Concurrent_compatibility_forward_align_succeeds()
    {
        await ForEachStore(async (sessions, instances, clock) =>
        {
            var definitions = new VersionedDefinitions(V1(), V2());
            var service = Service(instances, definitions, sessions, clock, 4);
            var now = clock.GetUtcNow();
            await instances.InsertAsync(
                new AgentInstance(
                    AgentInstance.CompatibilityFor("examiner"),
                    "examiner",
                    1,
                    V1().Identity,
                    AgentInstanceLifecycle.Active,
                    now,
                    now,
                    Compatibility: true));

            var definition = V2();
            var first = service.ResolveCompatibilityAsync(definition).AsTask();
            var second = service.ResolveCompatibilityAsync(definition).AsTask();
            var results = await Task.WhenAll(first, second);
            Assert.All(results, item => Assert.Equal(2, item.ActiveVersion));

            var stored = await instances.FindCompatibilityAsync("examiner");
            Assert.NotNull(stored);
            Assert.Equal(2, stored.ActiveVersion);
        });
    }

    [Fact]
    public async Task Concurrent_compatibility_forward_align_mixed_targets_reaches_highest_version()
    {
        await ForEachStore(async (sessions, instances, clock) =>
        {
            var definitions = new VersionedDefinitions(V1(), V2(), V3());
            var service = Service(instances, definitions, sessions, clock, 4);
            var now = clock.GetUtcNow();
            await instances.InsertAsync(
                new AgentInstance(
                    AgentInstance.CompatibilityFor("examiner"),
                    "examiner",
                    1,
                    V1().Identity,
                    AgentInstanceLifecycle.Active,
                    now,
                    now,
                    Compatibility: true));

            var v2 = V2();
            var v3 = V3();
            var toV2 = service.ResolveCompatibilityAsync(v2).AsTask();
            var toV3 = service.ResolveCompatibilityAsync(v3).AsTask();
            var results = await Task.WhenAll(toV2, toV3);
            Assert.All(results, item => Assert.True(item.ActiveVersion >= 2));

            var stored = await instances.FindCompatibilityAsync("examiner");
            Assert.NotNull(stored);
            Assert.Equal(3, stored.ActiveVersion);
        });
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

    [Fact]
    public async Task Backfill_assigns_one_compatibility_instance_without_rewriting_history()
    {
        await ForEachStore(async (sessions, instances, clock) =>
        {
            var older = Now;
            var tied = Now.AddMinutes(5);
            var newerV1 = Now.AddHours(1);
            var stale = SupportPersona("old tone");
            var winner = SupportPersona("winner tone");
            var loser = SupportPersona("loser tone");
            var definitions = new VersionedDefinitions(V1(), V2(), stale, winner, loser);
            var low = Legacy(LegacyLow, V1(), "legacy line", newerV1);
            var high = Legacy(LegacyHigh, V2(), "other legacy line", older);
            await sessions.SaveAsync(low, 0);
            await sessions.SaveAsync(high, 0);
            await sessions.SaveAsync(Legacy(TieOlder, stale, "old support", older), 0);
            await sessions.SaveAsync(Legacy(TieWinner, winner, "winning support", tied), 0);
            await sessions.SaveAsync(Legacy(TieLoser, loser, "losing support", tied), 0);
            var service = Service(instances, definitions, sessions, clock, 4);
            await service.BackfillAsync();
            await service.BackfillAsync();

            var restoredLow = (await sessions.LoadAsync(LegacyLow))!;
            var restoredHigh = (await sessions.LoadAsync(LegacyHigh))!;
            var expected = AgentInstance.CompatibilityFor("examiner");
            Assert.Equal(expected, restoredLow.AgentInstanceId);
            Assert.Equal(expected, restoredHigh.AgentInstanceId);
            Assert.Equal(1, restoredLow.Revision);
            Assert.Equal(1, restoredHigh.Revision);
            Assert.Equal(1, restoredLow.Definition.Version);
            Assert.Equal(2, restoredHigh.Definition.Version);
            Assert.DoesNotContain("V2_MARKER", restoredLow.Definition.SystemInstructions, StringComparison.Ordinal);
            Assert.Contains("V2_MARKER", restoredHigh.Definition.SystemInstructions, StringComparison.Ordinal);
            Assert.Equal("legacy line", Assert.Single(restoredLow.Entries).Text);
            Assert.Equal("other legacy line", Assert.Single(restoredHigh.Entries).Text);
            Assert.Equal(V1().Identity, restoredLow.PinnedPersona);
            Assert.Equal(V2().Identity, restoredHigh.PinnedPersona);
            var instance = await service.RequireAsync(expected);
            Assert.True(instance.Compatibility);
            Assert.Equal(2, instance.ActiveVersion);
            Assert.Equal(V2().Identity, instance.Persona);

            var supportId = AgentInstance.CompatibilityFor("customer-support");
            var support = await service.RequireAsync(supportId);
            Assert.Equal(1, support.ActiveVersion);
            Assert.Equal("winner tone", support.Persona.Tone);
            var restoredWinner = (await sessions.LoadAsync(TieWinner))!;
            var restoredLoser = (await sessions.LoadAsync(TieLoser))!;
            var restoredOlder = (await sessions.LoadAsync(TieOlder))!;
            Assert.Equal(supportId, restoredWinner.AgentInstanceId);
            Assert.Equal(supportId, restoredLoser.AgentInstanceId);
            Assert.Equal(supportId, restoredOlder.AgentInstanceId);
            Assert.Equal("winner tone", restoredWinner.PinnedPersona!.Tone);
            Assert.Equal("loser tone", restoredLoser.PinnedPersona!.Tone);
            Assert.Equal("old tone", restoredOlder.PinnedPersona!.Tone);
            Assert.Equal("winning support", Assert.Single(restoredWinner.Entries).Text);
        });
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
            await exercise(sessions, new SqliteAgentInstanceStore(factory), clock);
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
        new(instances, definitions, sessions, Ids(count, "019944af-0017-7000-8000-"), clock);

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

    private static SessionSnapshot Legacy(
        Guid sessionId,
        AgentDefinition definition,
        string text,
        DateTimeOffset? updatedAt = null) =>
        new(
            1,
            sessionId,
            1,
            definition,
            SessionMode.Text,
            null,
            SessionStatus.Created,
            [
                new ConversationEntry(
                    Guid.Parse($"019944af-001b-7000-8000-{sessionId.ToString("N")[^12..]}"),
                    1,
                    null,
                    ConversationRole.User,
                    text,
                    null,
                    EntryStatus.Completed,
                    SessionMode.Text,
                    0,
                    text.Length,
                    Now)
            ],
            string.Empty,
            0,
            null,
            null,
            Now,
            updatedAt ?? Now);

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
