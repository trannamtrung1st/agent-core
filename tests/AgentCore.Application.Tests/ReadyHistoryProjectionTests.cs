using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class ReadyHistoryProjectionTests
{
    [Fact]
    public async Task Attach_ready_uses_durable_newest_page_when_runtime_restore_window_is_bounded()
    {
        var store = new InMemoryMemoryStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var output = new CapturingSessionOutput();
        var sessionId = Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842");
        var now = time.GetUtcNow();
        var allEntries = Enumerable.Range(1, 500)
            .Select(sequence => Entry(sequence, now))
            .ToArray();
        var durable = new SessionSnapshot(
            1,
            sessionId,
            1,
            SampleDefinitions.Examiner,
            SessionMode.Text,
            null,
            SessionStatus.Created,
            allEntries,
            string.Empty,
            500,
            null,
            null,
            now,
            now, AgentInstanceId: Guid.NewGuid());
        await store.SaveAsync(durable, 0);

        var windowed = HistoryRestoreWindow.Select(allEntries);
        Assert.True(windowed.Count < 50);

        var runtimeSnapshot = durable with
        {
            Entries = windowed,
            Status = SessionStatus.Created
        };

        await using var runtime = Create(output, store, time, runtimeSnapshot);
        await runtime.AttachAsync();
        await runtime.WaitUntilMailboxDrainedAsync();

        var ready = Assert.IsType<ReadyOutput>(output.Items.Single(item => item.Payload is ReadyOutput).Payload);
        Assert.Equal(50, ready.Ready.History.Count);
        Assert.Equal(451, ready.Ready.History[0].Sequence);
        Assert.Equal(500, ready.Ready.History[^1].Sequence);
        Assert.True(ready.Ready.HasOlderHistory);
        Assert.Equal(451, ready.Ready.HistoryBeforeSequence);
    }

    [Fact]
    public async Task Ready_history_overlays_published_streaming_prefix_ahead_of_durable_row()
    {
        var inner = new InMemoryMemoryStore();
        var store = new LaggingAssistantPersistStore(inner);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var output = new CapturingSessionOutput();
        var model = new TwoPhaseStreamModel();
        await using var runtime = Create(output, store, time, model: model);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput);
        await runtime.WaitUntilMailboxDrainedAsync();
        await store.StreamingAssistantPersisted.Task;
        var assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        var responseId = assistant.ResponseId!.Value;
        Assert.Equal("T1", assistant.Text);

        var durableRow = (await inner.LoadAsync(runtime.SessionId))!
            .Entries
            .Single(entry => entry.EntryId == assistant.EntryId);
        Assert.True(durableRow.Text.Length < assistant.Text.Length);
        Assert.Equal(0, durableRow.ReceivedTextEndExclusive);

        await runtime.TransportDetachAsync();
        await runtime.WaitUntilMailboxDrainedAsync();

        Assert.True(await runtime.AttachAsync());
        await runtime.WaitUntilMailboxDrainedAsync();
        var ready = Assert.IsType<ReadyOutput>(output.Items.Last(item => item.Payload is ReadyOutput).Payload);
        var streaming = Assert.Single(ready.Ready.History, entry => entry.ResponseId == responseId);
        Assert.Equal(EntryStatus.Streaming, streaming.Status);
        Assert.Equal("T1", streaming.Text);
        Assert.Equal(0, streaming.ReceivedTextEndExclusive);
        Assert.DoesNotContain("T2", streaming.Text);

        model.AfterFirstDelta.TrySetResult();
        var nextDelta = await output.WaitForAsync(item =>
            item.Payload is TextDeltaOutput delta
            && item.ResponseId == responseId
            && delta.TextStart == streaming.Text.Length);
        Assert.Equal("T2", Assert.IsType<TextDeltaOutput>(nextDelta.Payload).Text);
    }

    private static ConversationEntry Entry(long sequence, DateTimeOffset now)
    {
        var entryId = Guid.Parse($"019944af-0000-7000-8000-{sequence:D12}");
        var isUser = sequence % 2 == 1;
        var text = $"History seed {sequence}";
        return new ConversationEntry(
            entryId,
            sequence,
            isUser ? entryId : null,
            isUser ? ConversationRole.User : ConversationRole.Assistant,
            text,
            isUser ? null : Guid.Parse($"019944af-0001-7000-8000-{sequence:D12}"),
            EntryStatus.Completed,
            SessionMode.Text,
            text.Length,
            text.Length,
            now);
    }

    private static SessionRuntime Create(
        ISessionOutput output,
        IMemoryStore store,
        FakeTimeProvider time,
        SessionSnapshot? snapshot = null,
        ILanguageModel? model = null)
    {
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [snapshot?.SessionId ?? Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var now = time.GetUtcNow();
        snapshot ??= new SessionSnapshot(
            1,
            ids.NewSessionId(),
            1,
            SampleDefinitions.Examiner,
            SessionMode.Text,
            null,
            SessionStatus.Created,
            [],
            string.Empty,
            0,
            null,
            null,
            now,
            now, AgentInstanceId: Guid.NewGuid());
        if (store.LoadAsync(snapshot.SessionId).AsTask().GetAwaiter().GetResult() is null)
        {
            store.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
        }

        return SessionRuntimeFixture.Create(
            snapshot,
            model ?? new ScriptedLanguageModel(),
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance);
    }
}

