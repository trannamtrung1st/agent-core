using AgentCore.Application.Execution;
using AgentCore.Application.Sessions;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tests;

public sealed partial class TerminalDisplayRepairTests
{
    [Fact]
    public async Task Accumulated_exact_write_arguments_exhaust_checkpoint_before_another_effect()
    {
        var root = Path.Combine(Path.GetTempPath(), "budget-capacity-" + Guid.NewGuid().ToString("N"));
        try
        {
            var workspace = new AgentCore.Infrastructure.Workspaces.FileSessionWorkspace(Path.Combine(root, "ws"), Path.Combine(root, "templates"), sessions: new WorkspaceTestSessions());
            var rounds = Enumerable.Range(0, 50).Select(i => new ModelGenerationEvent[] {
                new ModelToolCallEvent(new("write-" + i, ToolCatalog.WorkspaceWrite, System.Text.Json.JsonSerializer.Serialize(new { path = "/working/part-" + i + ".txt", content = new string('x', 7000) + i }))), new ModelCompleted(ModelStopReason.ToolCalls) }).ToArray();
            var model = new CapacityFinalizationModel(rounds);
            var executor = new SessionToolExecutor(workspace: workspace, agentWorkspace: OwnedWorkspaces.Create(workspace), configurationGate: ToolConfigurationGates.AllowAll);
            await using var runtime = CreateCore(model, null, null, [ToolCatalog.WorkspaceWrite], executor: executor,
                executionBudgets: new(Standard: new(144, 900)));
            await runtime.AttachAsync(); Assert.True(await runtime.SubmitUserTextAsync("Write each requested part, then report the saved parts.")); await runtime.WaitUntilIdleAsync();
            var run = Assert.Single(await SessionRuntimeFixture.RunsForAsync(runtime));
            Assert.Equal(AgentRunStatus.Completed, run.Status);
            Assert.Equal("checkpointCapacity", RunBudgetDiagnosticProjection.From(run)!.TerminationReason);
            Assert.InRange(run.Checkpoint!.StepCount, 20, 49);
            Assert.True(AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages));
            var writes = messages!.Where(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.WorkspaceWrite && m.Text.Contains("\"bytes\":") && !m.Text.Contains("\"error\":")).ToArray();
            Assert.InRange(writes.Length, 20, 49);
            Assert.InRange(run.Checkpoint.StepCount - writes.Length, 0, 1);
            Assert.Empty(AgentRunToolCallCheckpoint.PendingCalls(messages!));
            var last = writes.Length - 1;
            Assert.Equal(new string('x', 7000) + last, System.Text.Encoding.UTF8.GetString((await workspace.ReadAsync(runtime.SessionId, runtime.Snapshot.Definition, "/workspace/working/part-" + last + ".txt")).Bytes));
            await Assert.ThrowsAsync<AgentCoreException>(() => workspace.ReadAsync(runtime.SessionId, runtime.Snapshot.Definition, "/workspace/working/part-" + writes.Length + ".txt").AsTask());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class CapacityFinalizationModel(ModelGenerationEvent[][] rounds) : ILanguageModel
    {
        private int index;
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var evt in request.Tools is null ? FinalAnswer() : rounds[index++]) yield return evt;
        }
    }

    [Fact]
    public async Task Instance_budget_update_during_work_applies_to_next_run_in_same_session_only()
    {
        var instances = new AgentCore.Infrastructure.Persistence.InMemoryAgentInstanceStore();
        var browser = new CountingBrowser();
        var model = new RecordingModel(
            [new ModelToolCallEvent(new("first", ToolCatalog.BrowserSnapshot, "{}")), new ModelCompleted(ModelStopReason.ToolCalls)], FinalAnswer(),
            [new ModelToolCallEvent(new("second", ToolCatalog.BrowserSnapshot, "{}")), new ModelCompleted(ModelStopReason.ToolCalls)], FinalAnswer());
        await using var runtime = CreateCore(model, browser, null, [ToolCatalog.BrowserSnapshot],
            executor: new SessionToolExecutor(browser: browser, agentInstances: instances, configurationGate: ToolConfigurationGates.AllowAll));
        var s = runtime.Snapshot; var now = DateTimeOffset.UtcNow;
        await instances.InsertAsync(new(s.AgentInstanceId, s.Definition.Id, s.Definition.Version, s.Definition.Identity, AgentInstanceLifecycle.Active, now, now));
        browser.ObservationContent = n =>
        {
            if (n == 1) instances.UpdateWithExpectedRevisionAsync(new(s.AgentInstanceId, 1, SetExecutionBudgets: true,
                ExecutionBudgets: new(InteractiveBrowser: ExecutionBudgetProfile.For(ExecutionBudgetClass.InteractiveBrowser, ExecutionBudgetPreset.Extended))), now.AddSeconds(1)).AsTask().GetAwaiter().GetResult();
            return "Asset page " + n;
        };
        await runtime.AttachAsync(); Assert.True(await runtime.SubmitUserTextAsync("Inspect the asset.")); await runtime.WaitUntilIdleAsync();
        Assert.True(await runtime.SubmitUserTextAsync("Inspect the next asset.")); await runtime.WaitUntilIdleAsync();
        var runs = (await SessionRuntimeFixture.RunsForAsync(runtime)).OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.AgentRunId).ToArray();
        Assert.Equal(2, runs.Length);
        Assert.Contains(runs, r => r.Admission.ExecutionBudget!.Profile.MaxSteps == 48 && r.Admission.ExecutionBudget.Source == "system");
        Assert.Contains(runs, r => r.Admission.ExecutionBudget!.Profile.MaxSteps == 96 && r.Admission.ExecutionBudget.Source == "instance");
        Assert.All(runs, r => Assert.Equal(1, r.Checkpoint!.StepCount));
    }

    [Fact]
    public async Task Requested_cleanup_begins_at_work_step_boundary_with_authorized_hover_and_observation()
    {
        var browser = new CountingBrowser { ObservationContent = n => "Observed asset page " + n };
        var rounds = Enumerable.Range(0, 36).Select(i => new ModelGenerationEvent[] {
            new ModelToolCallEvent(new("observe-" + i, ToolCatalog.BrowserSnapshot, "{}")), new ModelCompleted(ModelStopReason.ToolCalls) }).ToList();
        rounds.Add([new ModelToolCallEvent(new("close", ToolCatalog.BrowserClose, "{}")), new ModelCompleted(ModelStopReason.ToolCalls)]);
        rounds.Add(FinalAnswer());
        var model = new RecordingModel(rounds.ToArray());
        await using var runtime = Create(model, browser, ToolCatalog.BrowserSnapshot, ToolCatalog.BrowserHover,
            ToolCatalog.BrowserWait, ToolCatalog.BrowserPressKey, "browser.scroll", "browser.verify", ToolCatalog.BrowserClose, ToolCatalog.BrowserType);
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitUserTextAsync("Inspect the asset then log out and close browser."));
        await runtime.WaitUntilIdleAsync();
        var cleanupRequest = model.Requests[36];
        foreach (var tool in new[] { ToolCatalog.BrowserHover, ToolCatalog.BrowserWait, ToolCatalog.BrowserPressKey, "browser.scroll", "browser.verify" })
            Assert.Contains(cleanupRequest.Tools!, t => t.Name == tool);
        Assert.DoesNotContain(cleanupRequest.Tools!, t => t.Name == ToolCatalog.BrowserType);
        Assert.Contains(cleanupRequest.Messages, m => m.Text == RunFinalization.CleanupMarker);
        var run = Assert.Single(await SessionRuntimeFixture.RunsForAsync(runtime));
        Assert.Equal(37, run.Checkpoint!.StepCount); Assert.Equal(AgentRunStatus.Completed, run.Status);
        Assert.Equal(1, browser.CloseCalls);
        Assert.True(run.Admission.ExecutionBudget!.RequestedCleanup);
        Assert.True(RunBudgetDiagnosticProjection.From(run)!.ClosureConfirmed);
    }

    [Fact]
    public async Task Step_exhaustion_finalizes_from_receipts_without_dispatching_extra_effect()
    {
        var browser = new CountingBrowser { ObservationContent = n => "Observed asset page " + n };
        var rounds = Enumerable.Range(0, 49).Select(i => new ModelGenerationEvent[] {
            new ModelToolCallEvent(new("observe-" + i, ToolCatalog.BrowserSnapshot, "{}")), new ModelCompleted(ModelStopReason.ToolCalls) }).ToList();
        rounds.Add(FinalAnswer());
        var model = new RecordingModel(rounds.ToArray());
        await using var runtime = Create(model, browser, ToolCatalog.BrowserSnapshot);
        await runtime.AttachAsync(); Assert.True(await runtime.SubmitUserTextAsync("Inspect the asset."));
        await runtime.WaitUntilIdleAsync();
        var run = Assert.Single(await SessionRuntimeFixture.RunsForAsync(runtime));
        Assert.Equal(48, browser.ObserveCalls); Assert.Equal(48, run.Checkpoint!.StepCount);
        Assert.Equal("stepLimit", RunBudgetDiagnosticProjection.From(run)!.TerminationReason);
        Assert.Null(model.Requests.Last().Tools);
        Assert.Equal(AgentRunStatus.Completed, run.Status);
    }
}
