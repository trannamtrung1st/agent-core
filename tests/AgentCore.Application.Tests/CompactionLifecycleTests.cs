using System.Runtime.CompilerServices;
using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class CompactionLifecycleTests
{
    private const string Legacy = "P4A1_LEGACY_FACT";
    private const string Goal = "P4A1_KEEP_GOAL";
    private const string Second = "P4A1_KEEP_SECOND";
    private const string Third = "P4A1_KEEP_THIRD";
    private const string Secret = "sk-livep4a1secretvalue01";

    private static readonly Guid SessionId = Guid.Parse("019944af-0006-7000-8000-0000000000c3");
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Completed_turn_compacts_incrementally_and_reopen_keeps_the_committed_summary()
    {
        var entries = History(80);
        var store = new InMemoryMemoryStore();
        var snapshot = Snapshot(entries);
        await store.SaveAsync(snapshot, 0);
        var model = new CountingModel(new ScriptedLanguageModel());
        await using (var runtime = Runtime(snapshot, store, model))
        {
            await runtime.AttachAsync();
            Assert.Equal(0, model.CompactionCalls);
            Assert.True(await runtime.SubmitPersistedUserTextAsync("continue", Guid.Parse("019944af-0006-7000-8000-0000000000d1")));
            await runtime.WaitUntilIdleAsync();
            Assert.Equal(60, runtime.Snapshot.SummarizedThroughEntrySequence);
            Assert.Equal(SummaryFormats.Semantic, runtime.Snapshot.SummaryFormatVersion);
            Assert.Contains(Legacy, runtime.Snapshot.Summary, StringComparison.Ordinal);
            Assert.Contains(Goal, runtime.Snapshot.Summary, StringComparison.Ordinal);
            Assert.Contains(Second, runtime.Snapshot.Summary, StringComparison.Ordinal);
            Assert.Contains(Third, runtime.Snapshot.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, runtime.Snapshot.Summary, StringComparison.Ordinal);
            Assert.True(model.CompactionCalls >= 2);
            var durable = (await store.LoadAsync(SessionId))!;
            Assert.Equal(runtime.Snapshot.SummarizedThroughEntrySequence, durable.SummarizedThroughEntrySequence);
        }

        var restored = (await store.LoadAsync(SessionId))!;
        Assert.Equal(60, restored.SummarizedThroughEntrySequence);
        Assert.Contains(Goal, restored.Summary, StringComparison.Ordinal);
        var rows = await store.ReadHistoryAsync(SessionId, 0, 200);
        Assert.Contains(rows, entry => entry.Text.Contains(Secret, StringComparison.Ordinal));
        Assert.Contains(rows, entry => entry.Text.Contains(Goal, StringComparison.Ordinal));

        var reopenedModel = new CountingModel(new ScriptedLanguageModel());
        await using var reopened = Runtime(restored, store, reopenedModel);
        await reopened.AttachAsync();
        Assert.True(await reopened.SubmitPersistedUserTextAsync("again", Guid.Parse("019944af-0006-7000-8000-0000000000d2")));
        await reopened.WaitUntilIdleAsync();
        Assert.Equal(60, reopened.Snapshot.SummarizedThroughEntrySequence);
        Assert.Equal(0, reopenedModel.CompactionCalls);
    }

    [Fact]
    public async Task User_turn_proceeds_while_compaction_is_running_and_reattach_does_not_duplicate_it()
    {
        var entries = History(42);
        var store = new InMemoryMemoryStore();
        var snapshot = Snapshot(entries);
        await store.SaveAsync(snapshot, 0);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new CountingModel(new ScriptedLanguageModel(
            compactionFixture: CompactionFixture.Late,
            compactionRelease: release,
            compactionStarted: started));
        var output = new CapturingSessionOutput();
        await using var runtime = Runtime(snapshot, store, model, output);
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitPersistedUserTextAsync("first", Guid.Parse("019944af-0006-7000-8000-0000000000d3")));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Legacy, runtime.Snapshot.Summary);

        Assert.True(await runtime.SubmitPersistedUserTextAsync("second", Guid.Parse("019944af-0006-7000-8000-0000000000d4")));
        await output.WaitForAsync(item =>
            item.Payload is TextDeltaOutput delta && delta.Text.Contains("synthetic", StringComparison.Ordinal)
            && output.Items.Count(candidate => candidate.Payload is TextDeltaOutput text && text.Text.Contains("synthetic", StringComparison.Ordinal)) >= 2);
        Assert.Equal(Legacy, runtime.Snapshot.Summary);
        Assert.Equal(1, model.CompactionCalls);

        await runtime.DetachAsync();
        Assert.True(await runtime.AttachAsync());
        Assert.Equal(1, model.CompactionCalls);
        Assert.Equal(Legacy, runtime.Snapshot.Summary);

        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.TestCompactionSettled = settled;
        release.TrySetResult();
        await settled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(1, model.CompactionCalls);
        Assert.Contains(Goal, runtime.Snapshot.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, runtime.Snapshot.Summary, StringComparison.Ordinal);
        Assert.Contains(runtime.Snapshot.Entries, entry => entry.Text == "second");
    }

    [Fact]
    public async Task Finalization_cannot_commit_a_late_compaction_result()
    {
        var entries = History(42);
        var store = new InMemoryMemoryStore();
        var snapshot = Snapshot(entries);
        await store.SaveAsync(snapshot, 0);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new CountingModel(new ScriptedLanguageModel(
            compactionFixture: CompactionFixture.Late,
            compactionRelease: release,
            compactionStarted: started));
        await using var runtime = Runtime(snapshot, store, model);
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitPersistedUserTextAsync("end-me", Guid.Parse("019944af-0006-7000-8000-0000000000d5")));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await runtime.RequestEndAsync());
        release.TrySetResult();
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(Legacy, runtime.Snapshot.Summary);
        Assert.Equal(0, runtime.Snapshot.SummarizedThroughEntrySequence);
        var saved = (await store.LoadAsync(SessionId))!;
        Assert.Equal(Legacy, saved.Summary);
        Assert.Equal(0, saved.SummarizedThroughEntrySequence);
    }

    [Fact]
    public async Task Short_history_and_pending_approval_do_not_compact()
    {
        var shortStore = new InMemoryMemoryStore();
        var shortSnapshot = Snapshot(History(8));
        await shortStore.SaveAsync(shortSnapshot, 0);
        var shortModel = new CountingModel(new ScriptedLanguageModel());
        await using (var runtime = Runtime(shortSnapshot, shortStore, shortModel))
        {
            await runtime.AttachAsync();
            Assert.True(await runtime.SubmitPersistedUserTextAsync("short", Guid.Parse("019944af-0006-7000-8000-0000000000d6")));
            await runtime.WaitUntilIdleAsync();
            Assert.Equal(0, shortModel.CompactionCalls);
            Assert.Equal(Legacy, runtime.Snapshot.Summary);
        }

        var entries = History(42);
        var store = new InMemoryMemoryStore();
        var snapshot = Snapshot(entries, definition: SensitiveDefinition());
        await store.SaveAsync(snapshot, 0);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new ApprovalHoldModel(release, started);
        var output = new CapturingSessionOutput();
        await using var approvalRuntime = Runtime(snapshot, store, model, output, new SessionToolExecutor());
        await approvalRuntime.AttachAsync();
        Assert.True(await approvalRuntime.SubmitPersistedUserTextAsync("first", Guid.Parse("019944af-0006-7000-8000-0000000000d7")));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await approvalRuntime.SubmitPersistedUserTextAsync("approve this", Guid.Parse("019944af-0006-7000-8000-0000000000d8")));
        var approval = await output.WaitForAsync(item => item.Payload is ApprovalRequestedOutput);
        var requested = (ApprovalRequestedOutput)approval.Payload!;
        Assert.NotNull(approvalRuntime.BuildPublicPendingApproval());
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        approvalRuntime.TestCompactionSettled = settled;
        release.TrySetResult();
        await settled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Legacy, approvalRuntime.Snapshot.Summary);
        Assert.Equal(0, approvalRuntime.Snapshot.SummarizedThroughEntrySequence);

        Assert.Equal(
            ResponseApprovalResult.Accepted,
            await approvalRuntime.RespondApprovalAsync(
                approvalRuntime.ActiveResponseId!.Value,
                requested.ApprovalId,
                ToolApprovalDecision.Approve));
        await approvalRuntime.WaitUntilIdleAsync();
    }

    [Fact]
    public async Task History_read_failure_releases_the_slot_and_a_later_turn_compacts()
    {
        var entries = History(45);
        var inner = new InMemoryMemoryStore();
        var snapshot = Snapshot(entries);
        await inner.SaveAsync(snapshot, 0);
        var before = await inner.ReadHistoryAsync(SessionId, 0, 200);
        var store = new ThrowOnceHistoryStore(inner);
        var model = new CountingModel(new ScriptedLanguageModel());
        await using var runtime = Runtime(snapshot, store, model);
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitPersistedUserTextAsync("first", Guid.Parse("019944af-0006-7000-8000-0000000000e1")));
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(Legacy, runtime.Snapshot.Summary);
        Assert.Equal(0, runtime.Snapshot.SummarizedThroughEntrySequence);
        Assert.Equal(0, model.CompactionCalls);
        var afterFailure = await inner.ReadHistoryAsync(SessionId, 0, 200);
        foreach (var entry in before)
        {
            var match = Assert.Single(afterFailure, row => row.EntryId == entry.EntryId);
            Assert.Equal(entry.Text, match.Text);
            Assert.Equal(entry.Sequence, match.Sequence);
        }

        Assert.True(await runtime.SubmitPersistedUserTextAsync("second", Guid.Parse("019944af-0006-7000-8000-0000000000e2")));
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(SummaryFormats.Semantic, runtime.Snapshot.SummaryFormatVersion);
        Assert.True(runtime.Snapshot.SummarizedThroughEntrySequence > 0);
        Assert.Contains(Goal, runtime.Snapshot.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, runtime.Snapshot.Summary, StringComparison.Ordinal);
        Assert.True(model.CompactionCalls >= 1);
        var afterRetry = await inner.ReadHistoryAsync(SessionId, 0, 200);
        Assert.Contains(afterRetry, row => row.Text.Contains(Secret, StringComparison.Ordinal));
        Assert.Contains(afterRetry, row => row.Text == "second");
    }

    private static SessionRuntime Runtime(
        SessionSnapshot snapshot,
        IMemoryStore store,
        ILanguageModel model,
        CapturingSessionOutput? output = null,
        SessionToolExecutor? tools = null)
    {
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 256).Select(index => Guid.Parse($"019944af-0007-7000-8000-{index:D12}")),
            [snapshot.SessionId]);
        return new SessionRuntime(
            snapshot,
            model,
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            output ?? new CapturingSessionOutput(),
            ids,
            new FakeTimeProvider(Now),
            NullLogger<SessionRuntime>.Instance,
            policy: new InteractionPolicy(PendingVoiceTimeoutMs: 30_000),
            tools: tools,
            voice: new VoiceAvailability { SpeechAdaptersResolved = true });
    }

    private static SessionSnapshot Snapshot(IReadOnlyList<ConversationEntry> entries, AgentDefinition? definition = null) =>
        new(
            1,
            SessionId,
            1,
            definition ?? SampleDefinitions.Examiner,
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
            var role = sequence % 2 == 0 ? ConversationRole.Assistant : ConversationRole.User;
            var text = sequence switch
            {
                2 => Goal,
                11 => Secret,
                22 => Second,
                42 => Third,
                _ => $"turn-{sequence}"
            };
            entries.Add(new ConversationEntry(
                Guid.Parse($"019944af-0006-7000-8000-{sequence:D12}"),
                sequence,
                null,
                role,
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

    private static AgentDefinition SensitiveDefinition() =>
        new(
            1,
            "general-assistant",
            3,
            new AgentIdentity("Test", "Role", "desc", "Tone"),
            [],
            "instructions",
            new BehaviorPolicy("answerNewTurn", true, true),
            new ConversationPolicy("balanced", false, "en", 2048),
            new InitiativePolicy(false, 60_000, 120_000, 1, ["longSilence"], 0),
            new VoiceConfiguration(false, "default", 1.0),
            new ProviderPreferences("primary-llm", "primary-stt", "primary-tts"),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new RoleEnvironment(ToolAllowlist: [ToolCatalog.DemoSensitiveAction]));

    private sealed class CountingModel(ScriptedLanguageModel inner) : ILanguageModel
    {
        public int CompactionCalls { get; private set; }

        public ModelCapabilities Capabilities => inner.Capabilities;

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (request.Messages.Any(message => message.Text.Contains(ConversationCompactor.Marker, StringComparison.Ordinal)))
            {
                CompactionCalls++;
            }

            await foreach (var item in inner.GenerateAsync(request, cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
    }

    private sealed class ApprovalHoldModel(TaskCompletionSource release, TaskCompletionSource started) : ILanguageModel
    {
        private int _turns;
        private int _compactions;

        public ModelCapabilities Capabilities { get; } = new(StreamingText: true, Cancellation: true, Tools: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (request.Messages.Any(message => message.Text.Contains(ConversationCompactor.Marker, StringComparison.Ordinal)))
            {
                _compactions++;
                if (_compactions == 1)
                {
                    started.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                yield return new ModelTextDelta("Summary: " + Goal);
                yield return new ModelCompleted(ModelStopReason.Completed);
                yield break;
            }

            _turns++;
            if (_turns == 1)
            {
                yield return new ModelTextDelta("Hello from synthetic.");
                yield return new ModelCompleted(ModelStopReason.Completed);
                yield break;
            }

            if (request.Messages.Any(message => message.Role == ModelRole.Tool))
            {
                yield return new ModelTextDelta("completed after approval");
                yield return new ModelCompleted(ModelStopReason.Completed);
                yield break;
            }

            yield return new ModelToolCallEvent(
                new ModelToolCall("call-sensitive", ToolCatalog.DemoSensitiveAction, "{}"));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }

    private sealed class ThrowOnceHistoryStore(IMemoryStore inner) : IMemoryStore
    {
        private int _remaining = 1;

        public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            inner.LoadAsync(sessionId, cancellationToken);

        public ValueTask SaveAsync(SessionSnapshot snapshot, long expectedRevision, CancellationToken cancellationToken = default) =>
            inner.SaveAsync(snapshot, expectedRevision, cancellationToken);

        public ValueTask<IReadOnlyList<ConversationEntry>> ReadHistoryAsync(
            Guid sessionId,
            long afterEntrySequence,
            int limit,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Decrement(ref _remaining) >= 0)
            {
                throw new InvalidOperationException("Synthetic compaction history read failed.");
            }

            return inner.ReadHistoryAsync(sessionId, afterEntrySequence, limit, cancellationToken);
        }

        public ValueTask<UserProfile?> LoadProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            inner.LoadProfileAsync(profileId, cancellationToken);

        public ValueTask SaveProfileAsync(UserProfile profile, long expectedRevision, CancellationToken cancellationToken = default) =>
            inner.SaveProfileAsync(profile, expectedRevision, cancellationToken);

        public ValueTask RecoverCrashedSessionsAsync(CancellationToken cancellationToken = default) =>
            inner.RecoverCrashedSessionsAsync(cancellationToken);
    }
}
