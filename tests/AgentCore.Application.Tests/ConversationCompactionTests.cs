using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Application.Tests;

public sealed class ConversationCompactionTests
{
    private const string Legacy = "P4A1_LEGACY_FACT";
    private const string Goal = "P4A1_KEEP_GOAL";
    private const string Decision = "P4A1_KEEP_DECISION";
    private const string Constraint = "P4A1_KEEP_CONSTRAINT";
    private const string Unresolved = "P4A1_KEEP_UNRESOLVED";
    private const string Reference = "P4A1_KEEP_REFERENCE";
    private const string OpenLoop = "P4A1_KEEP_OPEN_LOOP";
    private const string Secret = "sk-livep4a1secretvalue01";
    private const string Visible = "P4A1_VISIBLE_PREFIX";
    private const string Hidden = "P4A1_HIDDEN_TAIL";
    private const string FailedHidden = "P4A1_FAILED_UNDELIVERED";
    private const string Interrupt = "P4A1_INTERRUPT_DECOY";
    private const string Finish = "P4A1_FINISH_DECOY";
    private const string ProvenanceDecoy = "p4a1-provenance-decoy";
    private const string Decoy = "P4A1_DECOY_RETAINED";
    private const string Lookahead = "P4A1_LOOKAHEAD";
    private const string BeyondRead = "P4A1_BEYOND_READ";
    private const string AfterOversized = "P4A1_AFTER_OVERSIZED";

