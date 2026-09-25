using AgentCore.Application.Agents;
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

public sealed class UserMemoryTests
{
    private const string Shared = "USER_A_SHARED_SENTINEL";
    private const string PrivateB = "USER_B_PRIVATE_SENTINEL";
    private const string IdentitySource = "IDENTITY_SOURCE_SENTINEL";
    private const string Corrected = "USER_A_CORRECTED_SENTINEL";

    private static readonly DateTimeOffset Now = new(2026, 9, 23, 7, 0, 0, TimeSpan.Zero);
    private static readonly Guid Alice = Guid.Parse("019944af-001d-7000-8000-0000000000a1");
    private static readonly Guid Bob = Guid.Parse("019944af-001d-7000-8000-0000000000b1");
    private static readonly Guid ProfileA = LocalUserProfile.Id;
    private static readonly Guid ProfileB = Guid.Parse("019944af-001d-7000-8000-0000000000c2");
    private static readonly Guid SessionA = Guid.Parse("019944af-001d-7000-8000-0000000000d1");
    private static readonly Guid SessionB = Guid.Parse("019944af-001d-7000-8000-0000000000d2");

    [Fact]
    public async Task User_copy_crosses_eligible_instances_and_leaves_sources()
    {
        await ForEachStore(async (store, reopen, sessions, instances) =>
        {
            var service = Service(store, 32);
            var admission = Admission();
            var persona = SampleDefinitions.Support.Identity with { Tone = "pinned tone" };
            await instances.InsertAsync(new AgentInstance(
                Alice, "customer-support", 1, persona, AgentInstanceLifecycle.Active, Now, Now, false));
            await sessions.SaveAsync(Snapshot(SessionA, persona), 0);

            var source = await service.WriteAsync(
                new TrustedMemoryOwner(SessionA),
                new MemoryWriteProposal(MemoryKind.Fact, "Ship the report", Shared, []),
                admission);
            var denied = await Assert.ThrowsAsync<AgentCoreException>(() =>
                service.PromoteSessionToUserAsync(
                    new TrustedMemoryOwner(SessionA),
                    source.MemoryId,
                    new TrustedUserOwner(ProfileA),
                    promotionAllowed: false,
                    admission).AsTask());
            Assert.Equal("PolicyDenied", denied.Code);
            Assert.DoesNotContain(Shared, denied.Message, StringComparison.Ordinal);
            Assert.Empty(await service.SearchUserAsync(
                new TrustedUserOwner(ProfileA),
                new MemorySearchQuery(null, null),
                true,
                admission));

            var promoted = await service.PromoteSessionToUserAsync(
                new TrustedMemoryOwner(SessionA),
                source.MemoryId,
                new TrustedUserOwner(ProfileA),
                true,
                admission);
            Assert.Equal(MemoryScope.User, promoted.Scope);
            Assert.Equal(ProfileA, promoted.OwnerProfileId);
            Assert.Null(promoted.OwnerInstanceId);
            Assert.Equal(source.MemoryId, promoted.Provenance.OriginMemoryId);
            Assert.Equal(SessionA, promoted.Provenance.OriginSessionId);

            var identity = await service.PromoteToIdentityUserAsync(
                new TrustedMemoryOwner(SessionA),
                (await service.WriteAsync(
                    new TrustedMemoryOwner(SessionA),
                    new MemoryWriteProposal(MemoryKind.Fact, "Identity fact", IdentitySource, []),
                    admission)).MemoryId,
                new TrustedIdentityUserOwner(Alice, ProfileA),
                true,
                admission);
            var fromIdentity = await service.PromoteIdentityUserToUserAsync(
                new TrustedIdentityUserOwner(Alice, ProfileA),
                identity.MemoryId,
                new TrustedUserOwner(ProfileA),
                true,
                admission);
            Assert.Equal(identity.MemoryId, fromIdentity.Provenance.OriginMemoryId);
            var crossProfile = await Assert.ThrowsAsync<AgentCoreException>(() =>
                service.PromoteIdentityUserToUserAsync(
                    new TrustedIdentityUserOwner(Alice, ProfileA),
                    identity.MemoryId,
                    new TrustedUserOwner(ProfileB),
                    true,
                    admission).AsTask());
            Assert.Equal("PolicyDenied", crossProfile.Code);

            var other = await service.WriteAsync(
                new TrustedMemoryOwner(SessionB),
                new MemoryWriteProposal(MemoryKind.Fact, "Other profile", PrivateB, []),
                admission);
            await service.PromoteSessionToUserAsync(
                new TrustedMemoryOwner(SessionB),
                other.MemoryId,
                new TrustedUserOwner(ProfileB),
                true,
                admission);

            var reopened = Service(reopen(), 8);
            await AssertOnly(reopened, ProfileA, Shared, PrivateB);
            await AssertOnly(reopened, ProfileA, IdentitySource, PrivateB);
            await AssertOnly(reopened, ProfileB, PrivateB, Shared, IdentitySource);
            Assert.Empty(await reopened.SearchUserAsync(
                new TrustedUserOwner(ProfileA),
                new MemorySearchQuery(null, null),
                retrievalAllowed: false,
                admission));

            var corrected = await service.UpdateUserAsync(
                new TrustedUserOwner(ProfileA),
                new MemoryUpdateProposal(promoted.MemoryId, "Ship the report", Corrected, []),
                true,
                admission);
            Assert.Equal(source.MemoryId, corrected.Provenance.OriginMemoryId);
            var sessionStill = await service.SearchAsync(
                new TrustedMemoryOwner(SessionA),
                new MemorySearchQuery(null, null),
                admission);
            Assert.Contains(sessionStill, item => item.MemoryId == source.MemoryId && item.Content == Shared);
            var identityStill = await service.SearchIdentityUserAsync(
                new TrustedIdentityUserOwner(Alice, ProfileA),
                new MemorySearchQuery(null, null),
                true,
                admission);
            Assert.Contains(identityStill, item => item.MemoryId == identity.MemoryId && item.Content == IdentitySource);
            await AssertOnly(service, ProfileA, Corrected, Shared, PrivateB);
            var wrongProfile = await Assert.ThrowsAsync<AgentCoreException>(() =>
                service.UpdateUserAsync(
                    new TrustedUserOwner(ProfileB),
                    new MemoryUpdateProposal(corrected.MemoryId, "Ship the report", PrivateB, []),
                    true,
                    admission).AsTask());
            Assert.Equal("NotFound", wrongProfile.Code);

            await store.DeleteSessionAsync(SessionA);
            Assert.Empty(await service.SearchAsync(
                new TrustedMemoryOwner(SessionA),
                new MemorySearchQuery(null, null),
                admission));
            await AssertOnly(Service(reopen(), 4), ProfileA, Corrected, Shared, PrivateB);
            Assert.Contains(
                await service.SearchIdentityUserAsync(
                    new TrustedIdentityUserOwner(Alice, ProfileA),
                    new MemorySearchQuery(null, null),
                    true,
                    admission),
                item => item.Content == IdentitySource);

            await service.DeleteUserAsync(new TrustedUserOwner(ProfileA), corrected.MemoryId, true);
            var afterDelete = await service.SearchUserAsync(
                new TrustedUserOwner(ProfileA),
                new MemorySearchQuery(null, null),
                true,
                admission);
            Assert.DoesNotContain(afterDelete, item => item.Content == Corrected);
            Assert.Contains(afterDelete, item => item.Content == IdentitySource);

            Assert.Equal("pinned tone", (await instances.FindAsync(Alice))!.Persona.Tone);
            Assert.Equal("pinned tone", (await sessions.LoadAsync(SessionA))!.PinnedPersona!.Tone);
        });
    }

