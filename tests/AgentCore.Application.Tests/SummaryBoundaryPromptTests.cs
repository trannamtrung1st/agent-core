using System.Diagnostics.Metrics;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

[Collection("telemetry-global")]
public sealed class SummaryBoundaryPromptTests
{
    private const string SummarySentinel = "P4A0_SUMMARY_SENTINEL";
    private const string BeforeWindowSentinel = "P4A0_BEFORE_WINDOW_SENTINEL";
    private const string BeforeBoundarySentinel = "P4A0_BEFORE_BOUNDARY_SENTINEL";
    private const string AfterBoundarySentinel = "P4A0_AFTER_BOUNDARY_SENTINEL";
    private const string CurrentSentinel = "P4A0_CURRENT_BATCH_SENTINEL";
    private const string QueuedSentinel = "P4A0_QUEUED_FUTURE_SENTINEL";
    private const string SteerSentinel = "P4A0_STEER_SENTINEL";
    private const string ReceivedSentinel = "P4A0_RECEIVED_PREFIX";
    private const string UndeliveredSentinel = "P4A0_UNDELIVERED_TAIL";
    private const string HeardSentinel = "P4A0_HEARD_PREFIX";
    private const string UnheardSentinel = "P4A0_UNHEARD_TAIL";
    private const string InterruptSentinel = "P4A0_INTERRUPT_REASON";

