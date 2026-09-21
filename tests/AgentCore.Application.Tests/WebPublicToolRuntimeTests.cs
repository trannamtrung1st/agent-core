using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class WebPublicToolRuntimeTests
{
    public const string EphemeralWebMarker = "P3C2_EPHEMERAL_WEB_SNIPPET_XYZZY";

    [Fact]
    public async Task Web_search_result_text_is_not_persisted_in_snapshot_history()
    {
        var store = new InMemoryMemoryStore();
        var search = new MarkerWebSearch(EphemeralWebMarker);
        var tools = new SessionToolExecutor(webSearch: search, configurationGate: ToolConfigurationGates.AllowAll);
        var output = new CapturingSessionOutput();
        var definition = await LoadGeneralV2Async();
        await using var runtime = CreateRuntime(output, store, definition, new WebSearchLoopModel(), tools);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("Search the public web for agent core."));
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await output.WaitForAsync(item => item.Payload is ResponseCompletedOutput, wait.Token);
        await runtime.WaitUntilIdleAsync();

        var persisted = (await store.LoadAsync(runtime.SessionId))!;
        var combined = string.Join('\n', persisted.Entries.Select(entry => entry.Text));
        Assert.DoesNotContain(EphemeralWebMarker, combined, StringComparison.Ordinal);
        Assert.All(persisted.Entries, entry => Assert.True(entry.Role is ConversationRole.User or ConversationRole.Assistant));
    }

    [Fact]
    public async Task Web_search_honors_runtime_cancel_before_provider_returns()
    {
        var search = new GatedWebSearch();
        var tools = new SessionToolExecutor(webSearch: search, configurationGate: ToolConfigurationGates.AllowAll);
        var output = new CapturingSessionOutput();
        var definition = await LoadGeneralV2Async();
        await using var runtime = CreateRuntime(output, new InMemoryMemoryStore(), definition, new WebSearchLoopModel(), tools);
        await runtime.AttachAsync();

        Assert.True(await runtime.SubmitUserTextAsync("Search please."));
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await search.Entered.WaitAsync(wait.Token);
        var started = await output.WaitForAsync(
            item => item.Payload is ResponseProgressOutput progress
                && progress.Kind == ResponseProgressKind.RunningTool
                && progress.State == ResponseProgressState.Started,
            wait.Token);
        var staleResponseId = started.ResponseId;
        Assert.True(await runtime.SubmitUserTextAsync("second", behavior: UserTextBehavior.Interrupt));
        await output.WaitForAsync(
            item => item.ResponseId == staleResponseId
                && item.Payload is ResponseProgressOutput progress
                && progress.State == ResponseProgressState.Failed,
            wait.Token);
        search.Release();
        await runtime.WaitUntilIdleAsync();
        Assert.DoesNotContain(
            output.Items,
            item => item.ResponseId == staleResponseId
                && item.Payload is ResponseProgressOutput progress
                && progress.State == ResponseProgressState.Completed);
    }

    [Fact]
    public async Task Web_search_stops_after_runtime_epoch_bump_from_deactivate()
    {
        var search = new GatedWebSearch();
        var tools = new SessionToolExecutor(webSearch: search, configurationGate: ToolConfigurationGates.AllowAll);
        var output = new CapturingSessionOutput();
        var definition = await LoadGeneralV2Async();
        await using var runtime = CreateRuntime(output, new InMemoryMemoryStore(), definition, new WebSearchLoopModel(), tools);
        await runtime.AttachAsync();
        var epochBefore = runtime.Snapshot.RuntimeEpoch;

        Assert.True(await runtime.SubmitUserTextAsync("Search please."));
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await search.Entered.WaitAsync(wait.Token);
        await output.WaitForAsync(
            item => item.Payload is ResponseProgressOutput progress && progress.State == ResponseProgressState.Started,
            wait.Token);
        Assert.True(await runtime.RequestDeactivateAsync());
        await runtime.WaitUntilIdleAsync();
        search.Release();

        Assert.True(runtime.Snapshot.RuntimeEpoch > epochBefore);
        Assert.DoesNotContain(
            output.Items,
            item => item.Payload is ResponseProgressOutput progress
                && progress.Kind == ResponseProgressKind.RunningTool
                && progress.State == ResponseProgressState.Completed);
    }

    private static async Task<AgentDefinition> LoadGeneralV2Async()
    {
        var store = new FileAgentDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
        return (await store.GetAsync("general-assistant", 2))!;
    }

    private static string FindAgents()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "agents");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("agents directory not found.");
    }

    private static SessionRuntime CreateRuntime(
        ISessionOutput output,
        InMemoryMemoryStore store,
        AgentDefinition definition,
        ILanguageModel model,
        SessionToolExecutor tools)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 256).Select(index => Guid.Parse($"019944af-00c2-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940bf01")]);
        var now = time.GetUtcNow();
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
            now);
        store.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
        return new SessionRuntime(
            snapshot,
            model,
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            tools: tools);
    }

    private sealed class WebSearchLoopModel : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Messages.Any(message => message.Role == ModelRole.Tool))
            {
                yield return new ModelTextDelta("done");
                yield return new ModelCompleted(ModelStopReason.Completed);
                yield break;
            }

            var lastUser = request.Messages.LastOrDefault(message => message.Role == ModelRole.User)?.Text ?? string.Empty;
            if (lastUser.Equals("second", StringComparison.Ordinal))
            {
                yield return new ModelTextDelta("ack");
                yield return new ModelCompleted(ModelStopReason.Completed);
                yield break;
            }

            yield return new ModelToolCallEvent(new ModelToolCall(
                "web-1",
                ToolCatalog.WebSearch,
                """{"query":"agent core","limit":3}"""));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }

    private sealed class MarkerWebSearch(string marker) : IWebSearchProvider
    {
        public bool IsAvailable => true;

        public ValueTask<WebSearchResult> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new WebSearchResult(
                [new WebSearchItem("t", "https://example.test", marker)],
                false));
        }
    }

    private sealed class GatedWebSearch : IWebSearchProvider
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string? LastQuery { get; private set; }

        public Task Entered => _entered.Task;

        public bool IsAvailable => true;

        public void Release() => _release.TrySetResult();

        public async ValueTask<WebSearchResult> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            LastQuery = request.Query;
            return new WebSearchResult(
                [new WebSearchItem("t", "https://example.test", "ok")],
                false);
        }
    }
}
