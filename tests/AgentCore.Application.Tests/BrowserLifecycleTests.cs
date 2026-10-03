using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Application.Tools;
using AgentCore.Application.Work;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;
using AgentCore.Domain.Work;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class BrowserLifecycleTests
{
    private static readonly string[] FixtureOrigin = ["http://127.0.0.1:5091"];

    [Fact]
    public async Task Delete_and_end_release_the_browser_session()
    {
        var lease = new CountingLease();
        var sessions = new InMemoryMemoryStore();
        var manager = new SessionManager(
            new StaticDefinitions(SampleDefinitions.Examiner),
            sessions,
            Ids("019944af-00c1-7000-8000-", "873f07d1-e264-4c81-a31b-7e59e940c1"),
            TimeProvider.System,
            new VoiceAvailability { SpeechAdaptersResolved = true },
            browserLease: lease);
        var created = await manager.CreateAsync("examiner", 1, SessionMode.Text);
        await manager.DurablyDeleteAsync(created.SessionId);
        Assert.Equal([created.SessionId], lease.Sessions);

        var browser = new HoldingBrowser();
        lease = new CountingLease();
        var runtime = Runtime(browser, lease, new OneNavigateModel());
        await runtime.AttachAsync();
        Assert.True(await runtime.RequestEndAsync());
        Assert.Equal([runtime.SessionId], lease.Sessions);
        await runtime.DisposeAsync();
        Assert.Equal([runtime.SessionId, runtime.SessionId], lease.Sessions);
    }

    [Fact]
    public async Task Cancel_and_steer_drop_a_late_navigation_and_do_not_navigate_again()
    {
        var browser = new HoldingBrowser { Hold = true, LateTitle = "LATE-PAGE" };
        var output = new CapturingSessionOutput();
        await using var runtime = Runtime(browser, new CountingLease(), new OneNavigateModel(), output);
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitUserTextAsync("open the record"));
        await browser.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ResponseCancelResult.Cancelled, await runtime.CancelResponseAsync(runtime.ActiveResponseId!.Value));
        browser.Release.TrySetResult();
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(1, browser.NavigateCalls);
        Assert.DoesNotContain(runtime.Snapshot.Entries, entry => entry.Text.Contains("LATE-PAGE", StringComparison.Ordinal));
        Assert.True(await runtime.SubmitUserTextAsync("next"));
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(1, browser.NavigateCalls);
        var completed = runtime.Snapshot.Entries.Single(entry =>
            entry.Role == ConversationRole.Assistant && entry.Status == EntryStatus.Completed);
        Assert.Equal("Still here.", completed.Text);

        browser = new HoldingBrowser { Hold = true, LateTitle = "LATE-PAGE" };
        output = new CapturingSessionOutput();
        await using var steered = Runtime(browser, new CountingLease(), new OneNavigateModel(), output);
        await steered.AttachAsync();
        Assert.True(await steered.SubmitUserTextAsync("open the record"));
        await browser.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await steered.SubmitUserTextAsync("next"));
        await output.WaitForAsync(item => item.Payload is PlaybackStopOutput);
        browser.Release.TrySetResult();
        await steered.WaitUntilIdleAsync();
        Assert.Equal(1, browser.NavigateCalls);
        Assert.DoesNotContain(steered.Snapshot.Entries, entry => entry.Text.Contains("LATE-PAGE", StringComparison.Ordinal));
        Assert.Equal(
            "Still here.",
            steered.Snapshot.Entries.Single(entry =>
                entry.Role == ConversationRole.Assistant && entry.Status == EntryStatus.Completed).Text);
    }

    [Fact]
    public async Task Reconnect_keeps_one_answer_and_does_not_navigate_again()
    {
        var browser = new HoldingBrowser();
        var lease = new CountingLease();
        var store = new InMemoryMemoryStore();
        var output = new CapturingSessionOutput();
        var sessionId = Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940c201");
        await using (var runtime = Runtime(browser, lease, new OneNavigateModel(), output, store, sessionId))
        {
            await runtime.AttachAsync();
            Assert.True(await runtime.SubmitUserTextAsync("open the record"));
            await runtime.WaitUntilIdleAsync();
            Assert.Equal(1, browser.NavigateCalls);
            await runtime.DetachAsync();
            await runtime.WaitUntilIdleAsync();
        }

        Assert.Equal([sessionId], lease.Sessions);
        var paused = (await store.LoadAsync(sessionId))!;
        var restoredOutput = new CapturingSessionOutput();
        await using var restored = Runtime(browser, new CountingLease(), new OneNavigateModel(), restoredOutput, store, sessionId, paused);
        await restored.AttachAsync();
        await restored.WaitUntilMailboxDrainedAsync();
        Assert.Equal(1, browser.NavigateCalls);
        var answer = Assert.Single(restored.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Completed, answer.Status);
        Assert.Equal("Checked the record.", answer.Text);
        Assert.IsType<ReadyOutput>(restoredOutput.Items.Single(item => item.Payload is ReadyOutput).Payload);
    }

    [Fact]
    public async Task Provider_loss_keeps_history_and_does_not_navigate_again()
    {
        var browser = new HoldingBrowser { ErrorCode = "provider_unavailable" };
        var store = new InMemoryMemoryStore();
        var sessionId = Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940c301");
        await using var runtime = Runtime(browser, new CountingLease(), new OneNavigateModel(), store: store, sessionId: sessionId);
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitUserTextAsync("open the record"));
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(1, browser.NavigateCalls);
        var answer = runtime.Snapshot.Entries.Single(entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Completed, answer.Status);
        Assert.Equal("The browser is unavailable.", answer.Text);
        Assert.True(await runtime.SubmitUserTextAsync("next"));
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(1, browser.NavigateCalls);
        var loaded = (await store.LoadAsync(sessionId))!;
        Assert.Equal(runtime.Snapshot.Entries.Count, loaded.Entries.Count);
        Assert.Contains(loaded.Entries, entry => entry.Text == "The browser is unavailable.");
        Assert.Contains(loaded.Entries, entry => entry.Text == "Still here.");
    }

    [Fact]
    public async Task In_flight_browser_navigation_is_not_replayed()
    {
        Assert.Equal(ToolReplaySafety.NonReplayable, ToolCatalog.ReplaySafetyOf(ToolCatalog.BrowserNavigate));
        Assert.Equal(ToolReplaySafety.NonReplayable, ToolCatalog.ReplaySafetyOf(ToolCatalog.BrowserObserve));
        Assert.Equal(ToolReplaySafety.NonReplayable, ToolCatalog.ReplaySafetyOf(ToolCatalog.BrowserAct));

        var browser = new HoldingBrowser();
        var now = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        var generation = Guid.Parse("019944af-00c4-7000-8000-000000000001");
        var workId = Guid.Parse("019944af-00c4-7000-8000-000000000002");
        var owner = new WorkOwner(
            Guid.Parse("019944af-00c4-7000-8000-000000000003"),
            Guid.Parse("019944af-00c4-7000-8000-000000000004"));
        var call = new ModelToolCall("nav-1", ToolCatalog.BrowserNavigate, """{"url":"http://127.0.0.1:5091/"}""");
        var payload = DurableToolCallCheckpoint.Write([new ModelMessage(ModelRole.Assistant, "", ToolCalls: [call])]);
        var store = new InMemoryWorkItemStore();
        var created = await store.CreateAsync(WorkItem.Create(
            workId,
            owner,
            new WorkProvenance(
                Guid.Parse("019944af-00c4-7000-8000-000000000005"),
                WorkSourceKind.ApplicationEvent,
                null,
                null,
                null,
                "source|browser-replay",
                now,
                now,
                """{"instruction":"synthetic"}""",
                "general-assistant",
                11,
                "Test"),
            new WorkModelPin("synthetic-default", "synthetic", "synthetic-small", "minimal"),
            3,
            now));
        var claimed = (await store.TryClaimAsync(workId, generation, now, now.AddMinutes(5)))!;
        var checkpoint = new WorkCheckpoint(payload, 0, 0, (int)ToolLimits.Overall.TotalMilliseconds);
        var saved = await store.CheckpointAsync(
            workId,
            claimed.Revision,
            generation,
            checkpoint,
            null,
            now);
        var hash = ToolActionHash.Compute(
            ToolCatalog.BrowserNavigate,
            JsonDocument.Parse(call.ArgumentsJson).RootElement);
        var prepared = await store.MarkSideEffectAsync(
            workId,
            saved.Revision,
            generation,
            WorkSideEffectDisposition.Prepared,
            call.Id,
            hash,
            now);
        var fenced = await store.MarkSideEffectAsync(
            workId,
            prepared.Revision,
            generation,
            WorkSideEffectDisposition.InFlight,
            call.Id,
            hash,
            now);
        var model = new UnusedModel();
        var outcome = await new DurableOccurrenceExecution(
                new SessionToolExecutor(browser: browser, configurationGate: ToolConfigurationGates.AllowAll),
                TimeProvider.System)
            .RunAsync(
                fenced,
                new ModelRequest(Guid.NewGuid(), [new ModelMessage(ModelRole.User, "open the record")]),
                model,
                BrowserDefinition(),
                TriggerKind.ApplicationEvent,
                (_, _, _) => ValueTask.FromResult(fenced),
                store,
                generation,
                now,
                Ids("019944af-00c5-7000-8000-", "873f07d1-e264-4c81-a31b-7e59e940c5"),
                CancellationToken.None);
        var failed = Assert.IsType<DurableOccurrenceFailed>(outcome);
        Assert.Equal("side-effect-indeterminate", failed.Code);
        Assert.Equal(0, browser.NavigateCalls);
        Assert.Equal(0, model.Calls);
        Assert.Equal(WorkItemCreateKind.Created, created.Kind);
    }

    private static SessionRuntime Runtime(
        HoldingBrowser browser,
        IBrowserSessionLease lease,
        ILanguageModel model,
        CapturingSessionOutput? output = null,
        InMemoryMemoryStore? store = null,
        Guid? sessionId = null,
        SessionSnapshot? existing = null)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        var ids = Ids("019944af-00c3-7000-8000-", "873f07d1-e264-4c81-a31b-7e59e940c3");
        store ??= new InMemoryMemoryStore();
        var now = time.GetUtcNow();
        var snapshot = existing ?? new SessionSnapshot(
            1,
            sessionId ?? Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940c101"),
            1,
            BrowserDefinition(),
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
        if (existing is null)
        {
            store.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
        }

        return new SessionRuntime(
            snapshot,
            model,
            new DefaultAgentBrain(new PromptContextBuilder(ToolConfigurationGates.AllowAll, browser)),
            store,
            output ?? new CapturingSessionOutput(),
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            tools: new SessionToolExecutor(browser: browser, configurationGate: ToolConfigurationGates.AllowAll),
            browserLease: lease);
    }

    private static AgentDefinition BrowserDefinition() =>
        new(
            1,
            "general-assistant",
            11,
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

    private static DeterministicIdGenerator Ids(string eventPrefix, string sessionPrefix) =>
        new(
            Enumerable.Range(1, 256).Select(index => Guid.Parse($"{eventPrefix}{index:D12}")),
            [Guid.Parse($"{sessionPrefix}01")]);

    private sealed class StaticDefinitions(AgentDefinition definition) : IAgentDefinitionStore
    {
        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AgentDefinition>>([definition]);

        public ValueTask<AgentDefinition?> GetAsync(string id, int? version = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AgentDefinition?>(
                string.Equals(id, definition.Id, StringComparison.Ordinal)
                && version is null or 1
                    ? definition
                    : null);
    }

    private sealed class CountingLease : IBrowserSessionLease
    {
        public List<Guid> Sessions { get; } = [];

        public ValueTask ReleaseAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            Sessions.Add(sessionId);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class HoldingBrowser : IBrowserSession
    {
        public bool IsAvailable { get; set; } = true;

        public BrowserHostPolicy HostPolicy { get; set; } = new(
            true,
            true,
            BrowserInteractionMode.InteractiveDemo,
            FixtureOrigin);

        public bool Hold { get; init; }

        public string? ErrorCode { get; init; }

        public string LateTitle { get; init; } = "Record lookup";

        public int NavigateCalls { get; private set; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<Uri?> GetCurrentUrlAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            new((Uri?)null);

        public async ValueTask<BrowserOperationResult> NavigateAsync(
            BrowserNavigateRequest request,
            CancellationToken cancellationToken = default)
        {
            NavigateCalls++;
            Entered.TrySetResult();
            if (Hold)
            {
                await Release.Task;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (ErrorCode is not null)
            {
                return new BrowserOperationResult(ErrorCode, null);
            }

            return new BrowserOperationResult(
                null,
                new BrowserObservation(request.Url!.AbsoluteUri, LateTitle, "Search", false, []));
        }

        public ValueTask<BrowserOperationResult> ObserveAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            new(new BrowserOperationResult("provider_unavailable", null));

        public ValueTask<BrowserOperationResult> ActAsync(BrowserActRequest request, CancellationToken cancellationToken = default) =>
            new(new BrowserOperationResult("provider_unavailable", null));
    }

    private sealed class OneNavigateModel : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            var user = request.Messages.LastOrDefault(message => message.Role == ModelRole.User)?.Text ?? "";
            var tool = request.Messages.LastOrDefault(message => message.Role == ModelRole.Tool);
            if (tool is null && user.Contains("open the record", StringComparison.Ordinal))
            {
                yield return new ModelToolCallEvent(new ModelToolCall(
                    "nav-1",
                    ToolCatalog.BrowserNavigate,
                    """{"url":"http://127.0.0.1:5091/"}"""));
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
                yield break;
            }

            var text = tool?.Text.Contains("provider_unavailable", StringComparison.Ordinal) == true
                ? "The browser is unavailable."
                : user.Contains("open the record", StringComparison.Ordinal)
                    ? "Checked the record."
                    : "Still here.";
            yield return new ModelSemanticResponseReady(new ModelSemanticResponse(
                text,
                new ModelSpeechProjection(ModelSpeechMode.Same, null),
                []));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }

    private sealed class UnusedModel : ILanguageModel
    {
        public int Calls { get; private set; }

        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }
    }
}
