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

public sealed class BrowserOriginHandoffTests
{
    [Fact]
    public async Task Challenged_origin_does_not_block_another_site()
    {
        var browser = new TwoSiteBrowser();
        var model = new SequencedModel(
            Navigate("a-1", "https://a.test/"),
            Navigate("a-2", "https://a.test/again"),
            Navigate("b-1", "https://b.test/specs"),
            [
                new ModelToolCallEvent(new ModelToolCall("see-b", ToolCatalog.BrowserObserve, "{}")),
                new ModelCompleted(ModelStopReason.ToolCalls)
            ],
            [
                new ModelToolCallEvent(new ModelToolCall(
                    "act-b",
                    ToolCatalog.BrowserAct,
                    """{"operation":"click","ref":"el_bbbbbbbbbbbbbbbbbbbbbb"}""")),
                new ModelCompleted(ModelStopReason.ToolCalls)
            ],
            Answer("Compared the open site."));
        await using var runtime = Create(model, browser);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("compare the two sites"));
        await runtime.WaitUntilIdleAsync();

        Assert.Equal(["https://a.test/", "https://b.test/specs"], browser.Navigated);
        Assert.Equal(1, browser.ObserveCalls);
        Assert.Equal(1, browser.ActCalls);
        Assert.Equal(
            EntryStatus.Completed,
            Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant).Status);
    }

    [Fact]
    public async Task Repeated_navigate_and_observe_on_the_challenged_origin_are_refused()
    {
        var browser = new TwoSiteBrowser();
        var model = new SequencedModel(
            Navigate("a-1", "https://a.test/"),
            Navigate("a-2", "https://a.test/again"),
            [
                new ModelToolCallEvent(new ModelToolCall("see-a", ToolCatalog.BrowserObserve, "{}")),
                new ModelCompleted(ModelStopReason.ToolCalls)
            ],
            Answer("Cars.com needs you."));
        await using var runtime = Create(model, browser);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("open cars"));
        await runtime.WaitUntilIdleAsync();

        Assert.Equal(["https://a.test/"], browser.Navigated);
        Assert.Equal(0, browser.ObserveCalls);
        Assert.Contains(model.ToolTexts, text => text.Contains("user_intervention_required", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Next_user_turn_can_observe_the_previously_challenged_origin()
    {
        var browser = new TwoSiteBrowser();
        var model = new SequencedModel(
            Navigate("a-1", "https://a.test/"),
            Answer("Please finish the check."),
            [
                new ModelToolCallEvent(new ModelToolCall("see-again", ToolCatalog.BrowserObserve, "{}")),
                new ModelCompleted(ModelStopReason.ToolCalls)
            ],
            Answer("The check is still there."));
        await using var runtime = Create(model, browser);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("open cars"));
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(0, browser.ObserveCalls);

        Assert.True(await runtime.SubmitUserTextAsync("continue"));
        await runtime.WaitUntilIdleAsync();

        Assert.Equal(1, browser.ObserveCalls);
        Assert.Equal(["https://a.test/"], browser.Navigated);
    }

    [Fact]
    public async Task Redirected_challenge_blocks_both_requested_and_final_origins()
    {
        var browser = new RedirectingBrowser();
        var model = new SequencedModel(
            Navigate("open", "https://cars.test/page"),
            Navigate("retry-requested", "https://cars.test/page"),
            Navigate("retry-final", "https://www.cars.test/page"),
            Answer("Both car addresses need you."));
        await using var runtime = Create(model, browser);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("compare cars and another site"));
        await runtime.WaitUntilIdleAsync();

        Assert.Equal(["https://cars.test/page"], browser.Navigated);
        Assert.Equal(
            EntryStatus.Completed,
            Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant).Status);
    }

    [Fact]
    public async Task Close_after_intervention_still_closes_once()
    {
        var browser = new TwoSiteBrowser();
        var model = new SequencedModel(
            Navigate("a-1", "https://a.test/"),
            [
                new ModelToolCallEvent(new ModelToolCall("close-1", ToolCatalog.BrowserClose, "null")),
                new ModelCompleted(ModelStopReason.ToolCalls)
            ],
            Answer("The browser is closed."));
        await using var runtime = Create(model, browser);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("open the site and then close the browser"));
        await runtime.WaitUntilIdleAsync();

        Assert.Equal(["https://a.test/"], browser.Navigated);
        Assert.Equal(1, browser.CloseCalls);
        Assert.Contains(
            model.ToolTexts,
            text => text.Contains("\"status\":\"closed\"", StringComparison.Ordinal)
                && !text.Contains("user_intervention_required", StringComparison.Ordinal));
        Assert.DoesNotContain(
            model.Requests[2].Messages,
            message => message.Text.Contains("requires human intervention", StringComparison.Ordinal));
        Assert.Equal(
            EntryStatus.Completed,
            Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant).Status);
    }

    [Fact]
    public async Task Closing_a_challenged_page_clears_the_current_origin()
    {
        var browser = new TwoSiteBrowser("already_closed");
        var model = new SequencedModel(
            Navigate("a-1", "https://a.test/"),
            [
                new ModelToolCallEvent(new ModelToolCall("see-a", ToolCatalog.BrowserObserve, "{}")),
                new ModelCompleted(ModelStopReason.ToolCalls)
            ],
            [
                new ModelToolCallEvent(new ModelToolCall("close-1", ToolCatalog.BrowserClose, "{}")),
                new ModelCompleted(ModelStopReason.ToolCalls)
            ],
            Navigate("a-2", "https://a.test/again"),
            Navigate("b-1", "https://b.test/specs"),
            [
                new ModelToolCallEvent(new ModelToolCall("see-b", ToolCatalog.BrowserObserve, "{}")),
                new ModelCompleted(ModelStopReason.ToolCalls)
            ],
            Answer("Specs are open."));
        await using var runtime = Create(model, browser);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("close the challenged page and continue"));
        await runtime.WaitUntilIdleAsync();

        Assert.Contains(model.Requests[3].Tools ?? [], tool => tool.Name == ToolCatalog.BrowserObserve);
        Assert.Contains(model.Requests[3].Tools ?? [], tool => tool.Name == ToolCatalog.BrowserAct);
        Assert.DoesNotContain(
            model.Requests[3].Messages,
            message => message.Text.Contains("requires human intervention", StringComparison.Ordinal));
        Assert.Equal(["https://a.test/", "https://b.test/specs"], browser.Navigated);
        Assert.Contains(model.Requests[4].Tools ?? [], tool => tool.Name == ToolCatalog.BrowserObserve);
        Assert.Contains(model.Requests[4].Tools ?? [], tool => tool.Name == ToolCatalog.BrowserNavigate);
        Assert.DoesNotContain(
            model.Requests[4].Messages,
            message => message.Text.Contains("requires human intervention", StringComparison.Ordinal));
        Assert.DoesNotContain(
            model.Requests[4].Messages,
            message => message.Text.Contains("Do not request more browser actions", StringComparison.Ordinal));
        Assert.Contains(model.Requests[5].Tools ?? [], tool => tool.Name == ToolCatalog.BrowserObserve);
        Assert.Equal(1, browser.CloseCalls);
        Assert.Equal(1, browser.ObserveCalls);
        Assert.Equal(
            EntryStatus.Completed,
            Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant).Status);
    }

    [Fact]
    public async Task A_refused_return_to_a_blocked_origin_keeps_the_open_page()
    {
        var browser = new TwoSiteBrowser();
        var model = new SequencedModel(
            Navigate("a-1", "https://a.test/"),
            Navigate("b-1", "https://b.test/specs"),
            Navigate("a-2", "https://a.test/again"),
            [
                new ModelToolCallEvent(new ModelToolCall("see-b", ToolCatalog.BrowserObserve, "{}")),
                new ModelCompleted(ModelStopReason.ToolCalls)
            ],
            Answer("Specs stayed open."));
        await using var runtime = Create(model, browser);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("leave the challenged site"));
        await runtime.WaitUntilIdleAsync();

        Assert.Equal(["https://a.test/", "https://b.test/specs"], browser.Navigated);
        Assert.Contains(model.Requests[3].Tools ?? [], tool => tool.Name == ToolCatalog.BrowserObserve);
        Assert.Contains(model.Requests[3].Tools ?? [], tool => tool.Name == ToolCatalog.BrowserAct);
        Assert.DoesNotContain(
            model.Requests[3].Messages,
            message => message.Text.Contains("requires human intervention", StringComparison.Ordinal));
        Assert.Equal(1, browser.ObserveCalls);
        Assert.Equal(
            EntryStatus.Completed,
            Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant).Status);
    }

    [Fact]
    public async Task A_successful_observe_breaks_the_blocked_attempt_streak()
    {
        var browser = new TwoSiteBrowser();
        var model = new SequencedModel(
            Navigate("a-1", "https://a.test/"),
            Navigate("b-1", "https://b.test/specs"),
            Navigate("a-2", "https://a.test/again"),
            [
                new ModelToolCallEvent(new ModelToolCall("see-b", ToolCatalog.BrowserObserve, "{}")),
                new ModelCompleted(ModelStopReason.ToolCalls)
            ],
            Navigate("a-3", "https://a.test/later"),
            Answer("Specs stayed open."));
        await using var runtime = Create(model, browser);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("keep the open site"));
        await runtime.WaitUntilIdleAsync();

        Assert.Equal(["https://a.test/", "https://b.test/specs"], browser.Navigated);
        Assert.Equal(1, browser.ObserveCalls);
        Assert.Contains(model.Requests[5].Tools ?? [], tool => tool.Name == ToolCatalog.BrowserObserve);
        Assert.Contains(model.Requests[5].Tools ?? [], tool => tool.Name == ToolCatalog.BrowserAct);
        Assert.DoesNotContain(
            model.Requests[5].Messages,
            message => message.Text.Contains("Do not request more browser actions", StringComparison.Ordinal));
        Assert.Equal(
            EntryStatus.Completed,
            Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant).Status);
    }

    [Fact]
    public async Task A_challenged_page_hides_observe_and_act_until_another_site_opens()
    {
        var browser = new TwoSiteBrowser();
        var model = new SequencedModel(
            Navigate("a-1", "https://a.test/"),
            Navigate("b-1", "https://b.test/specs"),
            [
                new ModelToolCallEvent(new ModelToolCall("see-b", ToolCatalog.BrowserObserve, "{}")),
                new ModelCompleted(ModelStopReason.ToolCalls)
            ],
            Answer("Specs are open."));
        await using var runtime = Create(model, browser);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("open another site"));
        await runtime.WaitUntilIdleAsync();

        Assert.DoesNotContain(model.Requests[1].Tools ?? [], tool => tool.Name is ToolCatalog.BrowserObserve or ToolCatalog.BrowserAct);
        Assert.Contains(model.Requests[1].Tools ?? [], tool => tool.Name == ToolCatalog.BrowserNavigate);
        Assert.Contains(model.Requests[1].Tools ?? [], tool => tool.Name == ToolCatalog.BrowserClose);
        Assert.Contains(model.Requests[1].Messages, message => message.Role == ModelRole.System && message.Text.Contains("requires human intervention", StringComparison.Ordinal));
        Assert.Contains(model.Requests[2].Tools ?? [], tool => tool.Name == ToolCatalog.BrowserObserve);
        Assert.Contains(model.Requests[2].Tools ?? [], tool => tool.Name == ToolCatalog.BrowserAct);
        Assert.Equal(1, browser.ObserveCalls);
        Assert.DoesNotContain(runtime.Snapshot.Entries, entry => entry.Text.Contains("requires human intervention", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_blocked_attempts_finish_without_more_browser_tools()
    {
        var browser = new TwoSiteBrowser();
        var model = new SequencedModel(
            Navigate("a-1", "https://a.test/"),
            [
                new ModelToolCallEvent(new ModelToolCall("see-a", ToolCatalog.BrowserObserve, "{}")),
                new ModelCompleted(ModelStopReason.ToolCalls)
            ],
            [
                new ModelToolCallEvent(new ModelToolCall("see-a-2", ToolCatalog.BrowserObserve, "{}")),
                new ModelCompleted(ModelStopReason.ToolCalls)
            ],
            Answer("Cars.com needs you before I can continue."));
        await using var runtime = Create(model, browser);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("open cars"));
        await runtime.WaitUntilIdleAsync();

        Assert.Equal(["https://a.test/"], browser.Navigated);
        Assert.Equal(0, browser.ObserveCalls);
        Assert.True(model.Requests[3].Tools is not { Count: > 0 });
        Assert.Contains(model.Requests[3].Messages, message => message.Role == ModelRole.System && message.Text.Contains("Do not request more browser actions", StringComparison.Ordinal));
        var assistant = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Completed, assistant.Status);
        Assert.Equal("Cars.com needs you before I can continue.", assistant.Text);
        Assert.DoesNotContain(runtime.Snapshot.Entries, entry => entry.Text.Contains("Do not request more browser actions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_tool_call_after_the_no_progress_stop_does_not_keep_generating()
    {
        var browser = new TwoSiteBrowser();
        var model = new SequencedModel(
            Navigate("a-1", "https://a.test/"),
            [
                new ModelToolCallEvent(new ModelToolCall("see-a", ToolCatalog.BrowserObserve, "{}")),
                new ModelCompleted(ModelStopReason.ToolCalls)
            ],
            [
                new ModelToolCallEvent(new ModelToolCall("see-a-2", ToolCatalog.BrowserObserve, "{}")),
                new ModelCompleted(ModelStopReason.ToolCalls)
            ],
            [
                new ModelToolCallEvent(new ModelToolCall("see-a-3", ToolCatalog.BrowserObserve, "{}")),
                new ModelCompleted(ModelStopReason.ToolCalls)
            ]);
        await using var runtime = Create(model, browser);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("open cars"));
        await runtime.WaitUntilIdleAsync();

        Assert.Equal(4, model.Requests.Count);
        Assert.Equal(["https://a.test/"], browser.Navigated);
        Assert.Equal(0, browser.ObserveCalls);
        Assert.Equal(
            EntryStatus.Failed,
            Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant).Status);
    }

    [Fact]
    public async Task Repeated_identical_observations_answer_instead_of_reaching_the_step_limit()
    {
        var browser = new StablePageBrowser();
        var model = new SequencedModel(
            Navigate("open", "https://store.test/orders"),
            [
                new ModelToolCallEvent(new ModelToolCall("see-1", ToolCatalog.BrowserObserve, """{"waitFor":"stable"}""")),
                new ModelCompleted(ModelStopReason.ToolCalls)
            ],
            [
                new ModelToolCallEvent(new ModelToolCall("see-2", ToolCatalog.BrowserObserve, "{}")),
                new ModelCompleted(ModelStopReason.ToolCalls)
            ],
            [
                new ModelToolCallEvent(new ModelToolCall("see-3", ToolCatalog.BrowserObserve, "{}")),
                new ModelCompleted(ModelStopReason.ToolCalls)
            ],
            Answer("Orders are listed. Low stock and products are unknown."));
        await using var runtime = Create(model, browser);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("check the store"));
        await runtime.WaitUntilIdleAsync();

        Assert.Equal(3, browser.ObserveCalls);
        Assert.Equal(5, model.Requests.Count);
        Assert.Null(model.Requests[4].Tools);
        Assert.Contains(
            model.Requests[4].Messages,
            message => message.Role == ModelRole.System
                && message.Text.Contains("No new browser evidence was obtained", StringComparison.Ordinal));
        Assert.DoesNotContain(
            runtime.Snapshot.Entries,
            entry => entry.Text.Contains("No new browser evidence", StringComparison.Ordinal));
        Assert.Equal(
            EntryStatus.Completed,
            Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant).Status);
    }

    private static ModelGenerationEvent[] Navigate(string id, string url) =>
    [
        new ModelToolCallEvent(new ModelToolCall(
            id,
            ToolCatalog.BrowserNavigate,
            $$"""{"url":"{{url}}"}""")),
        new ModelCompleted(ModelStopReason.ToolCalls)
    ];

    private static ModelGenerationEvent[] Answer(string text) =>
    [
        new ModelSemanticResponseReady(new ModelSemanticResponse(
            text,
            new ModelSpeechProjection(ModelSpeechMode.Same, null),
            [])),
        new ModelCompleted(ModelStopReason.Completed)
    ];

    private static SessionRuntime Create(ILanguageModel model, IBrowserSession browser)
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-02T12:00:00Z"));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 80).Select(index => Guid.Parse($"019944af-00f2-7000-8000-{index:D12}")),
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
            new RoleEnvironment(ToolAllowlist:
            [
                ToolCatalog.BrowserNavigate,
                ToolCatalog.BrowserObserve,
                ToolCatalog.BrowserAct,
                ToolCatalog.BrowserClose
            ]));
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

    private sealed class SequencedModel(params ModelGenerationEvent[][] steps) : ILanguageModel
    {
        private int _calls;

        public List<string> ToolTexts { get; } = [];

        public List<ModelRequest> Requests { get; } = [];

        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            Requests.Add(request);
            ToolTexts.AddRange(request.Messages.Where(message => message.Role == ModelRole.Tool).Select(message => message.Text));
            var call = Interlocked.Increment(ref _calls);
            foreach (var evt in steps[Math.Min(call, steps.Length) - 1])
            {
                yield return evt;
            }
        }
    }

    private sealed class StablePageBrowser : IBrowserSession
    {
        public int ObserveCalls { get; private set; }

        public bool IsAvailable => true;

        public BrowserHostPolicy HostPolicy { get; } = new(
            true,
            true,
            BrowserInteractionMode.InteractiveDemo,
            ["https://store.test"]);

        public ValueTask<Uri?> GetCurrentUrlAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            new(new Uri("https://store.test/orders"));

        public ValueTask<BrowserOperationResult> NavigateAsync(
            BrowserNavigateRequest request,
            CancellationToken cancellationToken = default) =>
            new(Page($"el_nav_{request.Url!.AbsoluteUri.Length}"));

        public ValueTask<BrowserOperationResult> ObserveAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            ObserveCalls++;
            return new(Page($"el_obs_{ObserveCalls}"));
        }

        public ValueTask<BrowserOperationResult> ActAsync(BrowserActRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<BrowserCloseResult> CloseAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private static BrowserOperationResult Page(string reference) =>
            new(
                null,
                new BrowserObservation(
                    "https://store.test/orders",
                    "Orders",
                    "Orders grid",
                    false,
                    [new BrowserElement(reference, "link", "Order")],
                    Settled: true));
    }

    private sealed class TwoSiteBrowser(string closeStatus = "closed") : IBrowserSession
    {
        public List<string> Navigated { get; } = [];

        public int ObserveCalls { get; private set; }

        public int ActCalls { get; private set; }

        public int CloseCalls { get; private set; }

        public bool IsAvailable => true;

        public BrowserHostPolicy HostPolicy { get; } = new(
            true,
            true,
            BrowserInteractionMode.InteractiveDemo,
            ["https://a.test", "https://b.test"]);

        public ValueTask<Uri?> GetCurrentUrlAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            new(Navigated.Count == 0 ? null : new Uri(Navigated[^1]));

        public ValueTask<BrowserOperationResult> NavigateAsync(
            BrowserNavigateRequest request,
            CancellationToken cancellationToken = default)
        {
            Navigated.Add(request.Url!.AbsoluteUri);
            return new(Page(request.Url));
        }

        public ValueTask<BrowserOperationResult> ObserveAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            ObserveCalls++;
            return new(Page(new Uri(Navigated[^1])));
        }

        public ValueTask<BrowserOperationResult> ActAsync(
            BrowserActRequest request,
            CancellationToken cancellationToken = default)
        {
            ActCalls++;
            return new(Page(new Uri(Navigated[^1])));
        }

        public ValueTask<BrowserCloseResult> CloseAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            CloseCalls++;
            return new(new BrowserCloseResult(closeStatus));
        }

        private static BrowserOperationResult Page(Uri url)
        {
            var challenged = string.Equals(url.Host, "a.test", StringComparison.OrdinalIgnoreCase);
            return new BrowserOperationResult(
                null,
                new BrowserObservation(
                    url.AbsoluteUri,
                    challenged ? "Verify" : "Specs",
                    challenged ? "Human verification required" : "Listed price",
                    false,
                    challenged ? [] : [new BrowserElement("el_bbbbbbbbbbbbbbbbbbbbbb", "link", "Specs")],
                    challenged ? BrowserInterventionKind.HumanVerificationRequired : BrowserInterventionKind.None));
        }
    }

    private sealed class RedirectingBrowser : IBrowserSession
    {
        public List<string> Navigated { get; } = [];

        public bool IsAvailable => true;

        public BrowserHostPolicy HostPolicy { get; } = new(
            true,
            true,
            BrowserInteractionMode.InteractiveDemo,
            ["https://cars.test", "https://www.cars.test", "https://b.test"]);

        public ValueTask<Uri?> GetCurrentUrlAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            new(Navigated.Count == 0 ? null : new Uri(Navigated[^1]));

        public ValueTask<BrowserOperationResult> NavigateAsync(
            BrowserNavigateRequest request,
            CancellationToken cancellationToken = default)
        {
            Navigated.Add(request.Url!.AbsoluteUri);
            var challenged = request.Url!.Host is "cars.test" or "www.cars.test";
            var final = challenged
                ? new Uri("https://www.cars.test/page")
                : request.Url;
            return new(new BrowserOperationResult(
                null,
                new BrowserObservation(
                    final.AbsoluteUri,
                    challenged ? "Verify" : "Specs",
                    challenged ? "Human verification required" : "Listed price",
                    false,
                    [],
                    challenged ? BrowserInterventionKind.HumanVerificationRequired : BrowserInterventionKind.None)));
        }

        public ValueTask<BrowserOperationResult> ObserveAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<BrowserOperationResult> ActAsync(BrowserActRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