    private static readonly DateTimeOffset Now = new(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void No_summary_uses_bounded_raw_history_and_keeps_the_current_batch_once()
    {
        var history = new List<ConversationEntry>();
        for (var sequence = 1; sequence <= 24; sequence++)
        {
            history.Add(Entry(sequence, ConversationRole.Assistant, $"older-{sequence}"));
        }

        history.Add(Entry(25, ConversationRole.User, CurrentSentinel));
        var request = Build(history, summary: "", through: 0, last: 25);
        Assert.Contains(request.Messages, message => message.Role == ModelRole.System && message.Text.Contains("(none)", StringComparison.Ordinal));
        Assert.Equal(1, Count(request, CurrentSentinel));
        Assert.DoesNotContain(request.Messages, message => message.Text == "older-1");
        Assert.Contains(request.Messages, message => message.Text == "older-24");
    }

    [Fact]
    public void Valid_boundary_drops_raw_history_at_or_below_the_sequence_before_budgets()
    {
        var history = new List<ConversationEntry>();
        for (var sequence = 1; sequence <= 25; sequence++)
        {
            var text = sequence switch
            {
                10 => BeforeBoundarySentinel,
                20 => AfterBoundarySentinel,
                25 => CurrentSentinel,
                _ => $"turn-{sequence}"
            };
            var role = sequence == 25 ? ConversationRole.User : ConversationRole.Assistant;
            history.Add(Entry(sequence, role, text));
        }

        var request = Build(history, SummarySentinel, through: 15, last: 25);
        Assert.Contains(request.Messages, message => message.Role == ModelRole.System && message.Text.Contains(SummarySentinel, StringComparison.Ordinal));
        Assert.Equal(0, Count(request, BeforeBoundarySentinel));
        Assert.Equal(1, Count(request, AfterBoundarySentinel));
        Assert.Equal(1, Count(request, CurrentSentinel));
        Assert.Contains(request.Messages, message => message.Text.Contains("turn-16", StringComparison.Ordinal));
    }

    [Fact]
    public void Invalid_boundary_fails_open_for_the_current_batch_without_content_in_telemetry()
    {
        var history = new List<ConversationEntry>
        {
            Entry(1, ConversationRole.Assistant, BeforeBoundarySentinel),
            Entry(2, ConversationRole.User, CurrentSentinel)
        };
        var kinds = new List<string>();
        var details = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == RuntimeTelemetry.Name && instrument.Name == "dropped_items")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                var value = tag.Value?.ToString() ?? "";
                details.Add(value);
                if (tag.Key == "kind")
                {
                    kinds.Add(value);
                }
            }
        });
        listener.Start();
        var request = Build(history, SummarySentinel, through: 2, last: 2);
        listener.Dispose();

        Assert.Equal(1, Count(request, BeforeBoundarySentinel));
        Assert.Equal(1, Count(request, CurrentSentinel));
        Assert.Contains(SummaryBoundary.RejectedKind, kinds);
        Assert.DoesNotContain(details, value => value.Contains(BeforeBoundarySentinel, StringComparison.Ordinal));
        Assert.DoesNotContain(details, value => value.Contains(CurrentSentinel, StringComparison.Ordinal));
        Assert.DoesNotContain(details, value => value.Contains(SummarySentinel, StringComparison.Ordinal));
    }

    [Fact]
    public void Interrupted_text_and_voice_prompts_use_delivered_prefixes_only()
    {
        var received = ReceivedSentinel + " " + UndeliveredSentinel;
        var heard = HeardSentinel + " " + UnheardSentinel;
        var history = new List<ConversationEntry>
        {
            Entry(
                1,
                ConversationRole.Assistant,
                received,
                EntryStatus.Interrupted,
                SessionMode.Text,
                receivedEnd: ReceivedSentinel.Length,
                interruptReason: InterruptSentinel),
            Entry(
                2,
                ConversationRole.Assistant,
                heard,
                EntryStatus.Interrupted,
                SessionMode.Voice,
                heardEnd: HeardSentinel.Length,
                interruptReason: InterruptSentinel),
            Entry(3, ConversationRole.User, CurrentSentinel)
        };
        var request = Build(history, summary: "", through: 0, last: 3);
        var transcript = string.Join("\n", request.Messages.Where(message => message.Role != ModelRole.System).Select(message => message.Text));
        Assert.Contains(ReceivedSentinel, transcript, StringComparison.Ordinal);
        Assert.Contains(HeardSentinel, transcript, StringComparison.Ordinal);
        Assert.DoesNotContain(UndeliveredSentinel, transcript, StringComparison.Ordinal);
        Assert.DoesNotContain(UnheardSentinel, transcript, StringComparison.Ordinal);
        Assert.DoesNotContain(InterruptSentinel, transcript, StringComparison.Ordinal);
        Assert.Equal(1, Count(request, CurrentSentinel));
    }

    [Fact]
    public void Evaluation_requests_carry_a_valid_summary_and_omit_covered_raw_turns()
    {
        const string goal = "P4A0_GOAL_SENTINEL";
        var history = new List<ConversationEntry>
        {
            Entry(10, ConversationRole.User, goal),
            Entry(11, ConversationRole.Assistant, "acknowledged"),
            Entry(12, ConversationRole.User, "later detail")
        };
        var context = new AgentContext(
            SampleDefinitions.Examiner,
            history,
            goal,
            null,
            SessionMode.Text,
            null,
            false,
            null,
            new AgentTrigger(Guid.Parse("019944af-0004-7000-8000-000000000091"), TriggerKind.LongSilence, null),
            UtcNow: Now,
            LastUserActivityAt: Now.AddMinutes(-2),
            SummarizedThroughEntrySequence: 10,
            LastEntrySequence: 12);
        AssertSummaryCarried(
            InitiativeEvaluator.CreateEvaluationRequest(context, new PromptContextBuilder()),
            goal);
        AssertSummaryCarried(
            CompletionEvaluator.CreateEvaluationRequest(Snapshot(history, goal, 10), Now),
            goal);
    }

    [Fact]
    public void Invalid_boundary_keeps_raw_turns_in_evaluation_requests()
    {
        const string goal = "P4A0_GOAL_SENTINEL";
        var history = new List<ConversationEntry>
        {
            Entry(10, ConversationRole.User, goal),
            Entry(11, ConversationRole.Assistant, "acknowledged"),
            Entry(12, ConversationRole.User, "later detail")
        };
        var context = new AgentContext(
            SampleDefinitions.Examiner,
            history,
            goal,
            null,
            SessionMode.Text,
            null,
            false,
            null,
            new AgentTrigger(Guid.Parse("019944af-0004-7000-8000-000000000092"), TriggerKind.LongSilence, null),
            UtcNow: Now,
            LastUserActivityAt: Now.AddMinutes(-2),
            SummarizedThroughEntrySequence: 12,
            LastEntrySequence: 12);
        AssertRawHistoryFailOpen(
            InitiativeEvaluator.CreateEvaluationRequest(context, new PromptContextBuilder()),
            goal);
        AssertRawHistoryFailOpen(
            CompletionEvaluator.CreateEvaluationRequest(Snapshot(history, goal, 12), Now),
            goal);
    }

    [Fact]
    public async Task Live_and_sqlite_reopen_share_the_boundary_and_do_not_load_the_full_transcript()
    {
        var snapshot = LongSnapshot();
        var memory = new InMemoryMemoryStore();
        await memory.SaveAsync(snapshot, 0);
        var live = await FirstPromptAsync(memory, snapshot.SessionId);
        Assert.Equal(0, Count(live.Request, BeforeWindowSentinel));
        Assert.Equal(0, Count(live.Request, BeforeBoundarySentinel));
        Assert.Equal(1, Count(live.Request, AfterBoundarySentinel));
        Assert.Equal(1, Count(live.Request, CurrentSentinel));
        Assert.Contains(live.Request.Messages, message => message.Text.Contains(SummarySentinel, StringComparison.Ordinal));
        Assert.DoesNotContain(live.RuntimeEntries, entry => entry.Text.Contains(BeforeWindowSentinel, StringComparison.Ordinal));
        Assert.Equal(15, live.Through);
        var durable = await memory.ReadHistoryAsync(snapshot.SessionId, 0, 100);
        Assert.Contains(durable, entry => entry.Text.Contains(BeforeWindowSentinel, StringComparison.Ordinal));

        var path = Path.Combine(Path.GetTempPath(), $"agent-core-p4a0-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new SqliteFactory(options);
        var sqlite = new SqliteMemoryStore(factory, new FakeTimeProvider(Now));
        try
        {
            await sqlite.EnsureCreatedAsync();
            await sqlite.SaveAsync(snapshot, 0);
            var reopened = await FirstPromptAsync(sqlite, snapshot.SessionId);
            Assert.Equal(Messages(live.Request), Messages(reopened.Request));
            Assert.Equal(15, reopened.Through);
            Assert.DoesNotContain(reopened.RuntimeEntries, entry => entry.Text.Contains(BeforeWindowSentinel, StringComparison.Ordinal));
            var sqliteHistory = await sqlite.ReadHistoryAsync(snapshot.SessionId, 0, 100);
            Assert.Contains(sqliteHistory, entry => entry.Text.Contains(BeforeWindowSentinel, StringComparison.Ordinal));
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
    public async Task Reattach_does_not_advance_or_duplicate_the_boundary()
    {
        var snapshot = LongSnapshot();
        var store = new InMemoryMemoryStore();
        await store.SaveAsync(snapshot, 0);
        var loaded = (await store.LoadAsync(snapshot.SessionId))!;
        await using var runtime = CreateRuntime(loaded, store, new RecordingLanguageModel(new ScriptedLanguageModel()), new FakeTimeProvider(Now));
        await runtime.AttachAsync();
        await runtime.SubmitPersistedUserTextAsync(CurrentSentinel, Guid.Parse("019944af-0004-7000-8000-000000000031"));
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(15, runtime.Snapshot.SummarizedThroughEntrySequence);
        await runtime.DetachAsync();
        await runtime.WaitUntilIdleAsync();
        var paused = await store.LoadAsync(snapshot.SessionId);
        Assert.NotNull(paused);
        Assert.Equal(15, paused.SummarizedThroughEntrySequence);
        Assert.Equal(SummarySentinel, paused.Summary);

        var model = new RecordingLanguageModel(new ScriptedLanguageModel());
        await using var restored = CreateRuntime(paused, store, model, new FakeTimeProvider(Now));
        await restored.AttachAsync();
        await restored.SubmitPersistedUserTextAsync("P4A0_REATTACH_SENTINEL", Guid.Parse("019944af-0004-7000-8000-000000000032"));
        await restored.WaitUntilIdleAsync();
        var request = model.LastRequest!;
        Assert.Equal(15, restored.Snapshot.SummarizedThroughEntrySequence);
        Assert.Equal(0, Count(request, BeforeBoundarySentinel));
        Assert.Equal(1, Count(request, AfterBoundarySentinel));
        Assert.Equal(1, Count(request, "P4A0_REATTACH_SENTINEL"));
        Assert.Equal(SummarySentinel, restored.Snapshot.Summary);
    }

    [Fact]
    public async Task Queue_stays_out_of_the_active_request_and_steer_omits_the_undelivered_tail()
    {
        var history = new List<ConversationEntry>
        {
            Entry(1, ConversationRole.User, "earlier"),
            Entry(2, ConversationRole.Assistant, BeforeBoundarySentinel),
            Entry(3, ConversationRole.User, "middle"),
            Entry(4, ConversationRole.Assistant, AfterBoundarySentinel)
        };
        var snapshot = Snapshot(history, SummarySentinel, 2);
        var store = new InMemoryMemoryStore();
        await store.SaveAsync(snapshot, 0);
        var loaded = (await store.LoadAsync(snapshot.SessionId))!;
        var output = new CapturingSessionOutput();
        var model = new HoldingModel();
        await using var runtime = CreateRuntime(loaded, store, model, new FakeTimeProvider(Now), output);
        await runtime.AttachAsync();
        await runtime.SubmitPersistedUserTextAsync("continue", Guid.Parse("019944af-0004-7000-8000-000000000041"));
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput delta && delta.Text.Contains(ReceivedSentinel, StringComparison.Ordinal));
        Assert.Single(model.Requests);
        Assert.Equal(0, Count(model.Requests[0], QueuedSentinel));
        Assert.Equal(0, Count(model.Requests[0], BeforeBoundarySentinel));
        Assert.Equal(1, Count(model.Requests[0], AfterBoundarySentinel));

        await runtime.SubmitPersistedUserTextAsync(
            QueuedSentinel,
            Guid.Parse("019944af-0004-7000-8000-000000000042"),
            behavior: UserTextBehavior.Queue);
        Assert.Single(model.Requests);

        await runtime.SubmitPersistedUserTextAsync(
            SteerSentinel,
            Guid.Parse("019944af-0004-7000-8000-000000000043"));
        await output.WaitForAsync(_ => model.Requests.Count >= 2);
        var steered = model.Requests[1];
        Assert.Equal(1, Count(steered, SteerSentinel));
        Assert.Equal(0, Count(steered, UndeliveredSentinel));
        Assert.Equal(0, Count(steered, BeforeBoundarySentinel));
        Assert.Contains(steered.Messages, message => message.Text.Contains(SummarySentinel, StringComparison.Ordinal));
        model.Release.TrySetResult();
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(2, runtime.Snapshot.SummarizedThroughEntrySequence);
    }

    private static ModelRequest Build(IReadOnlyList<ConversationEntry> history, string summary, long through, long last)
    {
        var context = new AgentContext(
            SampleDefinitions.Examiner,
            history,
            summary,
            null,
            SessionMode.Text,
            null,
            false,
            null,
            new AgentTrigger(Guid.Parse("019944af-0004-7000-8000-000000000001"), TriggerKind.UserTurn, CurrentSentinel),
            SummarizedThroughEntrySequence: through,
            LastEntrySequence: last);
        return new PromptContextBuilder().Build(context, Guid.Parse("019944af-0004-7000-8000-000000000002"));
    }

    private static async Task<PromptObservation> FirstPromptAsync(IMemoryStore store, Guid sessionId)
    {
        var loaded = (await store.LoadAsync(sessionId))!;
        var model = new RecordingLanguageModel(new ScriptedLanguageModel());
        var runtime = CreateRuntime(loaded, store, model, new FakeTimeProvider(Now));
        await using (runtime)
        {
            await runtime.AttachAsync();
            await runtime.SubmitPersistedUserTextAsync(CurrentSentinel, Guid.Parse("019944af-0004-7000-8000-000000000031"));
            await runtime.WaitUntilIdleAsync();
            return new PromptObservation(
                model.LastRequest!,
                runtime.Snapshot.Entries.ToArray(),
                runtime.Snapshot.SummarizedThroughEntrySequence,
                loaded);
        }
    }

    private static SessionSnapshot LongSnapshot()
    {
        var entries = new List<ConversationEntry>();
        for (var sequence = 1; sequence <= 30; sequence++)
        {
            var role = sequence % 2 == 1 ? ConversationRole.User : ConversationRole.Assistant;
            var text = sequence switch
            {
                1 => BeforeWindowSentinel,
                12 => BeforeBoundarySentinel,
                16 => AfterBoundarySentinel,
                _ => $"turn-{sequence}"
            };
            entries.Add(Entry(sequence, role, text));
        }

        return Snapshot(entries, SummarySentinel, 15);
    }

    private static SessionSnapshot Snapshot(IReadOnlyList<ConversationEntry> entries, string summary, long through) =>
        new(
            1,
            Guid.Parse("019944af-0004-7000-8000-0000000000aa"),
            1,
            SampleDefinitions.Examiner,
            SessionMode.Text,
            null,
            SessionStatus.Created,
            entries,
            summary,
            through,
            null,
            null,
            Now,
            Now,
            LastEntrySequence: entries.Max(entry => entry.Sequence));

    private static ConversationEntry Entry(
        long sequence,
        ConversationRole role,
        string text,
        EntryStatus status = EntryStatus.Completed,
        SessionMode mode = SessionMode.Text,
        int? receivedEnd = null,
        int? heardEnd = null,
        string? interruptReason = null)
    {
        var received = receivedEnd ?? (mode == SessionMode.Text ? text.Length : 0);
        var heard = heardEnd ?? (mode == SessionMode.Voice ? text.Length : 0);
        return new ConversationEntry(
            Guid.Parse($"019944af-0004-7000-8000-{sequence:D12}"),
            sequence,
            null,
            role,
            text,
            null,
            status,
            mode,
            heard,
            received,
            Now.AddSeconds(sequence),
            InterruptReason: interruptReason);
    }

    private static SessionRuntime CreateRuntime(
        SessionSnapshot snapshot,
        IMemoryStore store,
        ILanguageModel model,
        FakeTimeProvider time,
        CapturingSessionOutput? output = null)
    {
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-0005-7000-8000-{index:D12}")),
            [snapshot.SessionId]);
        return new SessionRuntime(
            snapshot,
            model,
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            output ?? new CapturingSessionOutput(),
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            policy: new InteractionPolicy(PendingVoiceTimeoutMs: 30_000),
            voice: new VoiceAvailability { SpeechAdaptersResolved = true });
    }

    private static void AssertSummaryCarried(ModelRequest request, string goal)
    {
        using var doc = JsonDocument.Parse(UserPayload(request));
        var root = doc.RootElement;
        var summary = root.GetProperty("sessionSummary");
        Assert.Equal(SummaryBoundary.RememberedDataLabel, summary.GetProperty("label").GetString());
        Assert.Equal(goal, summary.GetProperty("text").GetString());
        Assert.DoesNotContain(RecentTexts(root), text => text.Contains(goal, StringComparison.Ordinal));
    }

    private static void AssertRawHistoryFailOpen(ModelRequest request, string goal)
    {
        using var doc = JsonDocument.Parse(UserPayload(request));
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("sessionSummary").ValueKind);
        Assert.Contains(RecentTexts(root), text => text.Contains(goal, StringComparison.Ordinal));
    }

    private static string UserPayload(ModelRequest request) =>
        request.Messages.Last(message => message.Role == ModelRole.User).Text;

    private static IReadOnlyList<string> RecentTexts(JsonElement root) =>
        root.GetProperty("recentTurns")
            .EnumerateArray()
            .Select(turn => turn.GetProperty("text").GetString() ?? "")
            .ToArray();

    private static int Count(ModelRequest request, string sentinel) =>
        request.Messages.Count(message => message.Text.Contains(sentinel, StringComparison.Ordinal));

    private static IReadOnlyList<(ModelRole Role, string Text)> Messages(ModelRequest request) =>
        request.Messages.Select(message => (message.Role, message.Text)).ToArray();

    private sealed record PromptObservation(
        ModelRequest Request,
        IReadOnlyList<ConversationEntry> RuntimeEntries,
        long Through,
        SessionSnapshot Loaded);

    private sealed class SqliteFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class HoldingModel : ILanguageModel
    {
        private readonly List<ModelRequest> _requests = [];

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<ModelRequest> Requests => _requests;

        public ModelCapabilities Capabilities { get; } = new(StreamingText: true, Cancellation: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _requests.Add(request);
            yield return new ModelTextDelta(ReceivedSentinel);
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            yield return new ModelTextDelta(UndeliveredSentinel);
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }
}
