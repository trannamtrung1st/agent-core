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

public sealed class RichEnvelopeRuntimeTests
{
    [Fact]
    public async Task Display_receipt_does_not_mark_speech_heard_and_hides_undelivered_blocks()
    {
        var output = new CapturingSessionOutput();
        var store = new InMemoryMemoryStore();
        var model = new ScriptedLanguageModel(
            ["Hello[[speech:Spoken hello]][[md:**Hi**]][[artifact:fixture-artifact-1]][[xyz:nope]]"]);
        await using var runtime = Create(output, store, model);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await runtime.WaitUntilIdleAsync();
        var assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal("Hello", assistant.Text);
        Assert.Equal("Spoken hello", assistant.Envelope!.SpeechText);
        Assert.Equal(3, assistant.Envelope.Blocks.Count);
        Assert.Contains(assistant.Envelope.Blocks, block => block.Kind == ResponseBlockKind.Unknown);
        Assert.Equal(0, assistant.HeardTextEndExclusive);
        Assert.Equal(0, assistant.ReceivedTextEndExclusive);
        Assert.Empty(PublicHistory.FromEntry(assistant).Blocks);
        Assert.Equal(string.Empty, PublicHistory.FromEntry(assistant).Text);

        Assert.True(await runtime.SubmitReceiptAsync(assistant.ResponseId!.Value, assistant.Text.Length));
        await runtime.WaitUntilMailboxDrainedAsync();
        assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal("Hello", PublicHistory.FromEntry(assistant).Text);
        Assert.Equal("Spoken hello", PublicHistory.FromEntry(assistant).SpeechText);
        Assert.Equal(0, assistant.HeardTextEndExclusive);
        Assert.Empty(PublicHistory.FromEntry(assistant).Blocks);

        var blockIds = assistant.Envelope!.Blocks.Select(block => block.BlockId).ToArray();
        Assert.True(await runtime.SubmitReceiptAsync(assistant.ResponseId!.Value, assistant.Text.Length, blockIds: blockIds));
        await runtime.WaitUntilMailboxDrainedAsync();
        var publicEntry = PublicHistory.FromEntry(
            runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant));
        Assert.Equal(3, publicEntry.Blocks.Count);
        Assert.Equal("Spoken hello", publicEntry.SpeechText);
        Assert.Contains(publicEntry.Blocks, block => block.Kind == "artifact" && block.ArtifactId == "fixture-artifact-1");
        Assert.Contains(publicEntry.Blocks, block => block.Kind == "unknown");
    }

    [Fact]
    public async Task Unauthorized_artifact_and_unknown_blocks_fallback_without_exposing_id()
    {
        var output = new CapturingSessionOutput();
        var model = new ScriptedLanguageModel(["See [[artifact:secret-id]]"]);
        await using var runtime = Create(output, new InMemoryMemoryStore(), model);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await runtime.WaitUntilIdleAsync();
        var block = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant).Envelope!.Blocks.Single();
        Assert.Equal(ResponseBlockKind.Unknown, block.Kind);
        Assert.Null(block.ArtifactId);
        Assert.Equal(ResponseEnvelopeParser.UnauthorizedArtifactFallback, block.FallbackText);
    }

    [Fact]
    public async Task Disconnect_before_display_receipt_hides_generated_tail_on_reconnect()
    {
        var store = new InMemoryMemoryStore();
        var first = new CapturingSessionOutput();
        Guid sessionId;
        await using (var runtime = Create(first, store, new ScriptedLanguageModel()))
        {
            await runtime.AttachAsync();
            await runtime.SubmitUserTextAsync("Hello");
            await runtime.WaitUntilIdleAsync();
            sessionId = runtime.SessionId;
            await runtime.DetachAsync();
            await runtime.WaitUntilIdleAsync();
        }

        var restoredOutput = new CapturingSessionOutput();
        var paused = (await store.LoadAsync(sessionId))!;
        var loaded = await PausedSessionReopen.ReopenAsync(store, paused, new FakeTimeProvider(DateTimeOffset.UtcNow));
        await using var restored = Create(restoredOutput, store, new ScriptedLanguageModel(), loaded);
        await restored.AttachAsync();
        await restored.WaitUntilMailboxDrainedAsync();
        var ready = Assert.IsType<ReadyOutput>(restoredOutput.Items.Single(item => item.Payload is ReadyOutput).Payload);
        Assert.Equal(string.Empty, ready.Ready.History.Last(entry => entry.Role == ConversationRole.Assistant).Text);
    }

    [Fact]
    public async Task Speech_first_marker_allows_short_spoken_summary_of_long_display()
    {
        var output = new CapturingSessionOutput();
        const string spoken = "Câu chuyện kể về một quán phở nổi tiếng vì chuyện hài.";
        const string display = "Một hôm, chủ quán phở thử món phở xào mới. Khách ăn rất thích và hỏi vì sao quán không bán thường xuyên. " +
            "Chủ quán đùa rằng mỗi lần cắt hành để nấu món này, anh lại khóc đến mức không bán hàng được. " +
            "Từ đó khách gọi quán là Phở Khóc, và câu đùa ấy khiến cả khu phố biết đến quán.";
        var model = new ScriptedLanguageModel([$"[[speech:{spoken}]]{display}"]);
        var synthesizer = new RecordingSynthesizer();
        await using var runtime = Create(
            output,
            new InMemoryMemoryStore(),
            model,
            synthesizer: synthesizer,
            voice: true);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("Cho tôi một truyện hài dài nhưng nói tóm tắt thôi.");
        var final = await output.WaitForAsync(item => item.Payload is AudioFrameOutput frame && frame.IsFinal);
        Assert.Contains(synthesizer.Texts, text => text.Contains(spoken, StringComparison.Ordinal));
        Assert.DoesNotContain(synthesizer.Texts, text => text.Contains("Chủ quán đùa", StringComparison.Ordinal));
        await runtime.SubmitPlaybackAsync(final.ResponseId!.Value, "started", 0, 0);
        await runtime.SubmitPlaybackAsync(final.ResponseId!.Value, "completed", runtime.SentSamples, 0);
        await runtime.WaitUntilIdleAsync();
        var assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        var envelope = Assert.IsType<ResponseEnvelope>(assistant.Envelope);
        Assert.Equal(display, assistant.Text);
        Assert.Equal(spoken, envelope.SpeechText);
        Assert.True(assistant.Text.Length > envelope.SpeechText!.Length);
    }

    [Fact]
    public async Task Late_receipt_after_supersession_is_ignored()
    {
        var output = new CapturingSessionOutput();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new GatedDeltaLanguageModel(release);
        await using var runtime = Create(output, new InMemoryMemoryStore(), model);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput delta && delta.Text == "T1");
        var firstId = runtime.ActiveResponseId!.Value;
        Assert.True(await runtime.SubmitUserTextAsync("Next", behavior: UserTextBehavior.Interrupt));
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput delta && delta.Text == "T2");
        var first = runtime.Snapshot.Entries.Single(entry => entry.ResponseId == firstId);
        Assert.Equal(EntryStatus.Interrupted, first.Status);
        Assert.True(await runtime.SubmitReceiptAsync(firstId, first.Text.Length));
        await runtime.WaitUntilMailboxDrainedAsync();
        Assert.Equal(0, runtime.Snapshot.Entries.Single(entry => entry.ResponseId == firstId).ReceivedTextEndExclusive);
        release.TrySetResult();
        await runtime.WaitUntilMailboxDrainedAsync();
    }

    [Fact]
    public async Task Speech_credit_does_not_rewrite_display_receipts()
    {
        var output = new CapturingSessionOutput();
        var model = new ScriptedLanguageModel(["Hello[[speech:Spoken hello]]"]);
        var synthesizer = new RecordingSynthesizer();
        await using var runtime = Create(
            output,
            new InMemoryMemoryStore(),
            model,
            synthesizer: synthesizer,
            voice: true);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("Hello");
        var final = await output.WaitForAsync(item => item.Payload is AudioFrameOutput frame && frame.IsFinal);
        var assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(0, assistant.ReceivedTextEndExclusive);
        Assert.True(await runtime.SubmitReceiptAsync(assistant.ResponseId!.Value, assistant.Text.Length));
        await runtime.WaitUntilMailboxDrainedAsync();
        assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        var received = assistant.ReceivedTextEndExclusive;
        Assert.Equal(assistant.Text.Length, received);
        Assert.Equal(0, assistant.HeardTextEndExclusive);
        await runtime.SubmitPlaybackAsync(final.ResponseId!.Value, "started", 0, 0);
        await runtime.SubmitPlaybackAsync(final.ResponseId!.Value, "completed", runtime.SentSamples, 0);
        await runtime.WaitUntilIdleAsync();
        assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(received, assistant.ReceivedTextEndExclusive);
        Assert.True(assistant.HeardTextEndExclusive > 0);
        Assert.Equal("Hello", assistant.Text);
        var envelope = assistant.Envelope;
        Assert.NotNull(envelope);
        Assert.Equal("Spoken hello", envelope.SpeechText);
        Assert.True(assistant.HeardTextEndExclusive <= (envelope.SpeechText ?? string.Empty).Length);
    }

    [Fact]
    public async Task Attachment_reference_block_is_distinct_from_display_text()
    {
        var output = new CapturingSessionOutput();
        var model = new ScriptedLanguageModel(["See file.[[attachment:notes.txt]]"]);
        await using var runtime = Create(output, new InMemoryMemoryStore(), model);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await runtime.WaitUntilIdleAsync();
        var assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal("See file.", assistant.Text);
        Assert.Contains(assistant.Envelope!.Blocks, block => block.Kind == ResponseBlockKind.AttachmentReference && block.AttachmentId == "notes.txt");
        Assert.True(await runtime.SubmitReceiptAsync(assistant.ResponseId!.Value, assistant.Text.Length, blockIds: assistant.Envelope.Blocks.Select(block => block.BlockId).ToArray()));
        await runtime.WaitUntilMailboxDrainedAsync();
        var visible = PublicHistory.FromEntry(runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant));
        Assert.Equal("See file.", visible.Text);
        Assert.Contains(visible.Blocks, block => block.Kind == "attachment" && block.AttachmentId == "notes.txt");
    }

    [Fact]
    public async Task Ready_history_does_not_include_live_thinking_activity()
    {
        var store = new InMemoryMemoryStore();
        var first = new CapturingSessionOutput();
        Guid sessionId;
        await using (var runtime = Create(first, store, new ScriptedLanguageModel()))
        {
            await runtime.AttachAsync();
            await runtime.SubmitUserTextAsync("Hello");
            await runtime.WaitUntilIdleAsync();
            sessionId = runtime.SessionId;
            await runtime.DetachAsync();
            await runtime.WaitUntilIdleAsync();
        }

        var restoredOutput = new CapturingSessionOutput();
        var paused = (await store.LoadAsync(sessionId))!;
        var loaded = await PausedSessionReopen.ReopenAsync(store, paused, new FakeTimeProvider(DateTimeOffset.UtcNow));
        await using var restored = Create(restoredOutput, store, new ScriptedLanguageModel(), loaded);
        await restored.AttachAsync();
        await restored.WaitUntilMailboxDrainedAsync();
        var ready = Assert.IsType<ReadyOutput>(restoredOutput.Items.Single(item => item.Payload is ReadyOutput).Payload);
        Assert.DoesNotContain(ready.Ready.History, entry => entry.Text.Contains("Thinking", StringComparison.OrdinalIgnoreCase));
        Assert.All(ready.Ready.History, entry => Assert.NotEqual(EntryStatus.Streaming, entry.Status));
    }

    private static SessionRuntime Create(
        ISessionOutput output,
        InMemoryMemoryStore store,
        ILanguageModel model,
        SessionSnapshot? snapshot = null,
        ISpeechSynthesizer? synthesizer = null,
        bool voice = false)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var now = time.GetUtcNow();
        snapshot ??= new SessionSnapshot(
            1,
            ids.NewSessionId(),
            1,
            SampleDefinitions.Examiner,
            voice ? SessionMode.Text : SessionMode.Text,
            null,
            SessionStatus.Created,
            [],
            string.Empty,
            0,
            null,
            LocalUserProfile.Id,
            now,
            now);
        if (store.LoadAsync(snapshot.SessionId).AsTask().GetAwaiter().GetResult() is null)
        {
            store.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
        }

        return new SessionRuntime(
            snapshot,
            model,
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            recognizer: synthesizer is null ? null : new SyntheticSpeechRecognizer(),
            synthesizer: synthesizer);
    }

    private sealed class RecordingSynthesizer : ISpeechSynthesizer
    {
        private readonly SyntheticSpeechSynthesizer _inner = new();
        public List<string> Texts { get; } = [];
        public SynthesisCapabilities Capabilities => _inner.Capabilities;

        public IAsyncEnumerable<SpeechSynthesisEvent> SynthesizeAsync(
            SpeechRequest request,
            CancellationToken cancellationToken = default)
        {
            Texts.Add(request.Text);
            return _inner.SynthesizeAsync(request, cancellationToken);
        }
    }

    private sealed class GatedDeltaLanguageModel(TaskCompletionSource release) : ILanguageModel
    {
        private int _calls;

        public ModelCapabilities Capabilities { get; } = new(StreamingText: true, Cancellation: true, Tools: false);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            yield return new ModelTextDelta($"T{call}");
            await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }
}