file sealed class LaggingAssistantPersistStore(InMemoryMemoryStore inner) : IMemoryStore
{
    public TaskCompletionSource StreamingAssistantPersisted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        inner.LoadAsync(sessionId, cancellationToken);

    public async ValueTask SaveAsync(SessionSnapshot snapshot, long expectedRevision, CancellationToken cancellationToken = default)
    {
        var entries = snapshot.Entries
            .Select(entry => entry is { Role: ConversationRole.Assistant, Status: EntryStatus.Streaming } && entry.Text.Length > 0
                ? entry with { Text = entry.Text[..1], ReceivedTextEndExclusive = 0 }
                : entry)
            .ToArray();
        await inner.SaveAsync(snapshot with { Entries = entries }, expectedRevision, cancellationToken);
        if (entries.Any(entry =>
                entry.Role == ConversationRole.Assistant
                && entry.Status == EntryStatus.Streaming
                && entry.Text.Length > 0))
        {
            StreamingAssistantPersisted.TrySetResult();
        }
    }

    public ValueTask<IReadOnlyList<ConversationEntry>> ReadHistoryAsync(
        Guid sessionId,
        long afterEntrySequence,
        int limit,
        CancellationToken cancellationToken = default) =>
        inner.ReadHistoryAsync(sessionId, afterEntrySequence, limit, cancellationToken);

    public ValueTask<ConversationHistoryPage?> ReadHistoryPageAsync(
        Guid sessionId,
        long? afterEntrySequence,
        long? beforeEntrySequence,
        int limit,
        CancellationToken cancellationToken = default) =>
        inner.ReadHistoryPageAsync(sessionId, afterEntrySequence, beforeEntrySequence, limit, cancellationToken);

    public ValueTask<UserProfile?> LoadProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        inner.LoadProfileAsync(profileId, cancellationToken);

    public ValueTask SaveProfileAsync(UserProfile profile, long expectedRevision, CancellationToken cancellationToken = default) =>
        inner.SaveProfileAsync(profile, expectedRevision, cancellationToken);

    public ValueTask RecoverCrashedSessionsAsync(CancellationToken cancellationToken = default) =>
        inner.RecoverCrashedSessionsAsync(cancellationToken);
}

file sealed class TwoPhaseStreamModel : ILanguageModel
{
    public TaskCompletionSource AfterFirstDelta { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ModelCapabilities Capabilities { get; } = new(true, true);

    public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
        ModelRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new ModelTextDelta("T1");
        await AfterFirstDelta.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        yield return new ModelTextDelta("T2");
        yield return new ModelCompleted(ModelStopReason.Completed);
    }
}
