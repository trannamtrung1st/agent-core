using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Application.Tools;
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
    public async Task Invalid_response_provider_failure_logs_bounded_reason_and_channel()
    {
        var logs = new DiagnosticLogCapture<SessionRuntime>();
        var diagnostics = new QueueDiagnosticIdSource([DiagnosticId, SecondDiagnosticId]);
        await using var runtime = CreateRuntime(new ScriptedEventsModel(
            [
                new ModelFailed(new ProviderFailure(
                    ProviderErrorCode.InvalidResponse,
                    "Malformed assistant envelope.",
                    FailureReason: ProviderFailureReason.InvalidMemoryProposal,
                    ResponseChannel: ProviderResponseChannel.ResponseFunction))
            ]), logs, diagnostics);
        Assert.True(await runtime.SubmitPersistedUserTextAsync(
            "fail",
            Guid.Parse("019944af-00d7-7000-8000-000000000041")));
        await runtime.WaitUntilIdleAsync();

        var failed = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(DiagnosticId, failed.Failure!.DiagnosticId);
        Assert.Equal("InvalidResponse", failed.Failure.Code);
        Assert.Equal(ProviderFailureReason.InvalidMemoryProposal, failed.Failure.FailureReason);
        Assert.Equal(ProviderResponseChannel.ResponseFunction, failed.Failure.ProviderResponseChannel);
        var logged = Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Equal(DiagnosticId, logged.Properties["DiagnosticId"]);
        Assert.Equal(ProviderFailureReason.InvalidMemoryProposal, logged.Properties["FailureReason"]);
        Assert.Equal(ProviderResponseChannel.ResponseFunction, logged.Properties["ProviderResponseChannel"]);
        Assert.DoesNotContain("Malformed assistant envelope.", logged.Message, StringComparison.Ordinal);
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
    public async Task Length_limit_before_a_semantic_envelope_is_output_limit()
    {
        var logs = new DiagnosticLogCapture<SessionRuntime>();
        var diagnostics = new QueueDiagnosticIdSource([DiagnosticId]);
        await using var runtime = CreateRuntime(
            new ScriptedEventsModel([new ModelCompleted(ModelStopReason.LengthLimit)]),
            logs,
            diagnostics);
        Assert.True(await runtime.SubmitPersistedUserTextAsync(
            "long",
            Guid.Parse("019944af-00d7-7000-8000-000000000061")));
        await runtime.WaitUntilIdleAsync();

        var failed = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal("InvalidResponse", failed.Failure!.Code);
        Assert.Equal(ProviderFailureReason.OutputLimit, failed.Failure.FailureReason);
        Assert.Equal(ProviderResponseChannel.MarkerCompatibility, failed.Failure.ProviderResponseChannel);
        Assert.NotEqual(ProviderFailureReason.MissingDisplayText, failed.Failure.FailureReason);
        var error = Assert.IsType<ErrorOutput>(Assert.Single(Items(runtime), item => item.Payload is ErrorOutput).Payload);
        Assert.Equal("The model output was cut off before a complete response.", error.SafeMessage);
        Assert.DoesNotContain("Malformed assistant envelope.", error.SafeMessage, StringComparison.Ordinal);
        var projected = PublicHistory.FromEntry(failed);
        Assert.Equal(ProviderFailureReason.OutputLimit, projected.Failure!.FailureReason);
        Assert.DoesNotContain("{", projected.Failure.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Truncated_tool_call_failure_is_not_executed()
    {
        var logs = new DiagnosticLogCapture<SessionRuntime>();
        var diagnostics = new QueueDiagnosticIdSource([DiagnosticId]);
        await using var runtime = CreateRuntime(
            new ScriptedEventsModel(
            [
                new ModelToolCallEvent(new ModelToolCall(
                    "call-cut",
                    ToolCatalog.KnowledgeRetrieve,
                    """{"identity":"support-order-policy","raw":"SECRET_ARGUMENT"}""")),
                new ModelFailed(new ProviderFailure(
                    ProviderErrorCode.InvalidResponse,
                    "The model's tool call was cut off before it finished.",
                    FailureReason: ProviderFailureReason.ToolCallTruncated,
                    ResponseChannel: ProviderResponseChannel.ToolCall))
            ]),
            logs,
            diagnostics);
        Assert.True(await runtime.SubmitPersistedUserTextAsync(
            "cut",
            Guid.Parse("019944af-00d7-7000-8000-000000000071")));
        await runtime.WaitUntilIdleAsync();

        var failed = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(ProviderFailureReason.ToolCallTruncated, failed.Failure!.FailureReason);
        Assert.Equal(ProviderResponseChannel.ToolCall, failed.Failure.ProviderResponseChannel);
        var error = Assert.IsType<ErrorOutput>(Assert.Single(Items(runtime), item => item.Payload is ErrorOutput).Payload);
        Assert.DoesNotContain("SECRET_ARGUMENT", error.SafeMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET_ARGUMENT", failed.Text, StringComparison.Ordinal);
        Assert.Equal(EntryStatus.Failed, failed.Status);
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

    [Fact]
    public async Task Conversation_failure_log_includes_the_pinned_instance_and_provider_alias()
    {
        var instanceId = Guid.Parse("019944af-00d7-7000-8000-0000000000e1");
        var logs = new DiagnosticLogCapture<SessionRuntime>();
        var diagnostics = new QueueDiagnosticIdSource([DiagnosticId]);
        await using var runtime = CreateRuntime(
            new ScriptedEventsModel(
            [
                new ModelFailed(new ProviderFailure(ProviderErrorCode.Unavailable, "synthetic failure"))
            ]),
            logs,
            diagnostics,
            agentInstanceId: instanceId,
            modelSelection: new SessionModelSelection(
                "scripted-alpha",
                "primary-llm",
                "scripted-alpha",
                ModelSelectionSource.SystemDefault,
                null));
        Assert.True(await runtime.SubmitPersistedUserTextAsync("fail", Guid.Parse("019944af-00d7-7000-8000-000000000081")));
        await runtime.WaitUntilIdleAsync();

        var logged = Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Equal(DiagnosticId, logged.Properties["DiagnosticId"]);
        Assert.Equal(instanceId, logged.Properties["AgentInstanceId"]);
        Assert.Equal("primary-llm", logged.Properties["ProviderAlias"]);
        Assert.DoesNotContain("scripted-alpha", logged.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Terminal_end_persistence_failure_keeps_the_exception_off_the_client_error()
    {
        const string secret = "/private/database/path api_key=secret provider-body-secret";
        var logs = new DiagnosticLogCapture<SessionRuntime>();
        var diagnostics = new QueueDiagnosticIdSource([DiagnosticId, SecondDiagnosticId]);
        var store = new TerminalEndFailingStore(secret);
        await using var runtime = CreateRuntime(
            new ScriptedEventsModel(),
            logs,
            diagnostics,
            memory: store);
        Assert.True(await runtime.AttachAsync());
        Assert.False(await runtime.RequestEndAsync());
        await runtime.WaitUntilMailboxDrainedAsync();

        Assert.Equal(SessionStatus.Ending, runtime.Snapshot.Status);
        Assert.Equal(DiagnosticId, runtime.PersistenceFailureDiagnosticId);
        var error = Assert.IsType<ErrorOutput>(Assert.Single(Items(runtime), item => item.Payload is ErrorOutput).Payload);
        Assert.Equal("Session", error.Category);
        Assert.Equal("SessionPersistenceUnavailable", error.Code);
        Assert.Equal("Persistent save failed.", error.SafeMessage);
        Assert.Equal(DiagnosticId, error.DiagnosticId);
        Assert.DoesNotContain("/private/database/path", error.SafeMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("api_key=secret", error.SafeMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-body-secret", error.SafeMessage, StringComparison.Ordinal);

        var logged = Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Error);
        var failure = Assert.IsType<InvalidOperationException>(logged.Exception);
        Assert.Contains("/private/database/path", failure.Message, StringComparison.Ordinal);
        Assert.Contains("api_key=secret", failure.Message, StringComparison.Ordinal);
        Assert.Contains("provider-body-secret", failure.Message, StringComparison.Ordinal);
        Assert.Equal(DiagnosticId, logged.Properties["DiagnosticId"]);
        Assert.Equal(runtime.SessionId, logged.Properties["SessionId"]);
        Assert.DoesNotContain("/private/database/path", logged.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("api_key=secret", logged.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-body-secret", logged.Message, StringComparison.Ordinal);

        var durable = await store.Inner.LoadAsync(runtime.SessionId);
        Assert.NotEqual(SessionStatus.Ended, durable!.Status);
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
        ISpeechSynthesizer? synthesizer = null,
        IMemoryStore? memory = null,
        Guid? agentInstanceId = null,
        SessionModelSelection? modelSelection = null)
    {
        output ??= new CapturingSessionOutput();
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-00d7-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero));
        memory ??= new InMemoryMemoryStore();
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
            now,
            ModelSelection: modelSelection,
            AgentInstanceId: agentInstanceId);
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

file sealed class TerminalEndFailingStore(string secret) : IMemoryStore
{
    public InMemoryMemoryStore Inner { get; } = new();

    public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        Inner.LoadAsync(sessionId, cancellationToken);

    public ValueTask SaveAsync(SessionSnapshot snapshot, long expectedRevision, CancellationToken cancellationToken = default)
    {
        if (snapshot.Status == SessionStatus.Ended)
        {
            throw new InvalidOperationException(secret);
        }

        return Inner.SaveAsync(snapshot, expectedRevision, cancellationToken);
    }

    public ValueTask<IReadOnlyList<ConversationEntry>> ReadHistoryAsync(
        Guid sessionId,
        long afterEntrySequence,
        int limit,
        CancellationToken cancellationToken = default) =>
        Inner.ReadHistoryAsync(sessionId, afterEntrySequence, limit, cancellationToken);

    public ValueTask<ConversationHistoryPage?> ReadHistoryPageAsync(
        Guid sessionId,
        long? afterEntrySequence,
        long? beforeEntrySequence,
        int limit,
        CancellationToken cancellationToken = default) =>
        Inner.ReadHistoryPageAsync(sessionId, afterEntrySequence, beforeEntrySequence, limit, cancellationToken);

    public ValueTask<UserProfile?> LoadProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        Inner.LoadProfileAsync(profileId, cancellationToken);

    public ValueTask SaveProfileAsync(UserProfile profile, long expectedRevision, CancellationToken cancellationToken = default) =>
        Inner.SaveProfileAsync(profile, expectedRevision, cancellationToken);

    public ValueTask RecoverCrashedSessionsAsync(CancellationToken cancellationToken = default) =>
        Inner.RecoverCrashedSessionsAsync(cancellationToken);
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
