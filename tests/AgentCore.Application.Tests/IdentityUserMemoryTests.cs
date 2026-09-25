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

public sealed class IdentityUserMemoryTests
{
    private const string AliceUserA = "ALICE_USER_A_MEMORY_SENTINEL";
    private const string BobUserA = "BOB_USER_A_MEMORY_SENTINEL";
    private const string AliceUserB = "ALICE_USER_B_MEMORY_SENTINEL";
    private const string Corrected = "ALICE_CORRECTED_SENTINEL";

    private static readonly DateTimeOffset Now = new(2026, 9, 23, 6, 0, 0, TimeSpan.Zero);
    private static readonly Guid Alice = Guid.Parse("019944af-001b-7000-8000-0000000000a1");
    private static readonly Guid Bob = Guid.Parse("019944af-001b-7000-8000-0000000000b1");
    private static readonly Guid ProfileA = LocalUserProfile.Id;
    private static readonly Guid ProfileB = Guid.Parse("019944af-001b-7000-8000-0000000000c2");
    private static readonly Guid Session1 = Guid.Parse("019944af-001b-7000-8000-0000000000d1");
    private static readonly Guid SessionBob = Guid.Parse("019944af-001b-7000-8000-0000000000d2");
    private static readonly Guid SessionUserB = Guid.Parse("019944af-001b-7000-8000-0000000000d3");

