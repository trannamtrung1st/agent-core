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

    private static SessionRuntime Create(ILanguageModel model, TwoSiteBrowser browser)
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
                ToolCatalog.BrowserAct
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
                null));
        var store = new InMemoryMemoryStore();
        store.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
        return new SessionRuntime(
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

        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            ToolTexts.AddRange(request.Messages.Where(message => message.Role == ModelRole.Tool).Select(message => message.Text));
            var call = Interlocked.Increment(ref _calls);
            foreach (var evt in steps[Math.Min(call, steps.Length) - 1])
            {
                yield return evt;
            }
        }
    }

    private sealed class TwoSiteBrowser : IBrowserSession
    {
        public List<string> Navigated { get; } = [];

        public int ObserveCalls { get; private set; }

        public int ActCalls { get; private set; }

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
            Navigated.Add(request.Url.AbsoluteUri);
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
}
