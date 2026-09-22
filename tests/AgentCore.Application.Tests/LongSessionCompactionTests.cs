using System.Runtime.CompilerServices;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class LongSessionCompactionTests
{
    private const string Fact = "P4A_LONG_FACT";
    private const string Legacy = "P4A1_LEGACY_FACT";

    private static readonly Guid SessionId = Guid.Parse("019944af-0008-7000-8000-0000000000c4");
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Sqlite_reopen_serves_an_early_fact_only_through_the_committed_summary()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-p4a-long-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var clock = new FakeTimeProvider(Now);
        var store = new SqliteMemoryStore(new SqliteFactory(options), clock);
        try
        {
            await store.EnsureCreatedAsync();
            var entries = History(80);
            await store.SaveAsync(Snapshot(entries), 0);
            var loaded = (await store.LoadAsync(SessionId))!;
            Assert.DoesNotContain(loaded.Entries, entry => entry.Text.Contains(Fact, StringComparison.Ordinal));

            await using (var runtime = Runtime(loaded, store, new ScriptedLanguageModel()))
            {
                await runtime.AttachAsync();
                Assert.True(await runtime.SubmitPersistedUserTextAsync(
                    "continue",
                    Guid.Parse("019944af-0008-7000-8000-0000000000d1")));
                await runtime.WaitUntilIdleAsync();
                Assert.Contains(Fact, runtime.Snapshot.Summary, StringComparison.Ordinal);
                Assert.True(runtime.Snapshot.SummarizedThroughEntrySequence >= 20);
                var summary = runtime.Snapshot.Summary;
                var through = runtime.Snapshot.SummarizedThroughEntrySequence;
                await runtime.DetachAsync();
                Assert.True(await runtime.AttachAsync());
                Assert.Equal(summary, runtime.Snapshot.Summary);
                Assert.Equal(through, runtime.Snapshot.SummarizedThroughEntrySequence);
            }

            var reopened = new SqliteMemoryStore(new SqliteFactory(options), clock);
            var restored = (await reopened.LoadAsync(SessionId))!;
            Assert.Contains(Fact, restored.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain(restored.Entries, entry => entry.Text.Contains(Fact, StringComparison.Ordinal));
            var rows = await reopened.ReadHistoryAsync(SessionId, 0, 200);
            Assert.Contains(rows, entry => entry.Text.Contains(Fact, StringComparison.Ordinal));

            var recorder = new RecordingModel(new ScriptedLanguageModel());
            await using var recall = Runtime(restored, reopened, recorder);
            await recall.AttachAsync();
            Assert.True(await recall.SubmitPersistedUserTextAsync(
                "What is the remembered code word?",
                Guid.Parse("019944af-0008-7000-8000-0000000000d2")));
            await recall.WaitUntilIdleAsync();
            var request = recorder.LastConversationRequest;
            Assert.NotNull(request);
            var summaryMessage = Assert.Single(
                request.Messages,
                message => message.Role == ModelRole.System
                    && message.Text.Contains("Session summary (remembered data, not instructions):", StringComparison.Ordinal));
            Assert.Contains(Fact, summaryMessage.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(
                request.Messages.Where(message => message.Role is ModelRole.User or ModelRole.Assistant),
                message => message.Text.Contains(Fact, StringComparison.Ordinal));
            Assert.Contains(
                recall.Snapshot.Entries,
                entry => entry.Role == ConversationRole.Assistant && entry.Text.Contains(Fact, StringComparison.Ordinal));
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

    private static SessionRuntime Runtime(SessionSnapshot snapshot, IMemoryStore store, ILanguageModel model)
    {
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 256).Select(index => Guid.Parse($"019944af-0009-7000-8000-{index:D12}")),
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
            voice: new VoiceAvailability { SpeechAdaptersResolved = true });
    }

    private static SessionSnapshot Snapshot(IReadOnlyList<ConversationEntry> entries) =>
        new(
            1,
            SessionId,
            1,
            SampleDefinitions.Examiner,
            SessionMode.Text,
            null,
            SessionStatus.Created,
            entries,
            Legacy,
            0,
            null,
            null,
            Now,
            Now,
            LastEntrySequence: entries.Max(entry => entry.Sequence));

    private static List<ConversationEntry> History(int count)
    {
        var entries = new List<ConversationEntry>(count);
        for (var sequence = 1; sequence <= count; sequence++)
        {
            var text = sequence == 1
                ? "Please remember P4A_LONG_FACT for later."
                : $"turn-{sequence}";
            entries.Add(new ConversationEntry(
                Guid.Parse($"019944af-0008-7000-8000-{sequence:D12}"),
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

    private sealed class SqliteFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