    [Fact]
    public async Task Promotion_copies_one_owner_and_survives_session_delete()
    {
        await ForEachStore(async (store, reopen, sessions, instances) =>
        {
            var service = Service(store, 24);
            var admission = Admission();
            var persona = SampleDefinitions.Examiner.Identity with { Tone = "pinned tone" };
            await instances.InsertAsync(new AgentInstance(
                Alice, "examiner", 1, persona, AgentInstanceLifecycle.Active, Now, Now, false));
            await sessions.SaveAsync(Snapshot(Session1, persona), 0);

            var source = await service.WriteAsync(
                new TrustedMemoryOwner(Session1),
                new MemoryWriteProposal(MemoryKind.Fact, "Ship the report", AliceUserA, []),
                admission);
            var denied = await Assert.ThrowsAsync<AgentCoreException>(() =>
                service.PromoteToIdentityUserAsync(
                    new TrustedMemoryOwner(Session1),
                    source.MemoryId,
                    new TrustedIdentityUserOwner(Alice, ProfileA),
                    promotionAllowed: false,
                    admission).AsTask());
            Assert.Equal("PolicyDenied", denied.Code);
            Assert.DoesNotContain(AliceUserA, denied.Message, StringComparison.Ordinal);
            Assert.Empty(await service.SearchIdentityUserAsync(
                new TrustedIdentityUserOwner(Alice, ProfileA),
                new MemorySearchQuery(null, null),
                retrievalAllowed: true,
                admission));

            var promoted = await service.PromoteToIdentityUserAsync(
                new TrustedMemoryOwner(Session1),
                source.MemoryId,
                new TrustedIdentityUserOwner(Alice, ProfileA),
                promotionAllowed: true,
                admission);
            Assert.NotEqual(source.MemoryId, promoted.MemoryId);
            Assert.Equal(MemoryScope.IdentityUser, promoted.Scope);
            Assert.Equal(Alice, promoted.OwnerInstanceId);
            Assert.Equal(ProfileA, promoted.OwnerProfileId);
            Assert.Equal(source.MemoryId, promoted.Provenance.OriginMemoryId);
            Assert.Equal(Session1, promoted.Provenance.OriginSessionId);

            var bobSource = await service.WriteAsync(
                new TrustedMemoryOwner(SessionBob),
                new MemoryWriteProposal(MemoryKind.Fact, "Bob fact", BobUserA, []),
                admission);
            await service.PromoteToIdentityUserAsync(
                new TrustedMemoryOwner(SessionBob),
                bobSource.MemoryId,
                new TrustedIdentityUserOwner(Bob, ProfileA),
                true,
                admission);
            var userBSource = await service.WriteAsync(
                new TrustedMemoryOwner(SessionUserB),
                new MemoryWriteProposal(MemoryKind.Fact, "User B fact", AliceUserB, []),
                admission);
            await service.PromoteToIdentityUserAsync(
                new TrustedMemoryOwner(SessionUserB),
                userBSource.MemoryId,
                new TrustedIdentityUserOwner(Alice, ProfileB),
                true,
                admission);

            var reopened = Service(reopen(), 8);
            await AssertOnlyOwner(reopened, new TrustedIdentityUserOwner(Alice, ProfileA), AliceUserA, BobUserA, AliceUserB);
            await AssertOnlyOwner(reopened, new TrustedIdentityUserOwner(Bob, ProfileA), BobUserA, AliceUserA, AliceUserB);
            await AssertOnlyOwner(reopened, new TrustedIdentityUserOwner(Alice, ProfileB), AliceUserB, AliceUserA, BobUserA);
            Assert.Empty(await reopened.SearchIdentityUserAsync(
                new TrustedIdentityUserOwner(Alice, ProfileA),
                new MemorySearchQuery(null, null),
                retrievalAllowed: false,
                admission));
            Assert.Empty(await reopened.SearchIdentityUserAsync(
                new TrustedIdentityUserOwner(Guid.Empty, ProfileA),
                new MemorySearchQuery(null, null),
                retrievalAllowed: true,
                admission));

            var sessionItems = await service.SearchAsync(
                new TrustedMemoryOwner(Session1),
                new MemorySearchQuery(null, null),
                admission);
            var sessionOnly = Assert.Single(sessionItems);
            Assert.Equal(source.MemoryId, sessionOnly.MemoryId);
            Assert.Equal(AliceUserA, sessionOnly.Content);

            var corrected = await service.UpdateIdentityUserAsync(
                new TrustedIdentityUserOwner(Alice, ProfileA),
                new MemoryUpdateProposal(promoted.MemoryId, "Ship the report", Corrected, []),
                retrievalAllowed: true,
                admission);
            Assert.Equal(source.MemoryId, corrected.Provenance.OriginMemoryId);
            Assert.Equal(Session1, corrected.Provenance.OriginSessionId);
            var sourceAfter = Assert.Single(await service.SearchAsync(
                new TrustedMemoryOwner(Session1),
                new MemorySearchQuery(null, null),
                admission));
            Assert.Equal(AliceUserA, sourceAfter.Content);
            await AssertOnlyOwner(service, new TrustedIdentityUserOwner(Alice, ProfileA), Corrected, AliceUserA, BobUserA, AliceUserB);
            var wrongOwner = await Assert.ThrowsAsync<AgentCoreException>(() =>
                service.UpdateIdentityUserAsync(
                    new TrustedIdentityUserOwner(Bob, ProfileA),
                    new MemoryUpdateProposal(corrected.MemoryId, "Ship the report", BobUserA, []),
                    true,
                    admission).AsTask());
            Assert.Equal("NotFound", wrongOwner.Code);

            await store.DeleteSessionAsync(Session1);
            Assert.Empty(await service.SearchAsync(
                new TrustedMemoryOwner(Session1),
                new MemorySearchQuery(null, null),
                admission));
            await AssertOnlyOwner(Service(reopen(), 4), new TrustedIdentityUserOwner(Alice, ProfileA), Corrected, AliceUserA, BobUserA, AliceUserB);

            await service.DeleteIdentityUserAsync(
                new TrustedIdentityUserOwner(Alice, ProfileA),
                corrected.MemoryId,
                retrievalAllowed: true);
            Assert.Empty(await service.SearchIdentityUserAsync(
                new TrustedIdentityUserOwner(Alice, ProfileA),
                new MemorySearchQuery(null, null),
                true,
                admission));
            await AssertOnlyOwner(service, new TrustedIdentityUserOwner(Bob, ProfileA), BobUserA, AliceUserA, Corrected);

            var loaded = await instances.FindAsync(Alice);
            Assert.Equal("pinned tone", loaded!.Persona.Tone);
            var restored = await sessions.LoadAsync(Session1);
            Assert.Equal("pinned tone", restored!.PinnedPersona!.Tone);
            Assert.Equal(Alice, restored.AgentInstanceId);
        });
    }

