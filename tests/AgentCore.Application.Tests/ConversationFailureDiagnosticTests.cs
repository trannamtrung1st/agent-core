using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class ConversationFailureDiagnosticTests
{
    private static readonly Guid DiagnosticId = Guid.Parse("019944af-00d7-7000-8000-0000000000d1");
    private static readonly Guid SecondDiagnosticId = Guid.Parse("019944af-00d7-7000-8000-0000000000d2");

    [Fact]
    public async Task Scripted_model_failure_uses_one_id_on_the_log_error_and_entry()
    {
        var logs = new DiagnosticLogCapture<SessionRuntime>();
        var diagnostics = new QueueDiagnosticIdSource([DiagnosticId, SecondDiagnosticId]);
        await using var runtime = CreateRuntime(new ScriptedEventsModel(
            [
                new ModelFailed(new ProviderFailure(ProviderErrorCode.Unavailable, "synthetic failure"))
            ],
            [
                Ready("Recovered"),
                new ModelCompleted(ModelStopReason.Completed)
            ]), logs, diagnostics);
        Assert.True(await runtime.SubmitPersistedUserTextAsync("fail", Guid.Parse("019944af-00d7-7000-8000-000000000011")));
        await runtime.WaitUntilIdleAsync();

        var failed = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Failed, failed.Status);
        Assert.Equal(DiagnosticId, failed.Failure!.DiagnosticId);
        Assert.Equal("provider", failed.Failure.Category);
        Assert.Equal("Unavailable", failed.Failure.Code);
        var error = Assert.Single(Items(runtime), item => item.Payload is ErrorOutput);
        var payload = Assert.IsType<ErrorOutput>(error.Payload);
        Assert.Equal(DiagnosticId, payload.DiagnosticId);
        Assert.Equal(error.ResponseId, failed.ResponseId);
        var logged = Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Equal(DiagnosticId, logged.Properties["DiagnosticId"]);
        Assert.Null(logged.Exception);
        Assert.DoesNotContain("synthetic failure", logged.Message, StringComparison.Ordinal);

        Assert.True(await runtime.SubmitPersistedUserTextAsync("again", Guid.Parse("019944af-00d7-7000-8000-000000000012")));
        await runtime.WaitUntilIdleAsync();
        var completed = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Completed, completed.Status);
        Assert.Null(completed.Failure);
        Assert.Equal(SecondDiagnosticId, diagnostics.NewId());
    }

    [Fact]
    public async Task Model_pump_catch_logs_the_exception_with_the_only_id()
    {
        var logs = new DiagnosticLogCapture<SessionRuntime>();
        var diagnostics = new QueueDiagnosticIdSource([DiagnosticId, SecondDiagnosticId]);
        await using var runtime = CreateRuntime(new ThrowingModel(), logs, diagnostics);
        Assert.True(await runtime.SubmitPersistedUserTextAsync("boom", Guid.Parse("019944af-00d7-7000-8000-000000000021")));
        await runtime.WaitUntilIdleAsync();

        var failed = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(DiagnosticId, failed.Failure!.DiagnosticId);
        Assert.Equal("Unknown", failed.Failure.Code);
        var logged = Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Equal(DiagnosticId, logged.Properties["DiagnosticId"]);
        Assert.IsType<InvalidOperationException>(logged.Exception);
        Assert.Contains("Language model pump failed.", logged.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", logged.Message, StringComparison.Ordinal);
        var error = Assert.IsType<ErrorOutput>(Assert.Single(Items(runtime), item => item.Payload is ErrorOutput).Payload);
        Assert.Equal(DiagnosticId, error.DiagnosticId);
        Assert.Equal(SecondDiagnosticId, diagnostics.NewId());
    }

    [Fact]
    public async Task Incomplete_semantic_completion_stores_one_reference_without_an_exception()
    {
        var logs = new DiagnosticLogCapture<SessionRuntime>();
        var diagnostics = new QueueDiagnosticIdSource([DiagnosticId, SecondDiagnosticId]);
        SemanticTestHook.Bypass.Value = true;
        try
        {
        var model = new ScriptedEventsModel([new ModelCompleted(ModelStopReason.Completed)]);
        await using var runtime = CreateRuntime(model, logs, diagnostics);
        Assert.True(await runtime.SubmitPersistedUserTextAsync("empty", Guid.Parse("019944af-00d7-7000-8000-000000000031")));
        await runtime.WaitUntilIdleAsync();

        var failed = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal("IncompleteResponse", failed.Failure!.Code);
        Assert.Equal("response", failed.Failure.Category);
        var logged = Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Null(logged.Exception);
        Assert.Equal(DiagnosticId, logged.Properties["DiagnosticId"]);
        var error = Assert.IsType<ErrorOutput>(Assert.Single(Items(runtime), item => item.Payload is ErrorOutput).Payload);
        Assert.Equal("IncompleteResponse", error.Code);
        Assert.DoesNotContain("displayText", error.SafeMessage, StringComparison.Ordinal);
        Assert.Equal(SecondDiagnosticId, diagnostics.NewId());
        }
        finally
        {
            SemanticTestHook.Bypass.Value = false;
        }
    }

    [Fact]
    public async Task Semantic_mapping_failure_logs_the_exception_and_hides_its_message()
    {
        var logs = new DiagnosticLogCapture<SessionRuntime>();
        var diagnostics = new QueueDiagnosticIdSource([DiagnosticId, SecondDiagnosticId]);
        var semantic = new ModelSemanticResponse(
            "Shown",
            new ModelSpeechProjection(ModelSpeechMode.Custom, null),
            []);
        await using var runtime = CreateRuntime(
            new ScriptedEventsModel([new ModelSemanticResponseReady(semantic)]),
            logs,
            diagnostics);
        Assert.True(await runtime.SubmitPersistedUserTextAsync("bad", Guid.Parse("019944af-00d7-7000-8000-000000000041")));
        await runtime.WaitUntilIdleAsync();

        var failed = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal("InvalidEnvelope", failed.Failure!.Code);
        var logged = Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Error);
        Assert.IsType<ArgumentException>(logged.Exception);
        Assert.Equal(DiagnosticId, logged.Properties["DiagnosticId"]);
        var error = Assert.IsType<ErrorOutput>(Assert.Single(Items(runtime), item => item.Payload is ErrorOutput).Payload);
        Assert.Equal("The response could not be completed.", error.SafeMessage);
        Assert.DoesNotContain("speech.text", error.SafeMessage, StringComparison.Ordinal);
        var projected = PublicHistory.FromEntry(failed);
        Assert.Equal(DiagnosticId, projected.Failure!.DiagnosticId);
        Assert.Equal("InvalidEnvelope", projected.Failure.Code);
        Assert.Equal(SecondDiagnosticId, diagnostics.NewId());
    }

    [Fact]
    public async Task Cancelled_generation_has_no_diagnostic_id()
    {
        var logs = new DiagnosticLogCapture<SessionRuntime>();
        var diagnostics = new QueueDiagnosticIdSource([DiagnosticId]);
        await using var runtime = CreateRuntime(
            new ScriptedEventsModel([new ModelFailed(new ProviderFailure(ProviderErrorCode.Cancelled, "Generation cancelled."))]),
            logs,
            diagnostics);
        Assert.True(await runtime.SubmitPersistedUserTextAsync("stop", Guid.Parse("019944af-00d7-7000-8000-000000000051")));
        await runtime.WaitUntilIdleAsync();

        var failed = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Failed, failed.Status);
        Assert.Null(failed.Failure);
        Assert.DoesNotContain(Items(runtime), item => item.Payload is ErrorOutput error && error.DiagnosticId is not null);
        Assert.DoesNotContain(logs.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Equal(DiagnosticId, diagnostics.NewId());
    }

    [Fact]
    public async Task Superseded_response_does_not_publish_its_failure_on_the_active_response()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logs = new DiagnosticLogCapture<SessionRuntime>();
        var diagnostics = new QueueDiagnosticIdSource([DiagnosticId, SecondDiagnosticId]);
        var output = new CapturingSessionOutput();
        await using var runtime = CreateRuntime(new HoldThenFailModel(gate), logs, diagnostics, output);
        Assert.True(await runtime.SubmitPersistedUserTextAsync("first", Guid.Parse("019944af-00d7-7000-8000-000000000061")));
        await output.WaitForAsync(item => item.Payload is ResponseStartedOutput);
        Assert.True(await runtime.SubmitPersistedUserTextAsync(
            "second",
            Guid.Parse("019944af-00d7-7000-8000-000000000062"),
            behavior: UserTextBehavior.Interrupt));
        gate.TrySetResult();
        await runtime.WaitUntilIdleAsync();

        var assistants = runtime.Snapshot.Entries.Where(entry => entry.Role == ConversationRole.Assistant).ToArray();
        var active = assistants[^1];
        Assert.Equal(EntryStatus.Completed, active.Status);
        Assert.Null(active.Failure);
        Assert.DoesNotContain(
            output.Items,
            item => item.ResponseId == active.ResponseId && item.Payload is ErrorOutput);
        Assert.All(
            assistants.Where(entry => entry.EntryId != active.EntryId),
            entry => Assert.Null(entry.Failure));
        Assert.Equal(DiagnosticId, diagnostics.NewId());
    }

    [Fact]
    public async Task Speech_synthesis_failure_stores_one_reference()
    {
        var logs = new DiagnosticLogCapture<SessionRuntime>();
        var diagnostics = new QueueDiagnosticIdSource([DiagnosticId, SecondDiagnosticId]);
        var output = new CapturingSessionOutput();
        await using var runtime = CreateRuntime(
            new ScriptedEventsModel(
            [
                Ready("Hello"),
                new ModelCompleted(ModelStopReason.Completed)
            ]),
            logs,
            diagnostics,
            output,
            SessionMode.Voice,
            new ThrowingSynthesizer());
        Assert.True(await runtime.SubmitPersistedUserTextAsync("speak", Guid.Parse("019944af-00d7-7000-8000-000000000071")));
        await runtime.WaitUntilIdleAsync();

        var failed = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Failed, failed.Status);
        Assert.Equal(DiagnosticId, failed.Failure!.DiagnosticId);
        Assert.Equal("Unknown", failed.Failure.Code);
        var logged = Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Error);
        Assert.IsType<InvalidOperationException>(logged.Exception);
        Assert.Contains("TTS pump failed.", logged.Message, StringComparison.Ordinal);
        Assert.Equal(SecondDiagnosticId, diagnostics.NewId());
    }

    private static IReadOnlyList<SessionOutput> Items(SessionRuntime runtime) => Runtimes[runtime].Items;

    private static ModelSemanticResponseReady Ready(string text) =>
        new(new ModelSemanticResponse(
            text,
            new ModelSpeechProjection(ModelSpeechMode.Same, null),
            [new ModelResponseBlock(ModelResponseBlockKind.Markdown, text)]));

    private static SessionRuntime CreateRuntime(
        ILanguageModel model,
        DiagnosticLogCapture<SessionRuntime> logs,
        QueueDiagnosticIdSource diagnostics,
        CapturingSessionOutput? output = null,
        SessionMode mode = SessionMode.Text,
        ISpeechSynthesizer? synthesizer = null)
    {
        output ??= new CapturingSessionOutput();
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-00d7-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero));
        var memory = new InMemoryMemoryStore();
        var now = time.GetUtcNow();
        var snapshot = new SessionSnapshot(
            1,
            ids.NewSessionId(),
            1,
            SampleDefinitions.Examiner,
            mode,
            null,
            SessionStatus.Created,
            [],
            string.Empty,
            0,
            null,
            null,
            now,
            now);
        memory.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
        var runtime = new SessionRuntime(
            snapshot,
            model,
            new DefaultAgentBrain(new PromptContextBuilder()),
            memory,
            output,
            ids,
            time,
            logs,
            synthesizer: synthesizer,
            diagnostics: diagnostics);
        Runtimes[runtime] = output;
        return runtime;
    }

    private static readonly Dictionary<SessionRuntime, CapturingSessionOutput> Runtimes = [];
}