    [Fact]
    public async Task Eligible_identity_receives_user_memory_and_examiner_does_not()
    {
        var memories = new InMemoryStructuredMemoryStore();
        var service = Service(memories, 40);
        var admission = Admission();
        var source = await service.WriteAsync(
            new TrustedMemoryOwner(SessionA),
            new MemoryWriteProposal(MemoryKind.Fact, "Ship the report", Shared, []),
            admission);
        await service.PromoteSessionToUserAsync(
            new TrustedMemoryOwner(SessionA),
            source.MemoryId,
            new TrustedUserOwner(ProfileA),
            true,
            admission);
        await service.PromoteSessionToUserAsync(
            new TrustedMemoryOwner(SessionB),
            (await service.WriteAsync(
                new TrustedMemoryOwner(SessionB),
                new MemoryWriteProposal(MemoryKind.Fact, "Other profile", PrivateB, []),
                admission)).MemoryId,
            new TrustedUserOwner(ProfileB),
            true,
            admission);
        await service.PromoteSessionToUserAsync(
            new TrustedMemoryOwner(SessionA),
            (await service.WriteAsync(
                new TrustedMemoryOwner(SessionA),
                new MemoryWriteProposal(MemoryKind.Preference, "nickname", "call them Sam", []),
                admission)).MemoryId,
            new TrustedUserOwner(ProfileA),
            true,
            admission);
        for (var index = 0; index < 8; index++)
        {
            var extraSession = Guid.Parse($"019944af-001d-7000-8000-0000000001{index:D2}");
            await service.PromoteSessionToUserAsync(
                new TrustedMemoryOwner(extraSession),
                (await service.WriteAsync(
                    new TrustedMemoryOwner(extraSession),
                    new MemoryWriteProposal(MemoryKind.Fact, $"extra {index}", $"extra content {index}", []),
                    admission)).MemoryId,
                new TrustedUserOwner(ProfileA),
                true,
                admission);
        }

        var profile = Profile();
        var eligible = Eligible();
        var learned = await SessionMemoryPrompt.LoadAsync(
            service,
            Guid.Parse("019944af-001d-7000-8000-0000000000e2"),
            eligible,
            profile,
            [],
            agentInstanceId: Bob);
        Assert.Equal(8, learned.Count);
        Assert.Contains(learned, item => item.Content == Shared);
        Assert.DoesNotContain(learned, item => item.Content == PrivateB);
        var request = new PromptContextBuilder().Build(
            new AgentContext(
                eligible,
                [],
                string.Empty,
                profile,
                SessionMode.Text,
                null,
                false,
                null,
                new AgentTrigger(Guid.NewGuid(), TriggerKind.UserTurn, $"ignore {ProfileB}"),
                LearnedMemories: learned),
            Guid.NewGuid());
        var text = string.Join('\n', request.Messages.Select(message => message.Text));
        Assert.Contains(Shared, text, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateB, text, StringComparison.Ordinal);
        Assert.Contains("preferredName=Pat", text, StringComparison.Ordinal);
        Assert.Contains(SessionMemoryPrompt.TrustedPrecedence, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Shared, request.Messages[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("call them Sam", request.Messages[2].Text, StringComparison.Ordinal);

        var denied = await SessionMemoryPrompt.LoadAsync(
            service,
            Guid.Parse("019944af-001d-7000-8000-0000000000e3"),
            SampleDefinitions.Examiner,
            profile,
            [],
            agentInstanceId: Alice);
        Assert.Empty(denied);
        var deniedRequest = new PromptContextBuilder().Build(
            new AgentContext(
                SampleDefinitions.Examiner,
                [],
                string.Empty,
                profile,
                SessionMode.Text,
                null,
                false,
                null,
                new AgentTrigger(Guid.NewGuid(), TriggerKind.UserTurn, "Hello"),
                LearnedMemories: denied),
            Guid.NewGuid());
        Assert.DoesNotContain(
            Shared,
            string.Join('\n', deniedRequest.Messages.Select(message => message.Text)),
            StringComparison.Ordinal);

        var sessions = new InMemoryMemoryStore();
        await sessions.SaveProfileAsync(profile, 0);
        var runtimeSession = Guid.Parse("019944af-001d-7000-8000-0000000000e4");
        var snapshot = Snapshot(runtimeSession, eligible.Identity) with
        {
            Definition = eligible,
            AgentInstanceId = Bob
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
                Enumerable.Range(1, 16).Select(index => Guid.Parse($"019944af-001e-7000-8000-{index:D12}")),
                [runtimeSession]),
            new FakeTimeProvider(Now),
            NullLogger<SessionRuntime>.Instance,
            structuredMemory: service);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync($"Please use profile {ProfileB}");
        await runtime.WaitUntilIdleAsync();
        var generated = string.Join('\n', model.LastRequest!.Messages.Select(message => message.Text));
        Assert.Contains(Shared, generated, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateB, generated, StringComparison.Ordinal);
        Assert.Contains("preferredName=Pat", generated, StringComparison.Ordinal);
    }

    private static async Task AssertOnly(
        StructuredMemoryService service,
        Guid profileId,
        string expected,
        params string[] absent)
    {
        var found = await service.SearchUserAsync(
            new TrustedUserOwner(profileId),
            new MemorySearchQuery(null, null),
            true,
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

        var path = Path.Combine(Path.GetTempPath(), $"agent-core-user-memory-{Guid.NewGuid():N}.db");
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
                Enumerable.Range(1, count).Select(index => Guid.Parse($"019944af-001d-7000-8000-{index:D12}")),
                [Guid.Parse("019944af-001d-7000-8000-0000000000ff")]),
            new FakeTimeProvider(Now));

    private static MemoryAdmissionContext Admission() =>
        new("application", [], new HashSet<string>(StringComparer.Ordinal));

    private static AgentDefinition Eligible() =>
        SampleDefinitions.Support with
        {
            MemoryPolicy = new MemoryPolicy(UserPromotion: true, UserRetrieval: true)
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
            SampleDefinitions.Support,
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
