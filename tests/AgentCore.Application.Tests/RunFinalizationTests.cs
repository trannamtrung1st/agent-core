using System.Runtime.CompilerServices;
using AgentCore.Application.Ports;
using AgentCore.Application.Testing;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Tests;

public sealed partial class TerminalDisplayRepairTests
{
    [Fact]
    public async Task Provider_total_timeout_after_close_recovers_once_without_replaying_effects()
    {
        var browser = new CountingBrowser();
        var model = new RecordingModel(
            [new ModelToolCallEvent(new("close", ToolCatalog.BrowserClose, "{}")), new ModelCompleted(ModelStopReason.ToolCalls)],
            [new ModelFailed(new(ProviderErrorCode.Timeout, "Provider timed out.", FailureReason: "totalTimeout"))],
            FinalAnswer());
        await using var runtime = Create(model, browser, ToolCatalog.BrowserClose);
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitUserTextAsync("Close the browser"));
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(1, browser.CloseCalls);
        Assert.Equal(3, model.Calls);
        Assert.Null(model.Requests.Last().Tools);
        Assert.NotNull(model.Requests.Last().ResponseContract);
        Assert.Equal(EntryStatus.Completed, runtime.Snapshot.Entries.Last(e => e.Role == ConversationRole.Assistant).Status);
    }

    [Fact]
    public async Task Last_browser_action_near_cutoff_defers_remaining_batch_without_replay()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-09T12:00:00Z"));
        var browser = new CountingBrowser { OnClose = () => clock.Advance(TimeSpan.FromSeconds(260)) };
        var model = new RecordingModel(
            [new ModelToolCallEvent(new("close", ToolCatalog.BrowserClose, "{}")),
             new ModelToolCallEvent(new("unexecuted", ToolCatalog.BrowserClose, "{}")), new ModelCompleted(ModelStopReason.ToolCalls)], FinalAnswer());
        await using var runtime = Create(model, browser, clock, ToolCatalog.BrowserClose);
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitUserTextAsync("Close the browser"));
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(1, browser.CloseCalls);
        Assert.Null(model.Requests.Last().Tools);
        var run = Assert.Single(await SessionRuntimeFixture.RunsForAsync(runtime));
        Assert.Contains("finish_required", run.Checkpoint!.PayloadJson);
        Assert.Equal(AgentRunStatus.Completed, run.Status);
    }

    [Fact]
    public async Task User_stop_after_closure_is_not_retried_as_a_provider_cancellation()
    {
        var model = new FinalizationHoldingModel();
        var browser = new CountingBrowser();
        await using var runtime = Create(model, browser, ToolCatalog.BrowserClose);
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitUserTextAsync("Close the browser"));
        await model.Holding.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await runtime.CancelActiveResponseAsync();
        model.Release.TrySetResult();
        await runtime.WaitUntilIdleAsync();
        var entry = runtime.Snapshot.Entries.Last(e => e.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Interrupted, entry.Status);
        Assert.Equal("userStop", entry.InterruptReason);
        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(1, browser.CloseCalls);
        Assert.Single(entry.Envelope!.EffectReceipts!);
    }

    [Fact]
    public async Task Finalization_rejects_new_tools_and_preserves_effects_without_another_retry()
    {
        var browser = new CountingBrowser();
        var model = new RecordingModel(
            [new ModelToolCallEvent(new("close", ToolCatalog.BrowserClose, "{}")), new ModelCompleted(ModelStopReason.ToolCalls)],
            [new ModelFailed(new(ProviderErrorCode.Timeout, "Provider timed out.", FailureReason: "totalTimeout"))],
            [new ModelToolCallEvent(new("again", ToolCatalog.BrowserClose, "{}")), new ModelCompleted(ModelStopReason.ToolCalls)]);
        await using var runtime = Create(model, browser, ToolCatalog.BrowserClose);
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitUserTextAsync("Close the browser"));
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(1, browser.CloseCalls);
        var failed = runtime.Snapshot.Entries.Last(e => e.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Failed, failed.Status);
        Assert.Equal("finalizationToolCall", failed.Failure!.FailureReason);
        Assert.Single(failed.Envelope!.EffectReceipts!);
        Assert.Equal(3, model.Calls);
    }

    [Fact]
    public async Task Work_cutoff_leaves_time_for_tool_free_reply_after_committed_close()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-09T12:00:00Z"));
        var browser = new CountingBrowser();
        var model = new FinalizationHoldingModel();
        await using var runtime = Create(model, browser, clock, ToolCatalog.BrowserClose);
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitUserTextAsync("Close the browser"));
        await model.Holding.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(270));
        model.Release.TrySetResult();
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(1, browser.CloseCalls);
        Assert.Equal(3, model.Requests.Count);
        Assert.Null(model.Requests.Last().Tools);
        Assert.NotNull(model.Requests.Last().ResponseContract);
        Assert.Equal(EntryStatus.Completed, runtime.Snapshot.Entries.Last(e => e.Role == ConversationRole.Assistant).Status);
        var run = Assert.Single(await SessionRuntimeFixture.RunsForAsync(runtime));
        Assert.Contains("Core finalization phase: runDeadline", run.Checkpoint!.PayloadJson);
    }

    [Fact]
    public async Task Hard_deadline_is_a_run_timeout_with_preserved_close_and_follow_up_failure_facts()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-09T12:00:00Z"));
        var browser = new CountingBrowser();
        var model = new FinalizationHoldingModel();
        var logger = new FinalizationLogger();
        await using var runtime = CreateCore(model, browser, clock, [ToolCatalog.BrowserClose], logger: logger);
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitUserTextAsync("Close the browser"));
        await model.Holding.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(301));
        model.Release.TrySetResult();
        await runtime.WaitUntilIdleAsync();
        var failed = runtime.Snapshot.Entries.Last(e => e.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Failed, failed.Status);
        Assert.Equal("runDeadline", failed.Failure!.FailureReason);
        Assert.Equal("Browser closed", Assert.Single(failed.Envelope!.EffectReceipts!).Label);
        Assert.True(await runtime.SubmitUserTextAsync("Why did that fail?"));
        await runtime.WaitUntilIdleAsync();
        Assert.True(model.Requests.Count == 3, "Requests=" + model.Requests.Count + "; Runs=" + string.Join(",", (await SessionRuntimeFixture.RunsForAsync(runtime)).Select(r => r.Status + ":" + r.Failure?.Code)) + "; Errors=" + string.Join(";", logger.Errors));
        var facts = Assert.Single(model.Requests.Last().Messages, m => m.Role == ModelRole.System && m.Text.StartsWith("Trusted Core execution facts (previous"));
        Assert.Contains("run-deadline", facts.Text);
        Assert.Contains("browser.close", facts.Text);
        Assert.DoesNotContain("sign-out confirmed", facts.Text);
        Assert.Equal(1, browser.CloseCalls);
    }

    private sealed class FinalizationHoldingModel : ILanguageModel
    {
        public List<ModelRequest> Requests { get; } = [];
        public TaskCompletionSource Holding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            if (Requests.Count == 1)
            {
                yield return new ModelToolCallEvent(new("close", ToolCatalog.BrowserClose, "{}"));
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
            }
            else if (Requests.Count == 2)
            {
                Holding.TrySetResult();
                await Release.Task;
                yield return new ModelFailed(new(ProviderErrorCode.Cancelled, "Generation cancelled."));
            }
            else foreach (var evt in FinalAnswer()) yield return evt;
        }
    }

    private static ModelGenerationEvent[] FinalAnswer() =>
    [new ModelSemanticResponseReady(new("Browser closure is recorded; sign-out was not verified.", new(ModelSpeechMode.Same, null), [])),
     new ModelCompleted(ModelStopReason.Completed)];

    private sealed class FinalizationLogger : ILogger
    {
        public List<string> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (exception is not null) Errors.Add(exception.ToString()); }
    }
}
