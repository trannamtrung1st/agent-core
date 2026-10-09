using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tests;

public sealed class RunBudgetDiagnosticsTests
{
    [Theory]
    [InlineData("stepLimit")] [InlineData("runDeadline")] [InlineData("checkpointCapacity")]
    [InlineData("outputLimit")] [InlineData("cleanupBlocked")]
    public void Canonical_exhaustion_reason_survives_checkpoint_projection(string reason)
    {
        var run = NewRun(); var now = run.CreatedAtUtc; var generation = Guid.NewGuid();
        run = run.TakeClaim(generation, now, now.AddMinutes(1));
        var messages = new ModelMessage[] { new(ModelRole.System, RunFinalization.Marker + reason) };
        run = run.SaveCheckpoint(run.Revision, generation, new(AgentRunToolCallCheckpoint.Write(messages), 2, 100, 290000, activeExecutionMs: 10000), null, now);
        var diagnostic = RunBudgetDiagnosticProjection.From(run)!;
        Assert.Equal(reason, diagnostic.TerminationReason); Assert.Equal("finalization", diagnostic.Phase);
        Assert.Equal(2, diagnostic.StepsConsumed); Assert.Equal(48, diagnostic.MaxSteps); Assert.Equal(10000, diagnostic.ActiveExecutionMs);
    }

    [Fact]
    public void Checkpoint_capacity_rejection_preserves_pending_identity_and_confirmed_receipts()
    {
        var run = NewRun(); var now = run.CreatedAtUtc; var generation = Guid.NewGuid();
        run = run.TakeClaim(generation, now, now.AddMinutes(1));
        var pending = new ModelToolCall("pending", ToolCatalog.BrowserHover, "{\"target\":{\"by\":\"role\",\"value\":\"button\",\"name\":\"Account\"}}");
        ModelMessage[] messages = [new(ModelRole.Tool, "{\"status\":\"ok\",\"effectConfirmedBySdk\":true}", Name: ToolCatalog.BrowserClick, ToolCallId: "done"), new(ModelRole.Assistant, "", ToolCalls: [pending])];
        var saved = run.SaveCheckpoint(run.Revision, generation, new(AgentRunToolCallCheckpoint.Write(messages), 2, 100, 290000), null, now);
        Assert.Throws<ArgumentException>(() => new AgentRunCheckpoint(new string('x', AgentRunLimits.MaxCheckpointBytes + 1), 3, 101, 289000));
        Assert.True(AgentRunToolCallCheckpoint.TryRead(saved.Checkpoint, out var recovered));
        Assert.Equal(pending, Assert.Single(AgentRunToolCallCheckpoint.PendingCalls(recovered!)));
        Assert.Contains(recovered!, m => m.Text.Contains("\"effectConfirmedBySdk\":true"));
        Assert.Equal(2, saved.Checkpoint!.StepCount);
    }

    [Fact]
    public void Earlier_work_verification_cannot_establish_cleanup_success()
    {
        var run = NewRun(); var now = run.CreatedAtUtc; var generation = Guid.NewGuid(); run = run.TakeClaim(generation, now, now.AddMinutes(1));
        ModelMessage[] messages = [new(ModelRole.Tool, "{\"applicationOutcomeVerified\":true}", Name: "browser.verify"), new(ModelRole.System, RunFinalization.CleanupMarker), new(ModelRole.Tool, "{\"status\":\"closed\"}", Name: ToolCatalog.BrowserClose)];
        run = run.SaveCheckpoint(run.Revision, generation, new(AgentRunToolCallCheckpoint.Write(messages), 2, 100, 290000), null, now);
        Assert.Equal("unverified", RunBudgetDiagnosticProjection.From(run)!.CleanupStatus);
        Assert.True(RunBudgetDiagnosticProjection.From(run)!.ClosureConfirmed);
    }

    [Fact]
    public void Uncertain_browser_reclaim_charges_old_lease_and_restarts_clock_without_replay()
    {
        var run = NewRun(); var now = run.CreatedAtUtc; var generation = Guid.NewGuid(); run = run.TakeClaim(generation, now, now.AddMinutes(1));
        const string args = "{\"target\":{\"by\":\"role\",\"value\":\"button\",\"name\":\"Account\"}}";
        using var doc = System.Text.Json.JsonDocument.Parse(args);
        var hash = AgentRunActionHash.Compute(ToolCatalog.BrowserClick, doc.RootElement);
        run = run.MarkSideEffect(run.Revision, generation, AgentRunSideEffectDisposition.Prepared, "pending", hash, now);
        run = run.MarkSideEffect(run.Revision, generation, AgentRunSideEffectDisposition.InFlight, "pending", hash, now);
        ModelMessage[] messages = [new(ModelRole.Assistant, "", ToolCalls: [new("pending", ToolCatalog.BrowserClick, args)])];
        run = run.SaveCheckpoint(run.Revision, generation, new(AgentRunToolCallCheckpoint.Write(messages), 1, 0, 290000, 10000), null, now.AddSeconds(10));
        var reclaimed = run.RecoverExpiredClaim(now.AddMinutes(2), Guid.NewGuid);
        Assert.Equal(AgentRunStatus.Running, reclaimed.Status);
        Assert.Equal(60000, reclaimed.Checkpoint!.ActiveExecutionMs);
        Assert.Equal(240000, reclaimed.Checkpoint.RemainingOverallBudgetMs);
        Assert.Equal(now.AddMinutes(2), reclaimed.Checkpoint.BudgetRecordedAtUtc);
        Assert.Equal(run.Admission.ExecutionBudget, reclaimed.Admission.ExecutionBudget);
        Assert.Equal(1, reclaimed.Checkpoint.StepCount);
        Assert.True(AgentRunToolCallCheckpoint.TryReadState(reclaimed.Checkpoint, out _, out var observationRequired, out _));
        Assert.True(observationRequired);
        Assert.Throws<ArgumentException>(() => reclaimed.SaveCheckpoint(reclaimed.Revision, reclaimed.Claim!.Generation,
            new("{}", 0, 0, 240000, 60000), null, now.AddMinutes(2)));
    }

    private static AgentRun NewRun()
    {
        var now = DateTimeOffset.UtcNow; var activation = new Activation(Guid.NewGuid(), Guid.NewGuid(), ActivationKind.UserTurn, [Guid.NewGuid()], null, null, null, null, "budget-test", now, "{}");
        return AgentRun.Create(Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid()), new(activation, "general-assistant", 1,
            new AgentIdentity("Test", "Role", "desc", "Tone"), Guid.NewGuid(), AgentRunOutputContract.ConversationResponse,
            new(ExecutionBudgetClass.InteractiveBrowser, ExecutionBudgetProfile.For(ExecutionBudgetClass.InteractiveBrowser, ExecutionBudgetPreset.Standard), "system", true)),
            new("synthetic", "synthetic", "synthetic", null), 3, now);
    }
}
