using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class SemanticCutoverRuntimeTests
{
    [Fact]
    public async Task Text_generation_attaches_speech_will_be_used_false()
    {
        var recorder = new RecordingLanguageModel();
        await using var runtime = Create(new CapturingSessionOutput(), recorder);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await runtime.WaitUntilIdleAsync();
        Assert.NotNull(recorder.LastRequest?.ResponseContract);
        Assert.False(recorder.LastRequest!.ResponseContract!.SpeechWillBeUsed);
        Assert.Equal("Shown", runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant).Text);
    }

    [Fact]
    public async Task Voice_generation_attaches_speech_will_be_used_true()
    {
        var recorder = new RecordingLanguageModel();
        var output = new CapturingSessionOutput();
        await using var runtime = Create(output, recorder, voice: true);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("Hello");
        await runtime.WaitUntilIdleAsync();
        Assert.True(recorder.LastRequest!.ResponseContract!.SpeechWillBeUsed);
    }

    [Fact]
    public async Task Invalid_native_json_fails_without_envelope()
    {
        var output = new CapturingSessionOutput();
        var model = new NativeJsonLanguageModel("""{"nope":true}""", structured: true);
        await using var runtime = Create(output, model);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await runtime.WaitUntilIdleAsync();
        var assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Failed, assistant.Status);
        Assert.Null(assistant.Envelope);
        Assert.DoesNotContain("{", assistant.Text, StringComparison.Ordinal);
        Assert.Contains(
            output.Items,
            item => item.Payload is ResponseProgressOutput progress
                && progress.Kind == ResponseProgressKind.Finalizing
                && progress.State == ResponseProgressState.Started);
    }

    [Fact]
    public async Task Stale_semantic_ready_is_ignored()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new GatedSemanticLanguageModel(release);
        await using var runtime = Create(new CapturingSessionOutput(), model);
        await runtime.AttachAsync();
        var send = runtime.SubmitUserTextAsync("Hello");
        await model.Started.Task;
        await runtime.SubmitUserTextAsync("Second");
        release.TrySetResult();
        await send;
        await runtime.WaitUntilIdleAsync();
        var last = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.NotEqual("stale", last.Text);
        Assert.Equal("live", last.Text);
    }

    [Fact]
    public async Task Reasoning_never_becomes_display()
    {
        var model = new ReasoningThenSemanticLanguageModel();
        await using var runtime = Create(new CapturingSessionOutput(), model);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await runtime.WaitUntilIdleAsync();
        var assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal("Shown", assistant.Text);
        Assert.DoesNotContain("secret-thought", assistant.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Voice_none_speech_completes_without_tts_error()
    {
        var output = new CapturingSessionOutput();
        var synthesizer = new RecordingSynthesizer();
        var model = new SemanticLanguageModel(
            "Shown",
            new ModelSpeechProjection(ModelSpeechMode.None, null));
        await using var runtime = Create(output, model, voice: true, synthesizer: synthesizer);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("Hello");
        await runtime.WaitUntilIdleAsync();
        var assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Completed, assistant.Status);
        Assert.Equal(ResponseSpeechMode.None, assistant.Envelope!.SpeechMode);
        Assert.Empty(synthesizer.Texts);
        Assert.DoesNotContain(output.Items, item => item.Payload is ErrorOutput);
    }

    [Fact]
    public async Task Voice_custom_speech_is_synthesized_not_display()
    {
        var output = new CapturingSessionOutput();
        var synthesizer = new RecordingSynthesizer();
        var model = new SemanticLanguageModel(
            "A long display that should not be spoken.",
            new ModelSpeechProjection(ModelSpeechMode.Custom, "Short spoken"));
        await using var runtime = Create(output, model, voice: true, synthesizer: synthesizer);
        await runtime.AttachAsync();
        await runtime.SetModeAsync(SessionMode.Voice);
        await output.WaitForAsync(item => item.Payload is StateChangedOutput state && state.Mode == SessionMode.Voice);
        await runtime.SubmitUserTextAsync("Hello");
        var final = await output.WaitForAsync(item => item.Payload is AudioFrameOutput frame && frame.IsFinal);
        Assert.Contains(synthesizer.Texts, text => text.Contains("Short spoken", StringComparison.Ordinal));
        Assert.DoesNotContain(
            synthesizer.Texts,
            text => text.Contains("long display", StringComparison.Ordinal));
        await runtime.SubmitPlaybackAsync(final.ResponseId!.Value, "started", 0, 0);
        await runtime.SubmitPlaybackAsync(final.ResponseId!.Value, "completed", runtime.SentSamples, 0);
        await runtime.WaitUntilIdleAsync();
        var assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal("A long display that should not be spoken.", assistant.Text);
        Assert.Equal("Short spoken", assistant.Envelope!.SpeechText);
    }

    [Fact]
    public async Task Unauthorized_path_blocks_do_not_expose_ids()
    {
        var model = new SemanticLanguageModel(
            "See",
            new ModelSpeechProjection(ModelSpeechMode.Same, null),
            [
                new ModelResponseBlock(ModelResponseBlockKind.ArtifactReference, ArtifactId: "secret-id"),
                new ModelResponseBlock(ModelResponseBlockKind.AttachmentReference, AttachmentId: "folder/notes.txt")
            ]);
        await using var runtime = Create(new CapturingSessionOutput(), model);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await runtime.WaitUntilIdleAsync();
        var blocks = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant).Envelope!.Blocks;
        Assert.All(blocks, block => Assert.Equal(ResponseBlockKind.Unknown, block.Kind));
        Assert.All(blocks, block => Assert.Null(block.ArtifactId));
        Assert.All(blocks, block => Assert.Null(block.AttachmentId));
        Assert.DoesNotContain("secret-id", string.Join('|', blocks.Select(block => block.FallbackText)));
        Assert.DoesNotContain("folder/notes.txt", string.Join('|', blocks.Select(block => block.FallbackText)));
    }

    private static SessionRuntime Create(
        ISessionOutput output,
        ILanguageModel model,
        bool voice = false,
        ISpeechSynthesizer? synthesizer = null)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 128).Select(index => Guid.Parse($"019944af-0000-7000-8000-{index:D12}")),
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
            LocalUserProfile.Id,
            now,
            now);
        var store = new InMemoryMemoryStore();
        store.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
        synthesizer ??= voice ? new RecordingSynthesizer() : null;
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

    private sealed class RecordingLanguageModel : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true);
        public ModelRequest? LastRequest { get; private set; }

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            await Task.Yield();
            yield return new ModelDisplayDelta("Shown");
            yield return new ModelSemanticResponseReady(
                new ModelSemanticResponse("Shown", new ModelSpeechProjection(ModelSpeechMode.Same, null), []));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }

    private sealed class NativeJsonLanguageModel(string json, bool structured) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true, StructuredOutput: structured);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ModelTextDelta(json);
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }

    private sealed class GatedSemanticLanguageModel(TaskCompletionSource gate) : ILanguageModel
    {
        private int _calls;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ModelCapabilities Capabilities { get; } = new(true, true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            Started.TrySetResult();
            if (call == 1)
            {
                try
                {
                    await gate.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }

                yield return new ModelDisplayDelta("stale");
                yield return new ModelSemanticResponseReady(
                    new ModelSemanticResponse("stale", new ModelSpeechProjection(ModelSpeechMode.Same, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed);
                yield break;
            }

            yield return new ModelDisplayDelta("live");
            yield return new ModelSemanticResponseReady(
                new ModelSemanticResponse("live", new ModelSpeechProjection(ModelSpeechMode.Same, null), []));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }

    private sealed class ReasoningThenSemanticLanguageModel : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ModelReasoningDelta("secret-thought");
            yield return new ModelDisplayDelta("Shown");
            yield return new ModelSemanticResponseReady(
                new ModelSemanticResponse("Shown", new ModelSpeechProjection(ModelSpeechMode.Same, null), []));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }

    private sealed class SemanticLanguageModel(
        string display,
        ModelSpeechProjection speech,
        IReadOnlyList<ModelResponseBlock>? blocks = null) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ModelDisplayDelta(display);
            yield return new ModelSemanticResponseReady(
                new ModelSemanticResponse(display, speech, blocks ?? []));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
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
}
