using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.SemanticResponses;
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
        Assert.DoesNotContain(
            output.Items,
            item => item.Payload is ResponseProgressOutput progress
                && progress.Kind == ResponseProgressKind.Finalizing);
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

    [Fact]
    public async Task Second_semantic_ready_refreshes_the_same_assistant_entry()
    {
        var output = new CapturingSessionOutput();
        await using var runtime = Create(output, new TwiceReadyLanguageModel());
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await runtime.WaitUntilIdleAsync();
        var assistant = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Completed, assistant.Status);
        Assert.Equal("Other", assistant.Text);
    }

    [Theory]
    [InlineData("D")]
    [InlineData("B")]
    [InlineData("N")]
    public async Task Authorized_case_variant_artifact_is_canonical_in_live_and_durable_response(string format)
    {
        var output = new CapturingSessionOutput();
        var store = new InMemoryMemoryStore();
        var artifacts = new InMemoryArtifactStore(TimeProvider.System);
        var blocks = new List<ModelResponseBlock>();
        var model = new SemanticLanguageModel("See file", new ModelSpeechProjection(ModelSpeechMode.None, null), blocks);
        await using var runtime = Create(output, model, store: store, artifacts: new SessionArtifactAuthorizer(artifacts));
        var artifact = await artifacts.CreateAsync(runtime.SessionId, "notes.txt", "text/plain", "exact bytes"u8.ToArray(), null, null);
        blocks.Add(new ModelResponseBlock(ModelResponseBlockKind.ArtifactReference,
            ArtifactId: artifact.ArtifactId.ToString(format).ToUpperInvariant()));
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Show the file");
        await runtime.WaitUntilIdleAsync();
        var canonicalId = artifact.ArtifactId.ToString("D");
        var live = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Completed, live.Status);
        var block = Assert.Single(live.Envelope!.Blocks);
        Assert.Equal(ResponseBlockKind.ArtifactReference, block.Kind);
        Assert.Equal(canonicalId, block.ArtifactId);
        Assert.Equal(canonicalId, block.DisplayText);
        Assert.Equal(canonicalId, block.FallbackText);
        var delivered = Assert.Single(output.Items, item => item.Payload is BlockUpsertOutput { Kind: "artifact" });
        Assert.Equal(canonicalId, ((BlockUpsertOutput)delivered.Payload).ArtifactId);
        var durable = await store.LoadAsync(runtime.SessionId);
        Assert.Equal(canonicalId, Assert.Single(Assert.Single(durable!.Entries,
            entry => entry.Role == ConversationRole.Assistant).Envelope!.Blocks).ArtifactId);
    }

    [Fact]
    public async Task Streamed_text_turn_reloads_as_one_durable_response()
    {
        var output = new CapturingSessionOutput();
        var store = new InMemoryMemoryStore();
        await using var runtime = Create(output, new RecordingLanguageModel(), store: store);
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await runtime.WaitUntilIdleAsync();
        Assert.Contains(output.Items, item => item.Payload is TextDeltaOutput);
        var loaded = await store.LoadAsync(runtime.SessionId);
        Assert.NotNull(loaded);
        var assistant = Assert.Single(loaded!.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Completed, assistant.Status);
        Assert.Equal("Shown", assistant.Text);
    }

    [Fact]
    public async Task Wait_on_direct_user_turn_fails_as_invalid_agent_step()
    {
        var output = new CapturingSessionOutput();
        await using var runtime = Create(output, new DispositionLanguageModel("Wait"));
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await runtime.WaitUntilIdleAsync();
        var assistant = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Failed, assistant.Status);
        Assert.NotNull(assistant.Failure);
        Assert.Contains(output.Items, item => item.Payload is ErrorOutput);
        var completed = Assert.IsType<ResponseCompletedOutput>(
            Assert.Single(output.Items, item => item.Payload is ResponseCompletedOutput).Payload);
        Assert.True(completed.Failed);
        Assert.Null(completed.InterruptReason);
    }

    [Fact]
    public async Task Structured_wait_json_on_user_turn_uses_the_failure_path()
    {
        var json = """
            {"disposition":"Wait","action":null,"displayText":"","speech":{"mode":"none","text":null},"blocks":[],"memory":[]}
            """;
        var output = new CapturingSessionOutput();
        await using var runtime = Create(
            output,
            new SemanticResponseLanguageModel(new NativeJsonLanguageModel(json, structured: true)));
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await runtime.WaitUntilIdleAsync();
        var assistant = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Failed, assistant.Status);
        Assert.NotNull(assistant.Failure);
        Assert.Contains(output.Items, item => item.Payload is ErrorOutput);
        var completed = Assert.IsType<ResponseCompletedOutput>(
            Assert.Single(output.Items, item => item.Payload is ResponseCompletedOutput).Payload);
        Assert.True(completed.Failed);
        Assert.Null(completed.InterruptReason);
    }

    [Fact]
    public async Task Complete_without_chat_on_user_turn_fails_as_invalid_agent_step()
    {
        var output = new CapturingSessionOutput();
        await using var runtime = Create(output, new NoChatCompleteLanguageModel());
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await runtime.WaitUntilIdleAsync();
        var assistant = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Failed, assistant.Status);
        Assert.Contains(output.Items, item => item.Payload is ErrorOutput);
        var completed = Assert.IsType<ResponseCompletedOutput>(
            Assert.Single(output.Items, item => item.Payload is ResponseCompletedOutput).Payload);
        Assert.True(completed.Failed);
        Assert.Null(completed.InterruptReason);
    }

    [Fact]
    public async Task Structured_complete_chat_json_delivers_one_assistant_response()
    {
        var json = """
            {"disposition":"Complete","action":{"kind":"chat.respond"},"displayText":"Shown","speech":{"mode":"same","text":null},"blocks":[],"memory":[]}
            """;
        await using var runtime = Create(
            new CapturingSessionOutput(),
            new SemanticResponseLanguageModel(new NativeJsonLanguageModel(json, structured: true)));
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await runtime.WaitUntilIdleAsync();
        var assistant = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Completed, assistant.Status);
        Assert.Equal("Shown", assistant.Text);
    }

    [Fact]
    public async Task Blocked_disposition_uses_the_failure_path()
    {
        var output = new CapturingSessionOutput();
        await using var runtime = Create(output, new DispositionLanguageModel("Blocked"));
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await runtime.WaitUntilIdleAsync();
        var assistant = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Failed, assistant.Status);
        Assert.NotNull(assistant.Failure);
        Assert.Contains(output.Items, item => item.Payload is ErrorOutput);
    }

    [Fact]
    public async Task Continue_disposition_delivers_one_chat_response()
    {
        await using var runtime = Create(new CapturingSessionOutput(), new DispositionLanguageModel("Continue"));
        await runtime.AttachAsync();
        await runtime.SubmitUserTextAsync("Hello");
        await runtime.WaitUntilIdleAsync();
        var assistant = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Completed, assistant.Status);
        Assert.Equal("Shown", assistant.Text);
    }

    private static SessionRuntime Create(
        ISessionOutput output,
        ILanguageModel model,
        bool voice = false,
        ISpeechSynthesizer? synthesizer = null,
        InMemoryMemoryStore? store = null,
        IArtifactReferenceAuthorizer? artifacts = null)
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
            now, AgentInstanceId: Guid.NewGuid());
        store ??= new InMemoryMemoryStore();
        store.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
        synthesizer ??= voice ? new RecordingSynthesizer() : null;
        return SessionRuntimeFixture.Create(
            snapshot,
            model,
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            recognizer: synthesizer is null ? null : new SyntheticSpeechRecognizer(),
            synthesizer: synthesizer,
            artifacts: artifacts);
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

    private sealed class TwiceReadyLanguageModel : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ModelDisplayDelta("Shown");
            yield return new ModelSemanticResponseReady(
                new ModelSemanticResponse("Shown", new ModelSpeechProjection(ModelSpeechMode.Same, null), []));
            yield return new ModelSemanticResponseReady(
                new ModelSemanticResponse("Other", new ModelSpeechProjection(ModelSpeechMode.Same, null), []));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }

    private sealed class NoChatCompleteLanguageModel : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true, StructuredOutput: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ModelSemanticResponseReady(
                new ModelSemanticResponse(
                    string.Empty,
                    new ModelSpeechProjection(ModelSpeechMode.None, null),
                    [],
                    Disposition: nameof(AgentStepDisposition.Complete),
                    ActionKind: null,
                    ActionSpecified: true));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }

    private sealed class DispositionLanguageModel(string disposition) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ModelDisplayDelta("Shown");
            yield return new ModelSemanticResponseReady(
                new ModelSemanticResponse(
                    "Shown",
                    new ModelSpeechProjection(ModelSpeechMode.Same, null),
                    [],
                    Disposition: disposition));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }
}
