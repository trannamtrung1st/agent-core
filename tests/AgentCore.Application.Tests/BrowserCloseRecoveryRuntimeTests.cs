using System.Runtime.CompilerServices;
using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Browser;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed partial class TerminalDisplayRepairTests
{
    [Theory]
    [InlineData(false, "{}", "{\"unexpected\":true}")]
    [InlineData(true, "{}", "{\"unexpected\":true}")]
    [InlineData(false, " ", "")]
    public async Task Recovered_owned_run_executes_pending_corrected_close_once_after_invalid_strategy_exhaustion(bool alreadyClosed, string correctedPayload, string previousInvalidPayload)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-10T00:00:00Z"));
        var owner = new AgentRunOwner(Guid.NewGuid(), Guid.NewGuid());
        var memory = new InMemoryMemoryStore();
        var runs = new InMemoryAgentRunStore(memory, new SystemDiagnosticIdSource());
        var definition = SampleDefinitions.Examiner with { Voice = new(false, "default", 1), Environment = new(ToolAllowlist: [ToolCatalog.BrowserClose]) };
        var snapshot = AgentRunTestFixtures.Snapshot(owner, definition, clock.GetUtcNow());
        var run = (await runs.AdmitAsync(snapshot, 0, AgentRunTestFixtures.Run(snapshot, clock.GetUtcNow()))).Run;
        run = await runs.ApplyAsync(owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, clock.GetUtcNow(), Guid.NewGuid(), clock.GetUtcNow().AddSeconds(1)));
        var recovery = new InvalidToolCallRecovery([]);
        var messages = new List<ModelMessage>();
        for (var i = 0; i < 4; i++)
        {
            var call = new ModelToolCall("bad-close-" + i, ToolCatalog.BrowserClose, previousInvalidPayload);
            messages.Add(new(ModelRole.Assistant, "", ToolCalls: [call]));
            messages.Add(new(ModelRole.Tool, recovery.Note(call, recovery.Refuse(call) ?? "{\"error\":\"invalid\"}", out _), ToolCallId: call.Id, Name: call.Name));
        }
        var corrected = new ModelToolCall("pending-corrected-close", ToolCatalog.BrowserClose, correctedPayload, "opaque-provider-continuation");
        messages.Add(new(ModelRole.Assistant, "", ToolCalls: [corrected]));
        run = await runs.ApplyAsync(owner, run.AgentRunId, new AgentRunCommand.Checkpoint(run.Revision, clock.GetUtcNow(), run.Claim!.Generation,
            new(AgentRunToolCallCheckpoint.Write(messages), 5, 0, 240000), null));
        clock.Advance(TimeSpan.FromSeconds(2));
        run = await runs.ApplyAsync(owner, run.AgentRunId, new AgentRunCommand.Recover(run.Revision, clock.GetUtcNow()));
        run = await runs.ApplyAsync(owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, clock.GetUtcNow(), Guid.NewGuid(), clock.GetUtcNow().AddMinutes(5)));
        var responseId = run.ResponseId;
        var browser = AdaptiveBrowserRuntimeTests.NewBrowser();
        await browser.StartAsync(default);
        try
        {
            await browser.ExecuteAsync(new(snapshot.SessionId, new BrowserNavigate(browser.HostPolicy.NavigationOrigins.Single() + "/")));
            var page = browser.ContextFor(snapshot.SessionId)!.Pages.Single();
            if (alreadyClosed) await browser.ExecuteAsync(new(snapshot.SessionId, new BrowserClose()));
            var model = new RecoveredCloseModel(alreadyClosed);
            await using var runtime = SessionRuntimeFixture.Create((await memory.LoadAsync(snapshot.SessionId))!, model,
                new DefaultAgentBrain(new PromptContextBuilder(ToolConfigurationGates.AllowAll, browser)), memory,
                new CapturingSessionOutput(), new SystemIdGenerator(clock), clock, NullLogger.Instance, agentRuns: runs,
                tools: new SessionToolExecutor(browser: browser, configurationGate: ToolConfigurationGates.AllowAll));
            await runtime.AttachAsync();
            Assert.True(await runtime.DispatchAgentRunAsync(run.AgentRunId, false));
            await runtime.WaitUntilIdleAsync();
            var completed = (await runs.GetAsync(owner, run.AgentRunId))!;
            Assert.Equal(AgentRunStatus.Completed, completed.Status);
            Assert.Equal(responseId, completed.ResponseId);
            Assert.True(page.IsClosed);
            Assert.Null(browser.ContextFor(snapshot.SessionId));
            Assert.Equal(1, model.Requests);
            Assert.True(AgentRunToolCallCheckpoint.TryRead(completed.Checkpoint, out var restored));
            Assert.Equal(corrected, Assert.Single(restored!.SelectMany(m => m.ToolCalls ?? []), c => c.Id == corrected.Id));
            Assert.Single(restored!, m => m.Role == ModelRole.Tool && m.ToolCallId == corrected.Id);
            Assert.Empty(AgentRunToolCallCheckpoint.PendingCalls(restored!));
            if (!string.IsNullOrWhiteSpace(previousInvalidPayload)) Assert.NotNull(new InvalidToolCallRecovery(restored!).Refuse(new("again", ToolCatalog.BrowserClose, previousInvalidPayload)));
        }
        finally { await browser.StopAsync(default); }
    }

    private sealed class RecoveredCloseModel(bool alreadyClosed) : ILanguageModel
    {
        public int Requests { get; private set; }
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests++;
            var receipt = Assert.Single(request.Messages, m => m.Role == ModelRole.Tool && m.ToolCallId == "pending-corrected-close");
            Assert.Contains(alreadyClosed ? "already_closed" : "\"status\":\"closed\"", receipt.Text);
            Assert.Contains("applicationOutcomeVerified\":false", receipt.Text);
            yield return new ModelSemanticResponseReady(new("Provider confirmed browser closure; application logout remains unverified.", new(ModelSpeechMode.Same, null), []));
            yield return new ModelCompleted(ModelStopReason.Completed);
            await Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(false, "{}")]
    [InlineData(true, "{}")]
    [InlineData(true, " ")]
    public async Task Owned_run_corrects_invalid_close_without_disabling_cleanup_or_unrelated_tools(bool cleanup, string corrected)
    {
        var browser = AdaptiveBrowserRuntimeTests.NewBrowser();
        await browser.StartAsync(default);
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-10T00:00:00Z"));
        var model = new CloseRecoveryModel(clock, cleanup, corrected);
        await using var runtime = CreateCore(model, browser, clock, [ToolCatalog.BrowserClose, ToolCatalog.BrowserSnapshot]);
        try
        {
            await browser.ExecuteAsync(new(runtime.SessionId, new BrowserNavigate(browser.HostPolicy.NavigationOrigins.Single() + "/")));
            var page = browser.ContextFor(runtime.SessionId)!.Pages.Single();
            await runtime.AttachAsync();
            Assert.True(await runtime.SubmitUserTextAsync("Inspect this browser and close it, even if inspection cannot finish."));
            await runtime.WaitUntilIdleAsync();
            Assert.Equal(EntryStatus.Completed, runtime.Snapshot.Entries.Last(e => e.Role == ConversationRole.Assistant).Status);
            Assert.True(page.IsClosed);
            Assert.Null(browser.ContextFor(runtime.SessionId));
            Assert.Equal(8, model.Requests.Count);
            var run = Assert.Single(await SessionRuntimeFixture.RunsForAsync(runtime));
            Assert.Contains("invalidCallRecovery", run.Checkpoint!.PayloadJson);
            Assert.Contains("close-recovery-5", run.Checkpoint.PayloadJson);
            if (cleanup) Assert.Contains("Core browser cleanup phase", run.Checkpoint.PayloadJson);
            var effects = runtime.Snapshot.Entries.Last(e => e.Role == ConversationRole.Assistant).Envelope!.EffectReceipts!;
            Assert.Single(effects, r => r.Tool == ToolCatalog.BrowserClose && r.Status == "closed");
            Assert.Single(effects, r => r.Tool == ToolCatalog.BrowserClose && r.Status == "already_closed");
        }
        finally { await browser.StopAsync(default); }
    }

    private sealed class CloseRecoveryModel(FakeTimeProvider clock, bool cleanup, string corrected) : ILanguageModel
    {
        public List<ModelRequest> Requests { get; } = [];
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            var step = Requests.Count - 1;
            var receipts = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
            if (step is 1 or 2) Assert.Contains("\"error\":\"invalid\"", receipts.Last().Text);
            if (step is 3 or 4) Assert.Contains("invalid_tool_strategy_blocked", receipts.Last().Text);
            if (step == 3 && cleanup) clock.Advance(TimeSpan.FromSeconds(220));
            if (step == 5) Assert.DoesNotContain("\"error\"", receipts.Last().Text);
            if (step == 6) Assert.Contains("\"status\":\"closed\"", receipts.Last().Text);
            if (step == 7)
            {
                Assert.Contains("already_closed", receipts.Last().Text);
                Assert.Contains("applicationOutcomeVerified\":false", receipts.Last().Text);
                yield return new ModelSemanticResponseReady(new("Browser closure confirmed by the provider. Application logout was not verified.", new(ModelSpeechMode.Same, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed);
                yield break;
            }
            var name = step == 4 ? ToolCatalog.BrowserSnapshot : ToolCatalog.BrowserClose;
            Assert.Contains(request.Tools ?? [], t => t.Name == name);
            yield return new ModelToolCallEvent(new("close-recovery-" + step, name, step < 4 ? "{\"unexpected\":true}" : step == 5 ? corrected : "{}"));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
            await Task.CompletedTask;
        }
    }
}
