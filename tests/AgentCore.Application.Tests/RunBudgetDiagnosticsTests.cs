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

    [Theory]
    [InlineData(false, "unverified")]
    [InlineData(true, "blocked")]
    public void Only_cleanup_phase_denials_project_cleanup_blocked(bool duringCleanup, string status)
    {
        var run = NewRun(); var now = run.CreatedAtUtc; var generation = Guid.NewGuid(); run = run.TakeClaim(generation, now, now.AddMinutes(1));
        var denied = new ModelMessage(ModelRole.Tool, "{\"error\":\"forbidden\"}", Name: ToolCatalog.BrowserHover);
        var marker = new ModelMessage(ModelRole.System, RunFinalization.CleanupMarker);
        ModelMessage[] messages = duringCleanup ? [marker, denied] : [denied, marker];
        run = run.SaveCheckpoint(run.Revision, generation, new(AgentRunToolCallCheckpoint.Write(messages), 1, 100, 290000), null, now);
        var projection = RunBudgetDiagnosticProjection.From(run)!;
        Assert.Equal(status, projection.CleanupStatus);
        Assert.Equal(duringCleanup ? "cleanupBlocked" : null, projection.TerminationReason);
    }

    [Theory]
    [InlineData(false, true, "completed")]
    [InlineData(true, true, "partial")]
    [InlineData(true, false, "unverified")]
    public void Generic_verification_click_and_model_claims_do_not_prove_logout(bool logout, bool close, string expected)
    {
        var run = WithReceipts(new(logout, close), [
            new(ModelRole.System, RunFinalization.CleanupMarker),
            new(ModelRole.Tool, "{\"status\":\"ok\",\"effectConfirmedBySdk\":true}", Name: ToolCatalog.BrowserClick),
            new(ModelRole.Tool, "{\"status\":\"ok\",\"applicationOutcomeVerified\":true}", Name: "browser.verify"),
            new(ModelRole.Assistant, "Logged out successfully. {\"logoutVerified\":true}"),
            new(ModelRole.Tool, "{\"status\":\"closed\"}", Name: ToolCatalog.BrowserClose)]);
        var projection = RunBudgetDiagnosticProjection.From(run)!;
        Assert.False(projection.LogoutVerified); Assert.True(projection.ClosureConfirmed);
        Assert.Equal(logout, projection.LogoutRequested); Assert.Equal(close, projection.ClosureRequested);
        Assert.Equal(expected, projection.CleanupStatus);
        var restored = System.Text.Json.JsonSerializer.Deserialize<AgentRun>(System.Text.Json.JsonSerializer.Serialize(run))!;
        Assert.Equal(projection, RunBudgetDiagnosticProjection.From(restored));
    }

    [Theory]
    [InlineData(false, "blocked")]
    [InlineData(true, "completed")]
    public void Ordered_close_recovery_clears_only_resolved_blocker(bool recovered, string expected)
    {
        var messages = new List<ModelMessage> {
            new(ModelRole.System, RunFinalization.CleanupMarker),
            new(ModelRole.Tool, "{\"error\":\"close_uncertain\"}", Name: ToolCatalog.BrowserClose),
            new(ModelRole.Tool, "{\"status\":\"ok\",\"applicationOutcomeVerified\":true}", Name: "browser.verify") };
        if (recovered) messages.Add(new(ModelRole.Tool, "{\"status\":\"closed\"}", Name: ToolCatalog.BrowserClose));
        var run = WithReceipts(new(false, true), messages.ToArray());
        var projection = RunBudgetDiagnosticProjection.From(run)!;
        Assert.Equal(expected, projection.CleanupStatus); Assert.Equal(!recovered, projection.CleanupBlocked);
        Assert.Equal(recovered ? null : "cleanupBlocked", projection.TerminationReason);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var receipts));
        Assert.Contains(receipts!, message => message.Text.Contains("close_uncertain"));
    }

    [Fact]
    public void Recovered_hover_does_not_mask_an_unresolved_close()
    {
        var run = WithReceipts(new(false, true), [new(ModelRole.System, RunFinalization.CleanupMarker),
            new(ModelRole.Tool, "{\"error\":\"close_failed\"}", Name: ToolCatalog.BrowserClose),
            new(ModelRole.Tool, "{\"error\":\"target_denied\"}", Name: ToolCatalog.BrowserHover),
            new(ModelRole.Tool, "{\"status\":\"ok\"}", Name: ToolCatalog.BrowserHover)]);
        Assert.True(RunBudgetDiagnosticProjection.From(run)!.CleanupBlocked);
    }

    [Fact]
    public void Sdk_confirmed_alternate_cleanup_path_recovers_denial_without_claiming_logout()
    {
        var run = WithReceipts(new(true, true), [new(ModelRole.System, RunFinalization.CleanupMarker),
            new(ModelRole.Tool, "{\"error\":\"target_denied\"}", Name: ToolCatalog.BrowserHover),
            new(ModelRole.Tool, "{\"status\":\"ok\",\"effectConfirmedBySdk\":true}", Name: ToolCatalog.BrowserClick),
            new(ModelRole.Tool, "{\"status\":\"closed\"}", Name: ToolCatalog.BrowserClose)]);
        var projection = RunBudgetDiagnosticProjection.From(run)!;
        Assert.False(projection.CleanupBlocked); Assert.False(projection.LogoutVerified);
        Assert.Equal("partial", projection.CleanupStatus);
    }

    private static AgentRun WithReceipts(BrowserCleanupIntent intent, ModelMessage[] messages)
    {
        var run = NewRun();
        // Create a fresh admission rather than mutating the immutable admitted pin.
        run = AgentRun.Create(Guid.NewGuid(), run.Owner, new(run.Admission.Activation, run.Admission.DefinitionId, run.Admission.DefinitionVersion,
            run.Admission.PinnedPersona, run.Admission.ResponseId, run.Admission.OutputContract,
            run.Admission.ExecutionBudget! with { CleanupIntent = intent, RequestedCleanup = intent.Any }), run.PinnedModel, run.MaxAttempts, run.CreatedAtUtc);
        var generation = Guid.NewGuid(); var now = run.CreatedAtUtc;
        run = run.TakeClaim(generation, now, now.AddMinutes(1));
        return run.SaveCheckpoint(run.Revision, generation, new(AgentRunToolCallCheckpoint.Write(messages), 4, 100, 290000), null, now);
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
