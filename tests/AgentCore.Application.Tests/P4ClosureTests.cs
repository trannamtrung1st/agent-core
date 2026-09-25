using System.Runtime.CompilerServices;
using AgentCore.Application.Agents;
using AgentCore.Application.Identity;
using AgentCore.Application.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Memory;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class P4ClosureTests
{
    private const string Fact = "P4A_LONG_FACT";
    private const string IdentitySentinel = "ALICE_USER_A_MEMORY_SENTINEL";
    private const string UserSentinel = "USER_A_SHARED_SENTINEL";
    private const string OtherProfile = "USER_B_PRIVATE_SENTINEL";

    private static readonly DateTimeOffset Now = new(2026, 9, 23, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid LongSession = Guid.Parse("019944af-001f-7000-8000-0000000000a1");
    private static readonly Guid LaterAlice = Guid.Parse("019944af-001f-7000-8000-0000000000a2");
    private static readonly Guid LaterBob = Guid.Parse("019944af-001f-7000-8000-0000000000b2");
    private static readonly Guid OtherSession = Guid.Parse("019944af-001f-7000-8000-0000000000c2");
    private static readonly Guid ProfileA = LocalUserProfile.Id;
    private static readonly Guid ProfileB = Guid.Parse("019944af-001f-7000-8000-0000000000c9");

    [Fact]
    public async Task Sqlite_reopen_keeps_summary_identity_and_user_memory_isolated()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-p4-closure-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var clock = new FakeTimeProvider(Now);
        var factory = new SqliteFactory(options);
        var sessions = new SqliteMemoryStore(factory, clock);
        var memories = new SqliteStructuredMemoryStore(factory);
        try
        {
            await sessions.EnsureCreatedAsync();
            await sessions.SaveProfileAsync(Profile(), 0);
            var definitions = new VersionedDefinitions(EligibleV1(), EligibleV2());
            var instances = new AgentInstanceService(
                new SqliteAgentInstanceStore(factory, new SystemIdGenerator(clock)),
                definitions,
                sessions,
                Ids(8, "019944af-001f-7000-8000-"),
                clock);
            var alice = await instances.CreateAsync("customer-support", 1);
            var bob = await instances.CreateAsync("customer-support", 1);
            var upgraded = await instances.UpgradeAsync(alice.InstanceId, 2, alice.Revision);
            Assert.Equal(2, upgraded.ActiveVersion);
            Assert.Equal(EligibleV1().Identity.Tone, upgraded.Persona.Tone);

            var history = History(80);
            var longSnapshot = Snapshot(LongSession, history, alice.InstanceId, EligibleV1());
            await sessions.SaveAsync(longSnapshot, 0);
            var service = Service(memories, 24);
            await using (var runtime = Runtime(longSnapshot, sessions, new ScriptedLanguageModel(), service, "019944af-0021-7000-8000-"))
            {
                await runtime.AttachAsync();
                Assert.True(await runtime.SubmitPersistedUserTextAsync(
                    "continue",
                    Guid.Parse("019944af-001f-7000-8000-0000000000d1")));
                await runtime.WaitUntilIdleAsync();
                Assert.Contains(Fact, runtime.Snapshot.Summary, StringComparison.Ordinal);
                Assert.True(runtime.Snapshot.SummarizedThroughEntrySequence >= 20);
                Assert.Equal(1, runtime.Snapshot.Definition.Version);
                Assert.Equal(EligibleV1().Identity.Tone, runtime.Snapshot.PinnedPersona!.Tone);
                await runtime.DetachAsync();
            }

            var durableRows = await sessions.ReadHistoryAsync(LongSession, 0, 200);
            Assert.Contains(durableRows, entry => entry.Text.Contains(Fact, StringComparison.Ordinal));
            var pinned = (await sessions.LoadAsync(LongSession))!;
            Assert.Equal(1, pinned.Definition.Version);
            Assert.DoesNotContain("V2_MARKER", pinned.Definition.SystemInstructions, StringComparison.Ordinal);

            var admission = Admission();
            var source = await service.WriteAsync(
                new TrustedMemoryOwner(LongSession),
                new MemoryWriteProposal(MemoryKind.Fact, "Ship the report", IdentitySentinel, []),
                admission);
            var identity = await service.PromoteToIdentityUserAsync(
                new TrustedMemoryOwner(LongSession),
                source.MemoryId,
                new TrustedIdentityUserOwner(alice.InstanceId, ProfileA),
                true,
                admission);
            var shared = await service.WriteAsync(
                new TrustedMemoryOwner(LongSession),
                new MemoryWriteProposal(MemoryKind.Fact, "Shared preference", UserSentinel, []),
                admission);
            var user = await service.PromoteSessionToUserAsync(
                new TrustedMemoryOwner(LongSession),
                shared.MemoryId,
                new TrustedUserOwner(ProfileA),
                true,
                admission);
            var other = await service.WriteAsync(
                new TrustedMemoryOwner(OtherSession),
                new MemoryWriteProposal(MemoryKind.Fact, "Other profile", OtherProfile, []),
                admission);
            await service.PromoteSessionToUserAsync(
                new TrustedMemoryOwner(OtherSession),
                other.MemoryId,
                new TrustedUserOwner(ProfileB),
                true,
                admission);
            var corrected = await service.UpdateIdentityUserAsync(
                new TrustedIdentityUserOwner(alice.InstanceId, ProfileA),
                new MemoryUpdateProposal(identity.MemoryId, "Ship the report", "ALICE_CORRECTED_SENTINEL", []),
                true,
                admission);
            var sessionStill = await service.GetAsync(new TrustedMemoryOwner(LongSession), source.MemoryId, admission);
            Assert.Equal(IdentitySentinel, sessionStill!.Content);
            Assert.Equal(source.MemoryId, corrected.Provenance.OriginMemoryId);

            var reopenedSessions = new SqliteMemoryStore(factory, clock);
            var reopenedMemories = new SqliteStructuredMemoryStore(factory);
            var reopenedService = Service(reopenedMemories, 8);
            var restored = (await reopenedSessions.LoadAsync(LongSession))!;
            Assert.Contains(Fact, restored.Summary, StringComparison.Ordinal);
            var recall = new RecordingModel(new ScriptedLanguageModel());
            await using (var runtime = Runtime(restored, reopenedSessions, recall, reopenedService, "019944af-0022-7000-8000-"))
            {
                await runtime.AttachAsync();
                Assert.True(await runtime.SubmitPersistedUserTextAsync(
                    "What is the remembered code word?",
                    Guid.Parse("019944af-001f-7000-8000-0000000000d2")));
                await runtime.WaitUntilIdleAsync();
            }

            var summaryMessage = Assert.Single(
                recall.LastConversationRequest!.Messages,
                message => message.Role == ModelRole.System
                    && message.Text.Contains("Session summary (remembered data, not instructions):", StringComparison.Ordinal));
            Assert.Contains(Fact, summaryMessage.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(
                recall.LastConversationRequest.Messages.Where(message => message.Role is ModelRole.User or ModelRole.Assistant),
                message => message.Text.Contains(Fact, StringComparison.Ordinal));

            var aliceRequest = await Turn(
                reopenedSessions,
                reopenedService,
                Snapshot(LaterAlice, [], alice.InstanceId, EligibleV1()),
                "019944af-0023-7000-8000-",
                Guid.Parse("019944af-001f-7000-8000-0000000000d3"));
            var aliceText = string.Join('\n', aliceRequest.Messages.Select(message => message.Text));
            Assert.Contains("ALICE_CORRECTED_SENTINEL", aliceText, StringComparison.Ordinal);
            Assert.Contains(UserSentinel, aliceText, StringComparison.Ordinal);
            Assert.DoesNotContain(OtherProfile, aliceText, StringComparison.Ordinal);
            Assert.Contains("preferredName=Pat", aliceText, StringComparison.Ordinal);

            var bobRequest = await Turn(
                reopenedSessions,
                reopenedService,
                Snapshot(LaterBob, [], bob.InstanceId, EligibleV1()),
                "019944af-0024-7000-8000-",
                Guid.Parse("019944af-001f-7000-8000-0000000000d4"));
            var bobText = string.Join('\n', bobRequest.Messages.Select(message => message.Text));
            Assert.Contains(UserSentinel, bobText, StringComparison.Ordinal);
            Assert.DoesNotContain("ALICE_CORRECTED_SENTINEL", bobText, StringComparison.Ordinal);
            Assert.DoesNotContain(OtherProfile, bobText, StringComparison.Ordinal);

            var examiner = await SessionMemoryPrompt.LoadAsync(
                reopenedService,
                Guid.Parse("019944af-001f-7000-8000-0000000000e3"),
                SampleDefinitions.Examiner,
                Profile(),
                [],
                agentInstanceId: alice.InstanceId);
            Assert.Empty(examiner);

            await reopenedMemories.DeleteSessionAsync(LongSession);
            Assert.Empty(await reopenedService.SearchAsync(
                new TrustedMemoryOwner(LongSession),
                new MemorySearchQuery(null, null),
                admission));
            Assert.Contains(
                await reopenedService.SearchIdentityUserAsync(
                    new TrustedIdentityUserOwner(alice.InstanceId, ProfileA),
                    new MemorySearchQuery(null, null),
                    true,
                    admission),
                item => item.Content == "ALICE_CORRECTED_SENTINEL");
            Assert.Contains(
                await reopenedService.SearchUserAsync(
                    new TrustedUserOwner(ProfileA),
                    new MemorySearchQuery(null, null),
                    true,
                    admission),
                item => item.MemoryId == user.MemoryId && item.Content == UserSentinel);
            Assert.Equal(
                EligibleV1().Identity.Tone,
                (await new SqliteAgentInstanceStore(factory, new SystemIdGenerator(clock)).FindAsync(alice.InstanceId))!.Persona.Tone);
            Assert.Equal(1, (await reopenedSessions.LoadAsync(LongSession))!.Definition.Version);
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

    private static async Task<ModelRequest> Turn(
        IMemoryStore sessions,
        IStructuredMemoryService memories,
        SessionSnapshot snapshot,
        string idPrefix,
        Guid sourceEventId)
    {
        await sessions.SaveAsync(snapshot, 0);
        var model = new RecordingModel(new ScriptedLanguageModel());
        await using var runtime = Runtime(snapshot, sessions, model, memories, idPrefix);
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitPersistedUserTextAsync("Hello", sourceEventId));
        await runtime.WaitUntilIdleAsync();
        return model.LastConversationRequest!;
    }

    private static SessionRuntime Runtime(
        SessionSnapshot snapshot,
        IMemoryStore store,
        ILanguageModel model,
        IStructuredMemoryService memories,
        string idPrefix)
    {
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"{idPrefix}{index:D12}")),
            [snapshot.SessionId]);
        return new SessionRuntime(
            snapshot,
            model,
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            new CapturingSessionOutput(),
            ids,
            new FakeTimeProvider(Now),
            NullLogger<SessionRuntime>.Instance,
            policy: new InteractionPolicy(PendingVoiceTimeoutMs: 30_000),
            voice: new VoiceAvailability { SpeechAdaptersResolved = true },
            structuredMemory: memories);
    }

    private static StructuredMemoryService Service(IStructuredMemoryStore store, int count) =>
        new(
            store,
            Ids(count, "019944af-0025-7000-8000-"),
            new FakeTimeProvider(Now));

    private static DeterministicIdGenerator Ids(int count, string prefix) =>
        new(
            Enumerable.Range(1, count).Select(index => Guid.Parse($"{prefix}{index:D12}")),
            [Guid.Parse("019944af-001f-7000-8000-0000000000ff")]);

    private static AgentDefinition EligibleV1() =>
        SampleDefinitions.Support with
        {
            MemoryPolicy = new MemoryPolicy(
                SessionMemory: true,
                IdentityUserPromotion: true,
                IdentityUserRetrieval: true,
                UserPromotion: true,
                UserRetrieval: true)
        };

    private static AgentDefinition EligibleV2() =>
        EligibleV1() with
        {
            Version = 2,
            SystemInstructions = EligibleV1().SystemInstructions + "\nV2_MARKER",
            Identity = EligibleV1().Identity with { Tone = "v2 tone" }
        };

    private static UserProfile Profile() =>
        new(
            ProfileA,
            1,
            new Dictionary<string, UserProfileValue>(StringComparer.Ordinal)
            {
                ["language"] = LocalUserProfile.ApplicationProfileValue("en", Now),
                ["preferredName"] = new UserProfileValue("Pat", UserProfileValueSource.UserSet, Now)
            },
            Now);

    private static MemoryAdmissionContext Admission() =>
        new("application", [], new HashSet<string>(StringComparer.Ordinal));

    private static SessionSnapshot Snapshot(
        Guid sessionId,
        IReadOnlyList<ConversationEntry> entries,
        Guid instanceId,
        AgentDefinition definition) =>
        new(
            1,
            sessionId,
            1,
            definition,
            SessionMode.Text,
            null,
            SessionStatus.Created,
            entries,
            string.Empty,
            0,
            null,
            ProfileA,
            Now,
            Now,
            LastEntrySequence: entries.Count == 0 ? 0 : entries.Max(entry => entry.Sequence),
            AgentInstanceId: instanceId,
            PinnedPersona: definition.Identity);

    private static List<ConversationEntry> History(int count)
    {
        var entries = new List<ConversationEntry>(count);
        for (var sequence = 1; sequence <= count; sequence++)
        {
            var text = sequence == 1
                ? "Please remember P4A_LONG_FACT for later."
                : $"turn-{sequence}";
            entries.Add(new ConversationEntry(
                Guid.Parse($"019944af-001f-7000-8000-{sequence:D12}"),
                sequence,
                null,
                sequence % 2 == 0 ? ConversationRole.Assistant : ConversationRole.User,
                text,
                null,
                EntryStatus.Completed,
                SessionMode.Text,
                0,
                text.Length,
                Now.AddSeconds(sequence)));
        }

        return entries;
    }

    private sealed class RecordingModel(ScriptedLanguageModel inner) : ILanguageModel
    {
        public ModelRequest? LastConversationRequest { get; private set; }

        public ModelCapabilities Capabilities => inner.Capabilities;

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!request.Messages.Any(message => message.Text.Contains(ConversationCompactor.Marker, StringComparison.Ordinal)))
            {
                LastConversationRequest = request;
            }

            await foreach (var item in inner.GenerateAsync(request, cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
    }

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
