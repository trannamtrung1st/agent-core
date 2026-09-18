using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class SpeechTranscriptDurabilityTests
{
    [Fact]
    public async Task Transcript_final_is_not_published_until_voice_user_turn_persists()
    {
        var output = new CapturingSessionOutput();
        var persistGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new GatedVoicePersistStore(persistGate);
        await using var runtime = Create(output, new SyntheticSpeechRecognizer(["hello from voice"]), store);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);

        var utterance = Guid.Parse("019944af-0000-7000-8000-0000000000e1");
        await runtime.SubmitSpeechAsync(new SpeechStarted(utterance), 0.9);
        await runtime.SubmitSpeechAsync(new SpeechFinal(utterance, "hello from voice", 0.9), 0.9);
        await store.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.DoesNotContain(output.Items, item => item.Payload is TranscriptFinalOutput);

        persistGate.TrySetResult();
        var final = await output.WaitForAsync(item => item.Payload is TranscriptFinalOutput);
        var payload = Assert.IsType<TranscriptFinalOutput>(final.Payload);
        Assert.Equal("hello from voice", payload.Text);
        Assert.NotEqual(Guid.Empty, payload.EntryId);
        Assert.True(payload.EntrySequence > 0);

        var history = await store.ReadHistoryAsync(runtime.SessionId, 0, 20).AsTask();
        Assert.Contains(history, entry => entry.Role == ConversationRole.User && entry.Text == "hello from voice");
        await runtime.WaitUntilIdleAsync();
    }

    [Fact]
    public async Task Discarded_empty_speech_final_emits_transcript_discarded_without_user_entry()
    {
        var output = new CapturingSessionOutput();
        await using var runtime = Create(output, new SyntheticSpeechRecognizer(), new InMemoryMemoryStore());
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);

        var utterance = Guid.Parse("019944af-0000-7000-8000-0000000000e2");
        await runtime.SubmitSpeechAsync(new SpeechStarted(utterance), 0.9);
        await runtime.SubmitSpeechAsync(new SpeechFinal(utterance, "   ", 0.9), 0.9);
        await runtime.WaitUntilMailboxDrainedAsync();

        Assert.Contains(output.Items, item => item.Payload is TranscriptDiscardedOutput);
        Assert.DoesNotContain(output.Items, item => item.Payload is TranscriptFinalOutput);
        Assert.DoesNotContain(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.User);
        await runtime.WaitUntilIdleAsync();
    }

    private static SessionRuntime Create(
        ISessionOutput output,
        ISpeechRecognizer recognizer,
        IMemoryStore store)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var now = time.GetUtcNow();
        var snapshot = new SessionSnapshot(
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
            now);
        store.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
        return new SessionRuntime(
            snapshot,
            new ScriptedLanguageModel(),
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            recognizer: recognizer);
    }

    private sealed class GatedVoicePersistStore(TaskCompletionSource persistGate) : IMemoryStore
    {
        private readonly InMemoryMemoryStore _inner = new();

        public TaskCompletionSource SaveStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            _inner.LoadAsync(sessionId, cancellationToken);

        public async ValueTask SaveAsync(
            SessionSnapshot snapshot,
            long expectedRevision,
            CancellationToken cancellationToken = default)
        {
            if (snapshot.Entries.Any(entry => entry.Role == ConversationRole.User && entry.Text == "hello from voice"))
            {
                SaveStarted.TrySetResult();
                await persistGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            await _inner.SaveAsync(snapshot, expectedRevision, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<IReadOnlyList<ConversationEntry>> ReadHistoryAsync(
            Guid sessionId,
            long afterEntrySequence,
            int limit,
            CancellationToken cancellationToken = default) =>
            _inner.ReadHistoryAsync(sessionId, afterEntrySequence, limit, cancellationToken);

        public ValueTask<UserProfile?> LoadProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
            _inner.LoadProfileAsync(profileId, cancellationToken);

        public ValueTask SaveProfileAsync(UserProfile profile, long expectedRevision, CancellationToken cancellationToken = default) =>
            _inner.SaveProfileAsync(profile, expectedRevision, cancellationToken);

        public ValueTask RecoverCrashedSessionsAsync(CancellationToken cancellationToken = default) =>
            _inner.RecoverCrashedSessionsAsync(cancellationToken);
    }
}
