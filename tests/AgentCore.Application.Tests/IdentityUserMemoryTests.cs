using AgentCore.Application.Context;
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
                Alice, "examiner", 1, persona, AgentInstanceLifecycle.Active, Now, Now));
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