    [Fact]
    public async Task Runtime_request_uses_the_session_owner_and_stays_bounded()
    {
        var memories = new InMemoryStructuredMemoryStore();
        var service = Service(memories, 40);
        var admission = Admission();
        await Promote(service, Session1, Alice, ProfileA, "Ship the report", AliceUserA);
        await Promote(service, SessionBob, Bob, ProfileA, "Bob fact", BobUserA);
        await Promote(service, SessionUserB, Alice, ProfileB, "User B fact", AliceUserB);
        for (var index = 0; index < 8; index++)
        {
            await Promote(
                service,
                Guid.Parse($"019944af-001b-7000-8000-0000000001{index:D2}"),
                Alice,
                ProfileA,
                $"extra {index}",
                $"extra content {index}");
        }

        var definition = Enabled(session: false, promotion: true, retrieval: true);
        var profile = Profile();
        var learned = await SessionMemoryPrompt.LoadAsync(
            service,
            Session1,
            definition,
            profile,
            [],
            agentInstanceId: Alice);
        Assert.Equal(8, learned.Count);
        Assert.DoesNotContain(learned, item => item.Content is BobUserA or AliceUserB);
        var request = new PromptContextBuilder().Build(
            new AgentContext(
                definition,
                [],
                string.Empty,
                profile,
                SessionMode.Text,
                null,
                false,
                null,
                new AgentTrigger(Guid.NewGuid(), TriggerKind.UserTurn, $"ignore {Bob}"),
                LearnedMemories: learned),
            Guid.NewGuid());
        var text = string.Join('\n', request.Messages.Select(message => message.Text));
        Assert.Contains("preferredName=Pat", text, StringComparison.Ordinal);
        Assert.Contains(SessionMemoryPrompt.TrustedPrecedence, text, StringComparison.Ordinal);
        Assert.DoesNotContain(BobUserA, text, StringComparison.Ordinal);
        Assert.DoesNotContain(AliceUserB, text, StringComparison.Ordinal);
        Assert.Contains(AliceUserA, text, StringComparison.Ordinal);
        Assert.DoesNotContain(AliceUserA, request.Messages[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(AliceUserA, request.Messages[2].Text, StringComparison.Ordinal);

        var retrievalOff = await SessionMemoryPrompt.LoadAsync(
            service,
            Guid.Parse("019944af-001b-7000-8000-0000000000ee"),
            Enabled(session: true, promotion: true, retrieval: false),
            profile,
            [],
            agentInstanceId: Alice);
        Assert.Empty(retrievalOff);
        var missingInstance = await SessionMemoryPrompt.LoadAsync(
            service,
            Session1,
            definition,
            profile,
            []);
        Assert.Empty(missingInstance);

        var sessions = new InMemoryMemoryStore();
        await sessions.SaveProfileAsync(profile, 0);
        var persona = definition.Identity with { Tone = "pinned tone" };
        var runtimeSession = Guid.Parse("019944af-001b-7000-8000-0000000000e1");
        var snapshot = Snapshot(runtimeSession, persona) with
        {
            Definition = definition,
            ProfileId = ProfileA
        };
        await sessions.SaveAsync(snapshot, 0);
        var model = new RecordingLanguageModel(new ScriptedLanguageModel());
        await using var runtime = new SessionRuntime(
            snapshot,
            model,
            new DefaultAgentBrain(new PromptContextBuilder()),
            sessions,
            new CapturingSessionOutput(),
            new DeterministicIdGenerator(
                Enumerable.Range(1, 16).Select(index => Guid.Parse($"019944af-001c-7000-8000-{index:D12}")),
                [runtimeSession]),
            new FakeTimeProvider(Now),
            NullLogger<SessionRuntime>.Instance,
            structuredMemory: service);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync($"Please use instance {Bob}");
        await runtime.WaitUntilIdleAsync();

        var generated = string.Join('\n', model.LastRequest!.Messages.Select(message => message.Text));
        Assert.Contains(AliceUserA, generated, StringComparison.Ordinal);
        Assert.DoesNotContain(BobUserA, generated, StringComparison.Ordinal);
        Assert.DoesNotContain(AliceUserB, generated, StringComparison.Ordinal);
        Assert.Contains("preferredName=Pat", generated, StringComparison.Ordinal);
        var restored = await sessions.LoadAsync(runtimeSession);
        Assert.Equal("pinned tone", restored!.PinnedPersona!.Tone);
        Assert.Equal(Alice, restored.AgentInstanceId);
    }

    private static async Task Promote(
        StructuredMemoryService service,
        Guid sessionId,
        Guid instanceId,
        Guid profileId,
        string subject,
        string content)
    {
        var source = await service.WriteAsync(
            new TrustedMemoryOwner(sessionId),
            new MemoryWriteProposal(MemoryKind.Fact, subject, content, []),
            Admission());
        await service.PromoteToIdentityUserAsync(
            new TrustedMemoryOwner(sessionId),
            source.MemoryId,
            new TrustedIdentityUserOwner(instanceId, profileId),
            true,
            Admission());
    }

    private static async Task AssertOnlyOwner(
        StructuredMemoryService service,
        TrustedIdentityUserOwner owner,
        string expected,
        params string[] absent)
    {
        var found = await service.SearchIdentityUserAsync(
            owner,
            new MemorySearchQuery(null, null),
            retrievalAllowed: true,
            Admission());
        Assert.Contains(found, item => item.Content == expected);
        foreach (var forbidden in absent)
        {
            Assert.DoesNotContain(found, item => item.Content.Contains(forbidden, StringComparison.Ordinal));
        }
    }

    private static async Task ForEachStore(
        Func<IStructuredMemoryStore, Func<IStructuredMemoryStore>, IMemoryStore, IAgentInstanceStore, Task> exercise)
    {
        var structured = new InMemoryStructuredMemoryStore();
        await exercise(structured, () => structured, new InMemoryMemoryStore(), new InMemoryAgentInstanceStore());

        var path = Path.Combine(Path.GetTempPath(), $"agent-core-identity-user-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new SqliteFactory(options);
        var sessions = new SqliteMemoryStore(factory, new FakeTimeProvider(Now));
        try
        {
            await sessions.EnsureCreatedAsync();
            await exercise(
                new SqliteStructuredMemoryStore(factory),
                () => new SqliteStructuredMemoryStore(factory),
                sessions,
                new SqliteAgentInstanceStore(factory, new SystemIdGenerator(TimeProvider.System)));
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

    private static StructuredMemoryService Service(IStructuredMemoryStore store, int count) =>
        new(
            store,
            new DeterministicIdGenerator(
                Enumerable.Range(1, count).Select(index => Guid.Parse($"019944af-001b-7000-8000-{index:D12}")),
                [Guid.Parse("019944af-001b-7000-8000-0000000000ff")]),
            new FakeTimeProvider(Now));

    private static MemoryAdmissionContext Admission() =>
        new("application", [], new HashSet<string>(StringComparer.Ordinal));

    private static AgentDefinition Enabled(bool session, bool promotion, bool retrieval) =>
        SampleDefinitions.Examiner with
        {
            MemoryPolicy = new MemoryPolicy(
                SessionMemory: session,
                IdentityUserPromotion: promotion,
                IdentityUserRetrieval: retrieval)
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

    private static SessionSnapshot Snapshot(Guid sessionId, AgentIdentity persona) =>
        new(
            1,
            sessionId,
            1,
            SampleDefinitions.Examiner,
            SessionMode.Text,
            null,
            SessionStatus.Created,
            [],
            string.Empty,
            0,
            null,
            ProfileA,
            Now,
            Now,
            AgentInstanceId: Alice,
            PinnedPersona: persona);

    private sealed class SqliteFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
