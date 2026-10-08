using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Execution;
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

public sealed class EffectReceiptJourneyTests
{
    [Fact]
    public async Task Browser_close_receipt_survives_unavailable_follow_up_and_does_not_leak()
    {
        var output = new CapturingSessionOutput();
        var store = new InMemoryMemoryStore();
        var model = new CloseThenUnavailableModel();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-02T12:00:00Z"));
        var runs = CreateRuns(store);
        await using var runtime = Create(output, store, model, clock, runs);
        await runtime.AttachAsync();

        await runtime.SubmitUserTextAsync("close the browser");
        await runtime.WaitUntilIdleAsync();

        var waiting = Assert.Single(await runs.OpenAsync(runtime.SessionId));
        Assert.Equal(AgentRunStatus.WaitingToRetry, waiting.Status);
        for (var attempt = 1; attempt < waiting.MaxAttempts; attempt++)
        {
            clock.Advance(TimeSpan.FromSeconds(3));
            var current = (await runs.GetAsync(waiting.Owner, waiting.AgentRunId))!;
            var claimed = await runs.ApplyAsync(current.Owner, current.AgentRunId,
                new AgentCore.Application.Execution.AgentRunCommand.Claim(current.Revision, clock.GetUtcNow(), Guid.NewGuid(), clock.GetUtcNow().AddMinutes(5)));
            Assert.True(await runtime.DispatchAgentRunAsync(claimed.AgentRunId, false));
            await runtime.WaitUntilIdleAsync();
        }
        var terminal = (await runs.GetAsync(waiting.Owner, waiting.AgentRunId))!;
        Assert.Equal(AgentRunStatus.Failed, terminal.Status);
        Assert.Equal(waiting.MaxAttempts, terminal.AttemptCount);
        Assert.Equal(waiting.ResponseId, terminal.ResponseId);
        Assert.Equal(1, model.CloseCalls);
        var first = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Failed, first.Status);
        var receipt = Assert.Single(first.Envelope!.EffectReceipts!);
        Assert.Equal("Browser closed", receipt.Label);

        var live = output.Items.Select(item => item.Payload).OfType<ResponseCompletedOutput>().Single();
        Assert.Equal("Browser closed", Assert.Single(live.EffectReceipts!).Label);

        var loaded = await store.LoadAsync(runtime.Snapshot.SessionId);
        var reloaded = Assert.Single(loaded!.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(
            "Browser closed",
            Assert.Single(PublicHistory.FromEntry(reloaded).EffectReceipts!).Label);

        await runtime.SubmitUserTextAsync("why did it fail?");
        await runtime.WaitUntilIdleAsync();
        var next = Assert.Single(await runs.OpenAsync(runtime.SessionId));
        for (var attempt = 1; attempt < next.MaxAttempts; attempt++)
        {
            clock.Advance(TimeSpan.FromSeconds(3));
            var current = (await runs.GetAsync(next.Owner, next.AgentRunId))!;
            await runs.ApplyAsync(current.Owner, current.AgentRunId,
                new AgentCore.Application.Execution.AgentRunCommand.Claim(current.Revision, clock.GetUtcNow(), Guid.NewGuid(), clock.GetUtcNow().AddMinutes(5)));
            Assert.True(await runtime.DispatchAgentRunAsync(next.AgentRunId, false));
            await runtime.WaitUntilIdleAsync();
        }

        var assistants = runtime.Snapshot.Entries.Where(entry => entry.Role == ConversationRole.Assistant).ToArray();
        Assert.Equal(2, assistants.Length);
        Assert.Equal(EntryStatus.Failed, assistants[1].Status);
        Assert.True(assistants[1].Envelope?.EffectReceipts is null or { Count: 0 });
        var completions = output.Items.Select(item => item.Payload).OfType<ResponseCompletedOutput>().ToArray();
        Assert.Equal(2, completions.Length);
        Assert.True(completions[1].EffectReceipts is null or { Count: 0 });
    }

    private static SessionRuntime Create(
        CapturingSessionOutput output,
        InMemoryMemoryStore store,
        ILanguageModel model, FakeTimeProvider? clock = null, RuntimeAgentRunStore? runs = null)
    {
        var time = clock ?? new FakeTimeProvider(DateTimeOffset.Parse("2026-10-02T12:00:00Z"));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 80).Select(index => Guid.Parse($"019944af-00e1-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940d201")]);
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
            new RoleEnvironment(ToolAllowlist: [ToolCatalog.BrowserClose]));
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
        snapshot = RuntimeAgentRunStore.WithPins(snapshot);
        store.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
        var browser = new ClosingBrowser();
        return SessionRuntimeFixture.Create(
            snapshot,
            model,
            new DefaultAgentBrain(new PromptContextBuilder(ToolConfigurationGates.AllowAll, browser)),
            store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            tools: new SessionToolExecutor(browser: browser, configurationGate: ToolConfigurationGates.AllowAll),
            agentRuns: runs ?? CreateRuns(store));
    }

    private static RuntimeAgentRunStore CreateRuns(InMemoryMemoryStore memory)
    {
        var runs = new RuntimeAgentRunStore(); runs.Bind(memory); return runs;
    }

    private sealed class CloseThenUnavailableModel : ILanguageModel
    {
        private int _calls;
        internal int CloseCalls { get; private set; }

        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                CloseCalls++;
                yield return new ModelToolCallEvent(new ModelToolCall("close-1", ToolCatalog.BrowserClose, "{}"));
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
                yield break;
            }

            yield return new ModelFailed(new ProviderFailure(
                ProviderErrorCode.Unavailable,
                "Language model is unavailable.",
                FailureReason: ProviderFailureReason.Http5xx));
        }
    }

    private sealed class ClosingBrowser : IBrowser
    {
        public BrowserProviderDescriptor Provider { get; } = new("fixture", "Test browser", new HashSet<BrowserFeature> { BrowserFeature.Navigate, BrowserFeature.Snapshot, BrowserFeature.Click, BrowserFeature.Type, BrowserFeature.Hover, BrowserFeature.Drag, BrowserFeature.FillForm, BrowserFeature.SelectOption, BrowserFeature.PressKey, BrowserFeature.Upload, BrowserFeature.FillCredential, BrowserFeature.Wait, BrowserFeature.Tabs, BrowserFeature.Screenshot, BrowserFeature.Close });
        public bool IsAvailable => true;

        public BrowserHostPolicy HostPolicy { get; } = new(true, true, BrowserInteractionMode.InteractiveDemo, ["https://example.test"]);

        public ValueTask<Uri?> GetCurrentUrlAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            new((Uri?)null);

        public ValueTask<BrowserOperationResult> NavigateAsync(
            BrowserNavigateRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<BrowserOperationResult> SnapshotAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<BrowserOperationResult> InteractAsync(BrowserInteractionRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<BrowserCloseResult> CloseAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            new(new BrowserCloseResult("closed"));
    }
}
