using AgentCore.Application.Execution;
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
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

// Owned Stop/steer, reconnect and provider-loss parity is in
// TenantSessionAccessTests.BrowserLifecycleParity; actual background pin/dispatch
// in BrowserBackgroundBudget. TenantBrowserApprovalTests.FreshCheckpoint retains
// immutable operation fences after current reads/refusals and checkpoint recreation.
// Unknown durable native work remains blocked; no legacy read resets that authority.
public sealed class BrowserLifecycleTests
{
    private static readonly string[] FixtureOrigin = ["http://127.0.0.1:5091"];

    [Fact]
    public async Task End_and_dispose_release_the_browser_session()
    {
        var browser = new HoldingBrowser();
        var lease = new CountingLease();
        var runtime = Runtime(browser, lease, new OneNavigateModel());
        await runtime.AttachAsync();
        Assert.True(await runtime.RequestEndAsync());
        Assert.Equal([runtime.SessionId], lease.Sessions);
        await runtime.DisposeAsync();
        Assert.Equal([runtime.SessionId, runtime.SessionId], lease.Sessions);
    }

    [Fact]
    public async Task In_flight_browser_navigation_is_not_replayed()
    {
        Assert.Equal(ToolReplaySafety.NonReplayable, ToolCatalog.ReplaySafetyOf(ToolCatalog.BrowserNavigate));
        Assert.Equal(ToolReplaySafety.NonReplayable, ToolCatalog.ReplaySafetyOf(ToolCatalog.BrowserSnapshot));
        Assert.Equal(ToolReplaySafety.NonReplayable, ToolCatalog.ReplaySafetyOf(ToolCatalog.BrowserClick));

        var now = DateTimeOffset.Parse("2026-10-08T00:00:00Z");
        var owner = new AgentRunOwner(Guid.NewGuid(), Guid.NewGuid());
        var memory = new InMemoryMemoryStore();
        var store = new InMemoryAgentRunStore(memory, new SystemDiagnosticIdSource());
        var snapshot = AgentRunTestFixtures.Snapshot(owner, BrowserDefinition(), now);
        var run = (await store.AdmitAsync(snapshot, 0, AgentRunTestFixtures.Run(snapshot, now))).Run;
        var generation = Guid.NewGuid();
        run = await store.ApplyAsync(owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, now, generation, now.AddMinutes(5)));
        var call = new ModelToolCall("nav-1", ToolCatalog.BrowserNavigate, "{\"url\":\"http://127.0.0.1:5091/\"}");
        var payload = AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [call])], executionState: new(TranscriptId: run.Admission.ResponseId));
        run = await store.ApplyAsync(owner, run.AgentRunId, new AgentRunCommand.Checkpoint(run.Revision, now, generation,
            new(payload, 1, 0, (int)ToolLimits.Overall.TotalMilliseconds), null));
        var hash = ToolActionHash.Compute(call.Name, JsonDocument.Parse(call.ArgumentsJson).RootElement);
        foreach (var disposition in new[] { AgentRunSideEffectDisposition.Prepared, AgentRunSideEffectDisposition.InFlight })
            run = await store.ApplyAsync(owner, run.AgentRunId, new AgentRunCommand.MarkSideEffect(run.Revision, now, generation, disposition, call.Id, hash));
        var recovered = await store.ApplyAsync(owner, run.AgentRunId, new AgentRunCommand.Recover(run.Revision, now.AddMinutes(6)));
        Assert.Equal(AgentRunStatus.Failed, recovered.Status);
        Assert.Equal("side-effect-indeterminate", recovered.Failure!.Code);
        Assert.Empty(await store.ListRunnableAsync(now.AddMinutes(6), 10));
    }

    private sealed class BoundBudgetModel : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: true);
        private int calls;
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            var step = calls++;
            yield return new ModelToolCallEvent(step < 26
                ? new("snapshot-" + step, ToolCatalog.BrowserSnapshot, "{}")
                : new("finish", ToolCatalog.WorkComplete, "{\"summary\":\"Checked 26 changing pages.\",\"attentionRequired\":false,\"outcome\":\"NoAction\"}"));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }

    private sealed class RecoveryModel(ModelToolCall original) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: true);
        public TaskCompletionSource AfterSnapshot { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Results { get; } = [];
        private int requests;
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Results.AddRange(request.Messages.Where(m => m.Role == ModelRole.Tool).Select(m => m.Text));
            var step = requests++;
            if (step == 1)
            {
                AfterSnapshot.TrySetResult();
                await Continue.Task.WaitAsync(ct);
            }
            var call = step switch
            {
                0 => new ModelToolCall("snapshot", ToolCatalog.BrowserSnapshot, "{}"),
                1 => original with { Id = "repeat" },
                2 => new ModelToolCall("new", ToolCatalog.BrowserClick, "{\"target\":{\"by\":\"role\",\"value\":\"button\",\"name\":\"el_bbbbbbbbbbbbbbbbbbbbbb\"}}"),
                _ => null
            };
            if (call is not null)
            {
                yield return new ModelToolCallEvent(call);
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
            }
            else
            {
                yield return new ModelSemanticResponseReady(new("Fresh evidence checked.", new(ModelSpeechMode.Same, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed);
            }
        }
    }

    private static SessionRuntime Runtime(
        HoldingBrowser browser,
        IBrowserLease lease,
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
            now, AgentInstanceId: Guid.NewGuid());
        if (existing is null)
        {
            store.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
        }

        return SessionRuntimeFixture.Create(
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
            2,
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
            new DefinitionExecutionPolicy(ToolAllowlist:
            [
                ToolCatalog.BrowserNavigate,
                ToolCatalog.BrowserSnapshot,
                ToolCatalog.BrowserClick
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

    private sealed class CountingLease : IBrowserLease
    {
        public List<Guid> Sessions { get; } = [];

        public ValueTask ReleaseAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            Sessions.Add(sessionId);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class HoldingBrowser : IBrowser, IBrowserContextUse
    {
        public BrowserProviderDescriptor Provider { get; } = new("fixture", "Test browser", new HashSet<BrowserFeature> { BrowserFeature.Navigate, BrowserFeature.Snapshot, BrowserFeature.Click, BrowserFeature.Type, BrowserFeature.Hover, BrowserFeature.Drag, BrowserFeature.FillForm, BrowserFeature.SelectOption, BrowserFeature.PressKey, BrowserFeature.Upload, BrowserFeature.FillCredential, BrowserFeature.Wait, BrowserFeature.Tabs, BrowserFeature.Screenshot, BrowserFeature.Close });
        public bool IsAvailable { get; set; } = true;

        public BrowserHostPolicy HostPolicy { get; set; } = new(
            true,
            true,
            BrowserInteractionMode.InteractiveDemo,
            FixtureOrigin);

        public int UnattendedLeases { get; private set; }
        public ValueTask<IAsyncDisposable> EnterUnattendedAsync(Guid owner, IReadOnlyList<string>? origins, CancellationToken ct = default)
        {
            UnattendedLeases++;
            return ValueTask.FromResult<IAsyncDisposable>(new FixtureLease());
        }
        public void AdoptUnattendedFlow(Guid owner) { }
        private sealed class FixtureLease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
        public bool SuccessfulSnapshot { get; init; }
        public int SnapshotCalls { get; private set; }
        public int InteractCalls { get; private set; }
        public string? LastInteractionRef { get; private set; }
        public bool Hold { get; init; }

        public string? ErrorCode { get; init; }

        public string LateTitle { get; init; } = "Record lookup";

        public int NavigateCalls { get; private set; }

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<Uri?> GetCurrentUrlAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            new(SuccessfulSnapshot ? new Uri(FixtureOrigin[0]) : null);

        public async ValueTask<BrowserResult> NavigateAsync(
            BrowserRequest request,
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
                return new BrowserResult(ErrorCode, null);
            }

            return new BrowserResult(
                null,
                new BrowserSnapshot(((BrowserNavigate)request.Command).Url!, LateTitle, "Search", false, []));
        }

        public ValueTask<BrowserResult> SnapshotAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            SnapshotCalls++;
            return new(SuccessfulSnapshot ? new(null, new BrowserSnapshot(FixtureOrigin[0], "Fresh page", "Record " + SnapshotCalls, false,
                [new(new BrowserTarget("role", "button", Name: "Save"), "button", "Save", ["click"])])) : new BrowserResult("provider_unavailable", null));
        }

        public ValueTask<BrowserResult> InteractAsync(BrowserRequest request, CancellationToken cancellationToken = default)
        {
            InteractCalls++;
            LastInteractionRef = ((BrowserClick)request.Command).Target.Name;
            return new(new BrowserResult(null, new BrowserSnapshot(FixtureOrigin[0], "Saved", "Saved", false, [])));
        }

        public ValueTask<BrowserResult> ExecuteAsync(BrowserRequest request, CancellationToken ct = default) => request.Operation switch
        {
            BrowserOperation.Navigate => NavigateAsync(request, ct),
            BrowserOperation.Snapshot or BrowserOperation.WaitFor => SnapshotAsync(request.SessionId, ct),
            BrowserOperation.Click or BrowserOperation.Type or BrowserOperation.Hover or BrowserOperation.Drag or BrowserOperation.Upload or BrowserOperation.FillForm => InteractAsync(request, ct),
            _ => new(new BrowserResult("unsupported_operation")),
        };
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