    private static readonly string Binary = "P4A1_BINARY_" + new string('A', 80);
    private static readonly Guid SessionId = Guid.Parse("019944af-0004-7000-8000-0000000000b2");
    private static readonly Guid AttachmentId = Guid.Parse("019944af-0004-7000-8000-0000000000c1");
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 1, 0, 0, TimeSpan.Zero);
    private static readonly ModelGenerationProvenance Provenance = new("synthetic-default", "synthetic", "scripted", "low");

    [Fact]
    public async Task Semantic_compaction_keeps_facts_and_leaves_raw_rows_unchanged()
    {
        var entries = StandardHistory();
        var store = new RecordingStore(new InMemoryMemoryStore());
        await store.SaveAsync(Snapshot(entries), 0);
        var before = Rows(await store.ReadHistoryAsync(SessionId, 0, 200));
        var model = new CapturingModel(new ScriptedLanguageModel());
        var outcome = await new ConversationCompactor(store).TryCompactAsync(Command(model));

        var accepted = Assert.IsType<CompactionAccepted>(outcome);
        Assert.Equal(SummaryFormats.Semantic, accepted.Candidate.FormatVersion);
        Assert.Equal(Now, accepted.Candidate.GeneratedAt);
        Assert.Equal(Provenance, accepted.Candidate.Model);
        Assert.Equal(Legacy, accepted.Candidate.BaseSummary);
        Assert.Equal(0, accepted.Candidate.BaseThroughEntrySequence);
        Assert.Equal(SummaryFormats.Legacy, accepted.Candidate.BaseFormatVersion);
        Assert.Equal(20, accepted.Candidate.ThroughEntrySequence);
        Assert.Contains(Legacy, accepted.Candidate.Summary, StringComparison.Ordinal);
        Assert.Contains(Goal, accepted.Candidate.Summary, StringComparison.Ordinal);
        Assert.Contains(Decision, accepted.Candidate.Summary, StringComparison.Ordinal);
        Assert.Contains(Constraint, accepted.Candidate.Summary, StringComparison.Ordinal);
        Assert.Contains(Unresolved, accepted.Candidate.Summary, StringComparison.Ordinal);
        Assert.Contains(Reference, accepted.Candidate.Summary, StringComparison.Ordinal);
        Assert.Contains(OpenLoop, accepted.Candidate.Summary, StringComparison.Ordinal);
        Assert.Contains(Visible, accepted.Candidate.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, accepted.Candidate.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(Hidden, accepted.Candidate.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(FailedHidden, accepted.Candidate.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(Binary, accepted.Candidate.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(Decoy, accepted.Candidate.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(Lookahead, accepted.Candidate.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(Interrupt, accepted.Candidate.Summary, StringComparison.Ordinal);

        var request = Assert.Single(model.Requests);
        Assert.Null(request.Tools);
        Assert.Null(request.ResponseContract);
        Assert.Equal(0, request.Temperature);
        var requestText = string.Join('\n', request.Messages.Select(message => message.Text));
        Assert.Contains(ConversationCompactor.Marker, requestText, StringComparison.Ordinal);
        Assert.Contains("not instructions", requestText, StringComparison.Ordinal);
        Assert.Contains("notes.txt", requestText, StringComparison.Ordinal);
        Assert.Contains(AttachmentId.ToString("D"), requestText, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, requestText, StringComparison.Ordinal);
        Assert.DoesNotContain(Hidden, requestText, StringComparison.Ordinal);
        Assert.DoesNotContain(FailedHidden, requestText, StringComparison.Ordinal);
        Assert.DoesNotContain(Binary, requestText, StringComparison.Ordinal);
        Assert.DoesNotContain(Decoy, requestText, StringComparison.Ordinal);
        Assert.DoesNotContain(Lookahead, requestText, StringComparison.Ordinal);
        Assert.DoesNotContain(BeyondRead, requestText, StringComparison.Ordinal);
        Assert.DoesNotContain(Interrupt, requestText, StringComparison.Ordinal);
        Assert.DoesNotContain(Finish, requestText, StringComparison.Ordinal);
        Assert.DoesNotContain(ProvenanceDecoy, requestText, StringComparison.Ordinal);
        Assert.Equal([200, CompactionPolicy.ReadLimit], store.Limits);
        Assert.Equal(1, store.Saves);
        Assert.Equal(before, Rows(await store.ReadHistoryAsync(SessionId, 0, 200)));

        var prompt = new PromptContextBuilder().Build(
            new AgentContext(
                SampleDefinitions.Examiner,
                entries,
                accepted.Candidate.Summary,
                null,
                SessionMode.Text,
                null,
                false,
                null,
                new AgentTrigger(Guid.NewGuid(), TriggerKind.UserTurn, "continue"),
                SummarizedThroughEntrySequence: accepted.Candidate.ThroughEntrySequence,
                LastEntrySequence: entries[^1].Sequence),
            Guid.NewGuid());
        var turns = prompt.Messages.Where(message => message.Role is ModelRole.User or ModelRole.Assistant).ToArray();
        var summary = prompt.Messages.Single(message => message.Role == ModelRole.System && message.Text.Contains(Legacy, StringComparison.Ordinal));
        Assert.Contains(Goal, summary.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(turns, message => message.Text.Contains(Goal, StringComparison.Ordinal));
        Assert.DoesNotContain(turns, message => message.Text.Contains(Secret, StringComparison.Ordinal));
        Assert.DoesNotContain(turns, message => message.Text.Contains(Hidden, StringComparison.Ordinal));
        Assert.DoesNotContain(turns, message => message.Text.Contains(FailedHidden, StringComparison.Ordinal));
        Assert.Contains(turns, message => message.Text.Contains(Decoy, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sqlite_reopen_preserves_summary_metadata_and_raw_rows()
    {
        var entries = StandardHistory();
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-p4a1-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var store = new SqliteMemoryStore(new SqliteFactory(options), new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Now));
        try
        {
            await store.EnsureCreatedAsync();
            await store.SaveAsync(Snapshot(entries), 0);
            var loaded = (await store.LoadAsync(SessionId))!;
            Assert.Equal(SummaryFormats.Legacy, loaded.SummaryFormatVersion);
            Assert.Null(loaded.SummaryGeneratedAt);
            Assert.Null(loaded.SummaryModel);
            Assert.Equal(Legacy, loaded.Summary);

            var before = Rows(await store.ReadHistoryAsync(SessionId, 0, 200));
            var outcome = await new ConversationCompactor(store).TryCompactAsync(Command(new ScriptedLanguageModel()));
            var accepted = Assert.IsType<CompactionAccepted>(outcome);
            Assert.Equal(before, Rows(await store.ReadHistoryAsync(SessionId, 0, 200)));

            var updated = loaded with
            {
                Revision = 2,
                Summary = accepted.Candidate.Summary,
                SummarizedThroughEntrySequence = accepted.Candidate.ThroughEntrySequence,
                SummaryFormatVersion = accepted.Candidate.FormatVersion,
                SummaryGeneratedAt = accepted.Candidate.GeneratedAt,
                SummaryModel = accepted.Candidate.Model,
                UpdatedAt = Now
            };
            await store.SaveAsync(updated, 1);

            var reopened = new SqliteMemoryStore(new SqliteFactory(options), new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Now));
            await reopened.EnsureCreatedAsync();
            var restored = (await reopened.LoadAsync(SessionId))!;
            Assert.Equal(accepted.Candidate.Summary, restored.Summary);
            Assert.Equal(accepted.Candidate.ThroughEntrySequence, restored.SummarizedThroughEntrySequence);
            Assert.Equal(SummaryFormats.Semantic, restored.SummaryFormatVersion);
            Assert.Equal(Now, restored.SummaryGeneratedAt);
            Assert.Equal(Provenance, restored.SummaryModel);
            Assert.Equal(before, Rows(await reopened.ReadHistoryAsync(SessionId, 0, 200)));
        }
        finally
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
    }

    [Fact]
    public async Task Compaction_preserves_the_prior_summary_when_output_is_unusable()
    {
        var cases = new (CompactionFixture Fixture, string Reason)[]
        {
            (CompactionFixture.Empty, CompactionRejection.Empty),
            (CompactionFixture.Malformed, CompactionRejection.Malformed),
            (CompactionFixture.Oversized, CompactionRejection.Oversized),
            (CompactionFixture.Throw, CompactionRejection.ProviderFailure)
        };

        foreach (var (fixture, reason) in cases)
        {
            var entries = StandardHistory();
            var store = new RecordingStore(new InMemoryMemoryStore());
            await store.SaveAsync(Snapshot(entries), 0);
            var before = Rows(await store.ReadHistoryAsync(SessionId, 0, 200));
            var outcome = await new ConversationCompactor(store).TryCompactAsync(
                Command(new ScriptedLanguageModel(compactionFixture: fixture)));
            var rejected = Assert.IsType<CompactionRejected>(outcome);
            Assert.Equal(reason, rejected.Reason);
            Assert.Contains(CompactionPolicy.ReadLimit, store.Limits);
            Assert.Equal(before, Rows(await store.ReadHistoryAsync(SessionId, 0, 200)));
            Assert.Equal(1, store.Saves);
            var loaded = (await store.LoadAsync(SessionId))!;
            Assert.Equal(Legacy, loaded.Summary);
            Assert.Equal(0, loaded.SummarizedThroughEntrySequence);
            Assert.Equal(SummaryFormats.Legacy, loaded.SummaryFormatVersion);
        }
    }

    [Fact]
    public async Task Late_output_after_cancellation_is_not_accepted()
    {
        var entries = StandardHistory();
        var store = new RecordingStore(new InMemoryMemoryStore());
        await store.SaveAsync(Snapshot(entries), 0);
        var before = Rows(await store.ReadHistoryAsync(SessionId, 0, 200));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new ScriptedLanguageModel(
            compactionFixture: CompactionFixture.Late,
            compactionRelease: release,
            compactionStarted: started);
        using var cts = new CancellationTokenSource();
        var pending = new ConversationCompactor(store).TryCompactAsync(Command(model), cts.Token);
        var startedInTime = await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(started.Task, startedInTime);
        cts.Cancel();
        release.TrySetResult();
        var rejected = Assert.IsType<CompactionRejected>(await pending);
        Assert.Equal(CompactionRejection.Cancelled, rejected.Reason);
        Assert.Equal(before, Rows(await store.ReadHistoryAsync(SessionId, 0, 200)));
        Assert.Equal(Legacy, (await store.LoadAsync(SessionId))!.Summary);
    }

    [Fact]
    public async Task Unsafe_summary_and_provider_reasoning_do_not_replace_the_prior_summary()
    {
        var entries = StandardHistory();
        var store = new RecordingStore(new InMemoryMemoryStore());
        await store.SaveAsync(Snapshot(entries), 0);

        var unsafeOutcome = await new ConversationCompactor(store).TryCompactAsync(
            Command(new TextModel(new ModelTextDelta(Secret), new ModelCompleted(ModelStopReason.Completed))));
        Assert.Equal(CompactionRejection.Unsafe, Assert.IsType<CompactionRejected>(unsafeOutcome).Reason);

        var reasoned = await new ConversationCompactor(store).TryCompactAsync(
            Command(new TextModel(
                new ModelReasoningDelta("P4A1_REASONING_DECOY"),
                new ModelTextDelta("Summary: " + Goal),
                new ModelCompleted(ModelStopReason.Completed))));
        var accepted = Assert.IsType<CompactionAccepted>(reasoned);
        Assert.Contains(Goal, accepted.Candidate.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("P4A1_REASONING_DECOY", accepted.Candidate.Summary, StringComparison.Ordinal);
        Assert.Equal(Legacy, (await store.LoadAsync(SessionId))!.Summary);
    }

    [Fact]
    public async Task Source_selection_does_not_skip_or_pass_unstable_rows()
    {
        var shortStore = new RecordingStore(new InMemoryMemoryStore());
        await shortStore.SaveAsync(Snapshot(StandardHistory(12)), 0);
        var shortModel = new CapturingModel(new ScriptedLanguageModel());
        var shortOutcome = await new ConversationCompactor(shortStore).TryCompactAsync(Command(shortModel));
        Assert.Equal(CompactionRejection.NotReady, Assert.IsType<CompactionRejected>(shortOutcome).Reason);
        Assert.Empty(shortModel.Requests);

        var oversizedFirst = StandardHistory();
        oversizedFirst[0] = Entry(1, ConversationRole.User, new string('q', CompactionPolicy.MaxSourceCharacters + 1));
        var firstStore = new RecordingStore(new InMemoryMemoryStore());
        await firstStore.SaveAsync(Snapshot(oversizedFirst), 0);
        var firstModel = new CapturingModel(new ScriptedLanguageModel());
        var firstOutcome = await new ConversationCompactor(firstStore).TryCompactAsync(Command(firstModel));
        Assert.Equal(CompactionRejection.OversizedSource, Assert.IsType<CompactionRejected>(firstOutcome).Reason);
        Assert.Empty(firstModel.Requests);

        var oversizedLater = StandardHistory();
        oversizedLater[4] = Entry(5, ConversationRole.User, new string('q', CompactionPolicy.MaxSourceCharacters + 1));
        oversizedLater[5] = Entry(6, ConversationRole.Assistant, AfterOversized);
        var laterStore = new RecordingStore(new InMemoryMemoryStore());
        await laterStore.SaveAsync(Snapshot(oversizedLater), 0);
        var laterModel = new CapturingModel(new ScriptedLanguageModel());
        var laterOutcome = await new ConversationCompactor(laterStore).TryCompactAsync(Command(laterModel));
        var laterAccepted = Assert.IsType<CompactionAccepted>(laterOutcome);
        Assert.Equal(4, laterAccepted.Candidate.ThroughEntrySequence);
        Assert.Contains(Goal, laterAccepted.Candidate.Summary, StringComparison.Ordinal);
        var laterRequest = string.Join('\n', Assert.Single(laterModel.Requests).Messages.Select(message => message.Text));
        Assert.DoesNotContain(AfterOversized, laterRequest, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('q', 40), laterRequest, StringComparison.Ordinal);

        var openTurn = new List<ConversationEntry>();
        for (var sequence = 1; sequence <= 40; sequence++)
        {
            var role = sequence <= 20 ? ConversationRole.User : ConversationRole.Assistant;
            openTurn.Add(Entry(sequence, role, $"open-{sequence}"));
        }

        var openStore = new RecordingStore(new InMemoryMemoryStore());
        await openStore.SaveAsync(Snapshot(openTurn), 0);
        var openModel = new CapturingModel(new ScriptedLanguageModel());
        var openOutcome = await new ConversationCompactor(openStore).TryCompactAsync(Command(openModel));
        Assert.Equal(CompactionRejection.NotReady, Assert.IsType<CompactionRejected>(openOutcome).Reason);
        Assert.Empty(openModel.Requests);

        var streaming = StandardHistory();
        streaming[29] = Entry(30, ConversationRole.Assistant, "streaming", EntryStatus.Streaming, receivedEnd: 0);
        var streamingStore = new RecordingStore(new InMemoryMemoryStore());
        await streamingStore.SaveAsync(Snapshot(streaming), 0);
        var streamingModel = new CapturingModel(new ScriptedLanguageModel());
        var streamingOutcome = await new ConversationCompactor(streamingStore).TryCompactAsync(Command(streamingModel));
        Assert.Equal(CompactionRejection.NotReady, Assert.IsType<CompactionRejected>(streamingOutcome).Reason);
        Assert.Empty(streamingModel.Requests);

        var excluded = StandardHistory();
        var excludedStore = new RecordingStore(new InMemoryMemoryStore());
        await excludedStore.SaveAsync(Snapshot(excluded), 0);
        var excludedModel = new CapturingModel(new ScriptedLanguageModel());
        var excludedOutcome = await new ConversationCompactor(excludedStore).TryCompactAsync(
            Command(excludedModel, new HashSet<Guid> { excluded[9].EntryId }));
        Assert.Equal(CompactionRejection.NotReady, Assert.IsType<CompactionRejected>(excludedOutcome).Reason);
        Assert.Empty(excludedModel.Requests);
    }

    private static CompactionCommand Command(ILanguageModel model, IReadOnlySet<Guid>? excluded = null) =>
        new(SessionId, Legacy, 0, SummaryFormats.Legacy, model, Now, Provenance, excluded);

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

    private static List<ConversationEntry> StandardHistory(int count = 45)
    {
        var entries = new List<ConversationEntry>(count);
        for (var sequence = 1; sequence <= count; sequence++)
        {
            var role = sequence % 2 == 0 ? ConversationRole.Assistant : ConversationRole.User;
            var text = sequence switch
            {
                2 => Goal,
                4 => Decision,
                6 => Constraint,
                8 => Unresolved,
                10 => Reference,
                11 => Secret,
                13 => Binary,
                14 => Visible + Hidden,
                16 => OpenLoop,
                20 => FailedHidden,
                40 => Decoy,
                41 => Lookahead,
                42 => BeyondRead,
                _ => $"turn-{sequence}"
            };
            var status = sequence == 14 ? EntryStatus.Interrupted : sequence == 20 ? EntryStatus.Failed : EntryStatus.Completed;
            int? received = sequence switch
            {
                14 => Visible.Length,
                20 => 0,
                _ => null
            };
            entries.Add(Entry(
                sequence,
                role,
                text,
                status,
                received,
                sequence == 18 ? Interrupt : null,
                sequence == 18 ? Finish : null,
                sequence == 18 ? new ModelGenerationProvenance(ProvenanceDecoy, "synthetic", "decoy", null) : null,
                sequence == 10
                    ? [new ConversationAttachmentRef(AttachmentId, "notes.txt", "text/plain")]
                    : null));
        }

        return entries;
    }

    private static ConversationEntry Entry(
        long sequence,
        ConversationRole role,
        string text,
        EntryStatus status = EntryStatus.Completed,
        int? receivedEnd = null,
        string? interruptReason = null,
        string? finishReason = null,
        ModelGenerationProvenance? provenance = null,
        IReadOnlyList<ConversationAttachmentRef>? attachments = null)
    {
        var received = receivedEnd ?? text.Length;
        return new ConversationEntry(
            Guid.Parse($"019944af-0004-7000-8000-{sequence:D12}"),
            sequence,
            null,
            role,
            text,
            null,
            status,
            SessionMode.Text,
            0,
            received,
            Now.AddSeconds(sequence),
            Attachments: attachments,
            FinishReason: finishReason,
            InterruptReason: interruptReason,
            ModelProvenance: provenance);
    }

    private static string[] Rows(IReadOnlyList<ConversationEntry> entries) =>
        entries.Select(entry =>
                $"{entry.EntryId:N}|{entry.Sequence}|{entry.Role}|{entry.Text}|{entry.Status}|{entry.ReceivedTextEndExclusive}|{entry.HeardTextEndExclusive}|{entry.InterruptReason}|{entry.FinishReason}|{entry.ModelProvenance?.CatalogKey}")
            .ToArray();

    private sealed class RecordingStore(IMemoryStore inner) : IMemoryStore
    {
        public List<int> Limits { get; } = [];
        public int Saves { get; private set; }

        public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            inner.LoadAsync(sessionId, cancellationToken);

        public ValueTask SaveAsync(SessionSnapshot snapshot, long expectedRevision, CancellationToken cancellationToken = default)
        {
            Saves++;
            return inner.SaveAsync(snapshot, expectedRevision, cancellationToken);
        }

        public ValueTask<IReadOnlyList<ConversationEntry>> ReadHistoryAsync(
            Guid sessionId,
            long afterEntrySequence,
            int limit,
            CancellationToken cancellationToken = default)
        {
            Limits.Add(limit);
            return inner.ReadHistoryAsync(sessionId, afterEntrySequence, limit, cancellationToken);
        }

        public ValueTask<UserProfile?> LoadProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            inner.LoadProfileAsync(profileId, cancellationToken);

        public ValueTask SaveProfileAsync(UserProfile profile, long expectedRevision, CancellationToken cancellationToken = default) =>
            inner.SaveProfileAsync(profile, expectedRevision, cancellationToken);

        public ValueTask RecoverCrashedSessionsAsync(CancellationToken cancellationToken = default) =>
            inner.RecoverCrashedSessionsAsync(cancellationToken);
    }

    private sealed class CapturingModel(ILanguageModel inner) : ILanguageModel
    {
        public List<ModelRequest> Requests { get; } = [];

        public ModelCapabilities Capabilities => inner.Capabilities;

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await foreach (var item in inner.GenerateAsync(request, cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
    }

    private sealed class TextModel(params ModelGenerationEvent[] events) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(StreamingText: true, Cancellation: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var item in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
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
