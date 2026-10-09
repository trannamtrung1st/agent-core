using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class GenerationRetryTests
{
    [Theory]
    [InlineData(ProviderFailureReason.SetupTimeout)]
    [InlineData(ProviderFailureReason.StreamIdle)]
    public async Task Initial_timeout_retries_once_and_completes(string reason)
    {
        var model = new ScriptedModel(
            [new ModelFailed(TimeoutFailure(reason))],
            Answer("Recovered answer."));
        await using var runtime = Create(model, browser: null);
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitUserTextAsync("hello"));
        await SessionRuntimeFixture.SettleRetriesAsync(runtime);

        Assert.Equal(2, model.Calls);
        var assistant = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Completed, assistant.Status);
        Assert.Equal("Recovered answer.", assistant.Text);
    }

    [Theory]
    [InlineData(ProviderFailureReason.TotalTimeout)]
    [InlineData(null)]
    public async Task Total_or_unclassified_timeout_is_terminal(string? reason)
    {
        var model = new ScriptedModel([new ModelFailed(TimeoutFailure(reason))]);
        await using var runtime = Create(model, browser: null);
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitUserTextAsync("hello"));
        await SessionRuntimeFixture.SettleRetriesAsync(runtime);

        Assert.Equal(1, model.Calls);
        Assert.Equal(EntryStatus.Failed,
            Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant).Status);
    }

    [Fact]
    public async Task Repeated_setup_timeout_is_terminal_with_safe_diagnostic_reason()
    {
        var model = new ScriptedModel(
            [new ModelFailed(TimeoutFailure(ProviderFailureReason.SetupTimeout))],
            [new ModelFailed(TimeoutFailure(ProviderFailureReason.SetupTimeout))],
            [new ModelFailed(TimeoutFailure(ProviderFailureReason.SetupTimeout))]);
        await using var runtime = Create(model, browser: null);
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitUserTextAsync("hello"));
        await SessionRuntimeFixture.SettleRetriesAsync(runtime);

        Assert.Equal(3, model.Calls);
        var assistant = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Failed, assistant.Status);
        Assert.Equal(ProviderFailureReason.SetupTimeout, assistant.Failure!.FailureReason);
    }

    private static ProviderFailure TimeoutFailure(string? reason) =>
        new(ProviderErrorCode.Timeout, "Provider deadline expired.", FailureReason: reason);

    [Fact]
    public async Task Transient_follow_up_retries_once_without_replaying_the_browser_tool()
    {
        var browser = new CountingBrowser();
        var model = new ScriptedModel(
            [
                new ModelToolCallEvent(new ModelToolCall(
                    "nav-1",
                    ToolCatalog.BrowserNavigate,
                    """{"url":"https://zigwheels.test/"}""")),
                new ModelCompleted(ModelStopReason.ToolCalls)
            ],
            [new ModelFailed(Unavailable(ProviderFailureReason.StreamIncomplete))],
            Answer("Zigwheels is open."));
        await using var runtime = Create(model, browser, ToolCatalog.BrowserNavigate);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("check zigwheels"));
        await SessionRuntimeFixture.SettleRetriesAsync(runtime);

        Assert.Equal(3, model.Calls);
        Assert.Equal(["https://zigwheels.test/"], browser.Navigated);
        var assistant = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Completed, assistant.Status);
        Assert.Equal("Zigwheels is open.", assistant.Text);
    }

    [Fact]
    public async Task Durable_attempts_preserve_completed_browser_rounds()
    {
        var browser = new CountingBrowser();
        var model = new ScriptedModel(
            [
                new ModelToolCallEvent(new ModelToolCall(
                    "nav-1",
                    ToolCatalog.BrowserNavigate,
                    """{"url":"https://zigwheels.test/a"}""")),
                new ModelCompleted(ModelStopReason.ToolCalls)
            ],
            [new ModelFailed(Unavailable(ProviderFailureReason.StreamIncomplete))],
            [
                new ModelToolCallEvent(new ModelToolCall(
                    "nav-2",
                    ToolCatalog.BrowserNavigate,
                    """{"url":"https://zigwheels.test/b"}""")),
                new ModelCompleted(ModelStopReason.ToolCalls)
            ],
            [new ModelFailed(Unavailable(ProviderFailureReason.Http5xx))],
            Answer("Both pages are open."));
        await using var runtime = Create(model, browser, ToolCatalog.BrowserNavigate);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("check two pages"));
        await SessionRuntimeFixture.SettleRetriesAsync(runtime);

        Assert.Equal(5, model.Calls);
        Assert.Equal(["https://zigwheels.test/a", "https://zigwheels.test/b"], browser.Navigated);
        Assert.Equal(
            EntryStatus.Completed,
            Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant).Status);
    }

    [Fact]
    public async Task Close_with_null_arguments_rejects_then_recovers_with_empty_object()
    {
        var browser = new CountingBrowser();
        var model = new ScriptedModel(
            [
                new ModelToolCallEvent(new ModelToolCall("close-1", ToolCatalog.BrowserClose, "null")),
                new ModelCompleted(ModelStopReason.ToolCalls)
            ],
            [
                new ModelToolCallEvent(new ModelToolCall("close-2", ToolCatalog.BrowserClose, "{}")),
                new ModelCompleted(ModelStopReason.ToolCalls)
            ],
            Answer("Browser closed."));
        await using var runtime = Create(model, browser, ToolCatalog.BrowserClose);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("close the browser"));
        await SessionRuntimeFixture.SettleRetriesAsync(runtime);

        Assert.Equal(3, model.Calls);
        Assert.Contains(model.Requests[1].Messages, message => message.Role == ModelRole.Tool && message.Text.Contains("requires exactly {}", StringComparison.Ordinal));
        Assert.Equal(1, browser.CloseCalls);
        Assert.Equal(
            EntryStatus.Completed,
            Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant).Status);
    }

    [Fact]
    public async Task Attempt_bound_makes_repeated_transient_failure_terminal()
    {
        var model = new ScriptedModel(
            [new ModelFailed(Unavailable(ProviderFailureReason.Http5xx))],
            [new ModelFailed(Unavailable(ProviderFailureReason.StreamIncomplete))],
            [new ModelFailed(Unavailable(ProviderFailureReason.StreamIncomplete))]);
        await using var runtime = Create(model, browser: null);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("hello"));
        await SessionRuntimeFixture.SettleRetriesAsync(runtime);

        Assert.Equal(3, model.Calls);
        var assistant = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Failed, assistant.Status);
    }

    [Fact]
    public async Task Invalid_response_is_not_retried()
    {
        var model = new ScriptedModel(
            [
                new ModelFailed(new ProviderFailure(
                    ProviderErrorCode.InvalidResponse,
                    "Assistant response was invalid.",
                    FailureReason: ProviderFailureReason.InvalidJson))
            ]);
        await using var runtime = Create(model, browser: null);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("hello"));
        await SessionRuntimeFixture.SettleRetriesAsync(runtime);

        Assert.Equal(1, model.Calls);
        Assert.Equal(
            EntryStatus.Failed,
            Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant).Status);
    }

    [Fact]
    public async Task Open_circuit_uses_bounded_durable_retries()
    {
        var model = new ScriptedModel(
            [new ModelFailed(Unavailable(ProviderFailureReason.CircuitOpen))],
            [new ModelFailed(Unavailable(ProviderFailureReason.CircuitOpen))],
            [new ModelFailed(Unavailable(ProviderFailureReason.CircuitOpen))]);
        await using var runtime = Create(model, browser: null);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("hello"));
        await SessionRuntimeFixture.SettleRetriesAsync(runtime);

        Assert.Equal(3, model.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Visible_text_prevents_a_retry(bool timeout)
    {
        var model = new ScriptedModel(
            [
                new ModelSemanticResponseReady(new ModelSemanticResponse(
                    "Partial answer",
                    new ModelSpeechProjection(ModelSpeechMode.Same, null),
                    [])),
                new ModelFailed(timeout ? TimeoutFailure(ProviderFailureReason.StreamIdle)
                    : Unavailable(ProviderFailureReason.StreamIncomplete))
            ]);
        await using var runtime = Create(model, browser: null);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("hello"));
        await SessionRuntimeFixture.SettleRetriesAsync(runtime);

        Assert.Equal(1, model.Calls);
    }

    [Theory]
    [InlineData(ProviderErrorCode.Unavailable, ProviderFailureReason.IncompleteToolCall)]
    [InlineData(ProviderErrorCode.Timeout, ProviderFailureReason.StreamIdle)]
    [InlineData(ProviderErrorCode.Timeout, ProviderFailureReason.SetupTimeout)]
    [InlineData(ProviderErrorCode.Unavailable, ProviderFailureReason.Http5xx)]
    public async Task Incomplete_tool_batch_has_no_effect_and_releases_the_run_for_retry(ProviderErrorCode code, string reason)
    {
        var browser = new CountingBrowser();
        var model = new ScriptedModel(
            [
                new ModelToolCallEvent(new ModelToolCall(
                    "nav-1",
                    ToolCatalog.BrowserNavigate,
                    """{"url":"https://zigwheels.test/"}""")),
                new ModelFailed(new ProviderFailure(code, "Provider failed.", FailureReason: reason))
            ]);
        await using var runtime = Create(model, browser, ToolCatalog.BrowserNavigate);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("check zigwheels"));
        await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await runtime.HasOpenAgentRunsAsync());

        Assert.Equal(1, model.Calls);
        Assert.Empty(browser.Navigated);
    }

    [Fact]
    public async Task Cancellation_during_the_retry_stops_the_generation()
    {
        var model = new CancelOnRetryModel();
        await using var runtime = Create(model, browser: null);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("hello"));
        Assert.True(await SessionRuntimeFixture.DispatchRetryAsync(runtime));
        await model.RetryStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await runtime.CancelActiveResponseAsync();
        await SessionRuntimeFixture.SettleRetriesAsync(runtime);

        Assert.Equal(2, model.Calls);
        Assert.Equal(EntryStatus.Interrupted, Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant).Status);
    }

    private static ProviderFailure Unavailable(string reason) =>
        new(ProviderErrorCode.Unavailable, "Language model is unavailable.", FailureReason: reason);

    private static ModelGenerationEvent[] Answer(string text) =>
    [
        new ModelSemanticResponseReady(new ModelSemanticResponse(
            text,
            new ModelSpeechProjection(ModelSpeechMode.Same, null),
            [])),
        new ModelCompleted(ModelStopReason.Completed)
    ];

    private static SessionRuntime Create(ILanguageModel model, IBrowser? browser, params string[] tools)
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-02T12:00:00Z"));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 80).Select(index => Guid.Parse($"019944af-00f1-7000-8000-{index:D12}")),
            [Guid.NewGuid()]);
        var now = time.GetUtcNow();
        var definition = new AgentDefinition(
            1,
            "general-assistant",
            12,
            new AgentIdentity("Test", "Role", "desc", "Tone"),
            [],
            "instructions",
            new BehaviorPolicy("answerNewTurn", true, true),
            new ConversationPolicy("balanced", false, "en", 2048),
            new InitiativePolicy(false, 60_000, 120_000, 1, ["longSilence"], 0),
            new VoiceConfiguration(false, "default", 1.0),
            new ProviderPreferences("primary-llm", "primary-stt", "primary-tts"),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new RoleEnvironment(ToolAllowlist: tools));
        var snapshot = new SessionSnapshot(
            1,
            ids.NewSessionId(),
            1,
            definition,
            SessionMode.Text,
            null,
            SessionStatus.Created,
            [],
            string.Empty,
            0,
            null,
            null,
            now,
            now,
            ModelSelection: new SessionModelSelection(
                "synthetic-offline/scripted",
                "primary-llm",
                "scripted",
                ModelSelectionSource.SystemDefault,
                null), AgentInstanceId: Guid.NewGuid());
        var store = new InMemoryMemoryStore();
        store.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
        return SessionRuntimeFixture.Create(
            snapshot,
            model,
            new DefaultAgentBrain(new PromptContextBuilder(ToolConfigurationGates.AllowAll, browser)),
            store,
            new CapturingSessionOutput(),
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            tools: new SessionToolExecutor(browser: browser, configurationGate: ToolConfigurationGates.AllowAll));
    }

    private sealed class ScriptedModel(params ModelGenerationEvent[][] steps) : ILanguageModel
    {
        private int _calls;

        public int Calls => _calls;
        public List<ModelRequest> Requests { get; } = [];

        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            Requests.Add(request);
            var call = Interlocked.Increment(ref _calls);
            foreach (var evt in steps[call - 1])
            {
                yield return evt;
            }
        }
    }

    private sealed class CancelOnRetryModel : ILanguageModel
    {
        private int _calls;

        public int Calls => _calls;

        public TaskCompletionSource RetryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ModelCapabilities Capabilities { get; } = new(true, true, StructuredOutput: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                yield return new ModelFailed(new ProviderFailure(
                    ProviderErrorCode.Unavailable,
                    "Language model is unavailable.",
                    FailureReason: ProviderFailureReason.StreamIncomplete));
                yield break;
            }

            RetryStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class CountingBrowser : IBrowser
    {
        public BrowserProviderDescriptor Provider { get; } = new("fixture", "Test browser", new HashSet<BrowserFeature> { BrowserFeature.Navigate, BrowserFeature.Snapshot, BrowserFeature.Click, BrowserFeature.Type, BrowserFeature.Hover, BrowserFeature.Drag, BrowserFeature.FillForm, BrowserFeature.SelectOption, BrowserFeature.PressKey, BrowserFeature.Upload, BrowserFeature.FillCredential, BrowserFeature.Wait, BrowserFeature.Tabs, BrowserFeature.Screenshot, BrowserFeature.Close });
        public List<string> Navigated { get; } = [];

        public int CloseCalls { get; private set; }

        public bool IsAvailable => true;

        public BrowserHostPolicy HostPolicy { get; } = new(
            true,
            true,
            BrowserInteractionMode.InteractiveDemo,
            ["https://zigwheels.test"]);

        public ValueTask<Uri?> GetCurrentUrlAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            new(Navigated.Count == 0 ? null : new Uri(Navigated[^1]));

        public ValueTask<BrowserResult> NavigateAsync(
            BrowserRequest request,
            CancellationToken cancellationToken = default)
        {
            Navigated.Add(((BrowserNavigate)request.Command).Url!);
            return new(new BrowserResult(
                null,
                new BrowserSnapshot(((BrowserNavigate)request.Command).Url!, "Zigwheels", "Open", false, [])));
        }

        public ValueTask<BrowserResult> SnapshotAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<BrowserResult> InteractAsync(BrowserRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<BrowserResult> CloseAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            CloseCalls++;
            return new(new BrowserResult(null, Status: "closed", DataJson: System.Text.Json.JsonSerializer.Serialize(new { status = "closed" })));
        }

        public ValueTask<BrowserResult> ExecuteAsync(BrowserRequest request, CancellationToken ct = default) => request.Operation switch
        {
            BrowserOperation.Navigate => NavigateAsync(request, ct),
            BrowserOperation.Snapshot or BrowserOperation.WaitFor => SnapshotAsync(request.SessionId, ct),
            BrowserOperation.Close => CloseAsync(request.SessionId, ct),
            BrowserOperation.Click or BrowserOperation.Type or BrowserOperation.Hover or BrowserOperation.Drag or BrowserOperation.Upload or BrowserOperation.FillForm => InteractAsync(request, ct),
            _ => new(new BrowserResult("unsupported_operation")),
        };
}
}