file sealed class ScriptedEventsModel(params ModelGenerationEvent[][] scripts) : ILanguageModel
{
    private int _calls;

    public ModelCapabilities Capabilities { get; } = new(true, true);

    public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
        ModelRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var call = Interlocked.Increment(ref _calls);
        var script = scripts[Math.Min(call, scripts.Length) - 1];
        foreach (var item in script)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}

file sealed class ThrowingModel : ILanguageModel
{
    public ModelCapabilities Capabilities { get; } = new(true, true);

    public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
        ModelRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        throw new InvalidOperationException("model boom");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}

file sealed class HoldThenFailModel(TaskCompletionSource gate) : ILanguageModel
{
    public ModelCapabilities Capabilities { get; } = new(true, true);

    public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
        ModelRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var lastUser = request.Messages.LastOrDefault(message => message.Role == ModelRole.User)?.Text ?? "";
        if (lastUser.Contains("first", StringComparison.Ordinal))
        {
            try
            {
                await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            yield return new ModelFailed(new ProviderFailure(ProviderErrorCode.Unavailable, "late failure"));
            yield break;
        }

        yield return new ModelSemanticResponseReady(new ModelSemanticResponse(
            "ok",
            new ModelSpeechProjection(ModelSpeechMode.Same, null),
            [new ModelResponseBlock(ModelResponseBlockKind.Markdown, "ok")]));
        yield return new ModelCompleted(ModelStopReason.Completed);
        await Task.CompletedTask.ConfigureAwait(false);
    }
}

file sealed class ThrowingSynthesizer : ISpeechSynthesizer
{
    public SynthesisCapabilities Capabilities { get; } = new(true, false, true, false, false, [new AudioFormat("pcm16", 16000, 1)]);

    public async IAsyncEnumerable<SpeechSynthesisEvent> SynthesizeAsync(
        SpeechRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        throw new InvalidOperationException("synthesis boom");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
}
