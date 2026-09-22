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

public sealed class SessionMemoryCheckpointTests
{
    private const string OtherSessionSentinel = "SESSION_B_ONLY_SENTINEL";

    private static readonly Guid SessionA = Guid.Parse("019944af-0008-7000-8000-0000000000a7");
    private static readonly Guid SessionB = Guid.Parse("019944af-0008-7000-8000-0000000000b7");
    private static readonly Guid FactSource = Guid.Parse("019944af-0008-7000-8000-0000000000e7");
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 5, 20, 0, TimeSpan.Zero);

    [Fact]
    public async Task Sqlite_reopen_keeps_corrected_memory_apart_from_profile_and_other_sessions()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-p4b-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var clock = new FakeTimeProvider(Now);
        var factory = new SqliteFactory(options);
        var sessions = new SqliteMemoryStore(factory, clock);
        try
        {
            await sessions.EnsureCreatedAsync();
            await sessions.SaveProfileAsync(Profile(), 0);
            await sessions.SaveAsync(Snapshot(SessionA), 0);
            await sessions.SaveAsync(Snapshot(SessionB), 0);
            var service = Service(new SqliteStructuredMemoryStore(factory), clock, 16);
            var owner = new TrustedMemoryOwner(SessionA);
            var other = new TrustedMemoryOwner(SessionB);
            var admission = Admission();

            var fact = await service.WriteAsync(
                owner,
                new MemoryWriteProposal(MemoryKind.Fact, "Ship the report", "by Friday", [FactSource]),
                admission);
            clock.Advance(TimeSpan.FromSeconds(1));
            await service.WriteAsync(owner, new MemoryWriteProposal(MemoryKind.Preference, "Call window", "mornings", []), admission);
            clock.Advance(TimeSpan.FromSeconds(1));
            await service.WriteAsync(owner, new MemoryWriteProposal(MemoryKind.Goal, "Finish the gate", "before P4C", []), admission);
            clock.Advance(TimeSpan.FromSeconds(1));
            await service.WriteAsync(owner, new MemoryWriteProposal(MemoryKind.Decision, "Keep SQLite", "for this session", []), admission);
            clock.Advance(TimeSpan.FromSeconds(1));
            var openLoop = await service.WriteAsync(
                owner,
                new MemoryWriteProposal(MemoryKind.OpenLoop, "Follow up", "still open", []),
                admission);
            var created = await service.SearchAsync(owner, new MemorySearchQuery(null, null), admission);
            Assert.Equal(5, created.Count);
            Assert.Equal(
                [MemoryKind.OpenLoop, MemoryKind.Decision, MemoryKind.Goal, MemoryKind.Preference, MemoryKind.Fact],
                created.Select(item => item.Kind));

            var rejected = await Assert.ThrowsAsync<AgentCoreException>(() => service.WriteAsync(
                owner,
                new MemoryWriteProposal(MemoryKind.Preference, "preferredName", "Not Pat", []),
                admission).AsTask());
            Assert.Equal("MemoryRejected", rejected.Code);
            Assert.DoesNotContain("Not Pat", rejected.Message, StringComparison.Ordinal);
            var profile = (await sessions.LoadProfileAsync(LocalUserProfile.Id))!;
            Assert.Equal(1, profile.Revision);
            Assert.Equal("Pat", profile.Preferences["preferredName"].Value);
            Assert.Equal(UserProfileValueSource.UserSet, profile.Preferences["preferredName"].Source);

            clock.Advance(TimeSpan.FromSeconds(1));
            var corrected = await service.UpdateAsync(
                owner,
                new MemoryUpdateProposal(fact.MemoryId, "Ship the report", "by Monday", [FactSource]),
                admission);
            Assert.Equal(fact.MemoryId, corrected.Provenance.SupersedesMemoryId);
            Assert.Equal([FactSource], corrected.Provenance.SourceEntryIds);
            var tombstone = await service.DeleteAsync(owner, openLoop.MemoryId);
            Assert.Equal(MemoryItemStatus.Deleted, tombstone.Status);
            Assert.Equal(string.Empty, tombstone.Content);
            await service.WriteAsync(
                other,
                new MemoryWriteProposal(MemoryKind.Fact, "Other session", OtherSessionSentinel, []),
                admission);

            var visible = await service.SearchAsync(owner, new MemorySearchQuery(null, null), admission);
            Assert.Equal(4, visible.Count);
            Assert.Contains(visible, item => item.Content == "by Monday");
            Assert.DoesNotContain(visible, item => item.Content is "by Friday" or "still open" || item.Content == OtherSessionSentinel);
            Assert.Null(await service.GetAsync(other, corrected.MemoryId, admission));
            Assert.Empty(await service.SearchAsync(other, new MemorySearchQuery("Monday", null), admission));

            var reopenedSessions = new SqliteMemoryStore(factory, clock);
            var reopened = Service(new SqliteStructuredMemoryStore(factory), clock, 4);
            var restored = await reopened.SearchAsync(owner, new MemorySearchQuery(null, null), admission);
            Assert.Equal(4, restored.Count);
            Assert.Contains(restored, item => item.MemoryId == corrected.MemoryId && item.Content == "by Monday");
            Assert.DoesNotContain(restored, item => item.Content == OtherSessionSentinel);
            var prior = await reopened.GetAsync(owner, fact.MemoryId, admission);
            Assert.Equal(MemoryItemStatus.Superseded, prior!.Status);
            Assert.Equal("by Friday", prior.Content);
            var deleted = await reopened.GetAsync(owner, openLoop.MemoryId, admission);
            Assert.Equal(string.Empty, deleted!.Content);
            Assert.Equal(OtherSessionSentinel, Assert.Single(await reopened.SearchAsync(other, new MemorySearchQuery(null, null), admission)).Content);
            var reopenedProfile = (await reopenedSessions.LoadProfileAsync(LocalUserProfile.Id))!;
            Assert.Equal("Pat", reopenedProfile.Preferences["preferredName"].Value);

            var restoredSession = (await reopenedSessions.LoadAsync(SessionA))!;
            Assert.True(restoredSession.Definition.MemoryPolicy?.SessionMemory);
            var model = new RecordingLanguageModel(new ScriptedLanguageModel());
            await using (var runtime = Runtime(restoredSession, reopenedSessions, reopened, model, clock))
            {
                await runtime.AttachAsync();
                await runtime.SubmitUserTextAsync("Hello");
                await runtime.WaitUntilIdleAsync();
            }

            var request = model.LastRequest!;
            var learned = request.Messages.Single(message => message.Text.StartsWith(SessionMemoryPrompt.LearnedDataLabel, StringComparison.Ordinal)).Text;
            Assert.Contains("fact: Ship the report | by Monday", learned, StringComparison.Ordinal);
            Assert.Contains(SessionMemoryPrompt.TrustedPrecedence, learned, StringComparison.Ordinal);
            Assert.DoesNotContain(OtherSessionSentinel, learned, StringComparison.Ordinal);
            Assert.DoesNotContain("still open", learned, StringComparison.Ordinal);
            Assert.DoesNotContain("preferredName=", learned, StringComparison.Ordinal);
            Assert.Contains("preferredName=Pat", request.Messages[2].Text, StringComparison.Ordinal);
            Assert.Contains("Identity: Alex", request.Messages[0].Text, StringComparison.Ordinal);
            Assert.DoesNotContain("by Monday", request.Messages[0].Text, StringComparison.Ordinal);

            var manager = new SessionManager(
                new StaticDefinitions(Enabled()),
                reopenedSessions,
                Ids(8, "019944af-0015-7000-8000-"),
                clock,
                new VoiceAvailability { SpeechAdaptersResolved = true },
                structuredMemory: new SqliteStructuredMemoryStore(factory));
            await manager.DurablyDeleteAsync(SessionA);
            Assert.Empty(await reopened.SearchAsync(owner, new MemorySearchQuery(null, null), admission));
            Assert.Null(await reopened.GetAsync(owner, corrected.MemoryId, admission));
            Assert.Equal(OtherSessionSentinel, Assert.Single(await reopened.SearchAsync(other, new MemorySearchQuery(null, null), admission)).Content);
            Assert.Equal("Pat", (await reopenedSessions.LoadProfileAsync(LocalUserProfile.Id))!.Preferences["preferredName"].Value);
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

    private static SessionRuntime Runtime(
        SessionSnapshot snapshot,
        IMemoryStore sessions,
        IStructuredMemoryService memories,
        ILanguageModel model,
        TimeProvider time) =>
        new(
            snapshot,
            model,
            new DefaultAgentBrain(new PromptContextBuilder()),
            sessions,
            new CapturingSessionOutput(),
            Ids(32, "019944af-0016-7000-8000-"),
            time,
            NullLogger<SessionRuntime>.Instance,
            structuredMemory: memories);

    private static StructuredMemoryService Service(IStructuredMemoryStore store, TimeProvider time, int count) =>
        new(store, Ids(count, "019944af-0014-7000-8000-"), time);

    private static DeterministicIdGenerator Ids(int count, string prefix) =>
        new(
            Enumerable.Range(1, count).Select(index => Guid.Parse($"{prefix}{index:D12}")),
            [Guid.Parse("019944af-0014-7000-8000-0000000000ff")]);

    private static AgentDefinition Enabled() =>
        SampleDefinitions.Examiner with { MemoryPolicy = new MemoryPolicy(SessionMemory: true) };

    private static UserProfile Profile() =>
        new(
            LocalUserProfile.Id,
            1,
            new Dictionary<string, UserProfileValue>(StringComparer.Ordinal)
            {
                ["language"] = LocalUserProfile.ApplicationProfileValue("en", Now),
                ["preferredName"] = new UserProfileValue("Pat", UserProfileValueSource.UserSet, Now)
            },
            Now);

    private static SessionSnapshot Snapshot(Guid sessionId) =>
        new(
            1,
            sessionId,
            1,
            Enabled(),
            SessionMode.Text,
            null,
            SessionStatus.Created,
            [],
            string.Empty,
            0,
            null,
            LocalUserProfile.Id,
            Now,
            Now);

    private static MemoryAdmissionContext Admission() =>
        new("application", [], new HashSet<string>(StringComparer.Ordinal));

    private sealed class SqliteFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class StaticDefinitions(AgentDefinition definition) : IAgentDefinitionStore
    {
        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AgentDefinition>>([definition]);

        public ValueTask<AgentDefinition?> GetAsync(
            string id,
            int? version = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AgentDefinition?>(
                string.Equals(id, definition.Id, StringComparison.Ordinal) ? definition : null);
    }
}
