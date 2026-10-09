using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Execution;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Tests;

public sealed class BrowserDialogProgressTests
{
    private const string Pending = "{\"error\":\"dialog_pending\"}";
    private static ModelToolCall Call(string name, string args = "{}", string id = "call") => new(id, name, args);

    [Fact]
    public void Blocked_finalization_preserves_existing_unattended_deadlines_and_never_extends_interactive_time()
    {
        var now = DateTimeOffset.Parse("2026-10-09T12:00:00Z");
        var deadline = now.AddSeconds(120);
        Assert.Equal(deadline, RunFinalization.Deadline(now, deadline, ToolExecutionBudget.Standard.FinalizationReserve));
        Assert.Equal(deadline, RunFinalization.Deadline(now, deadline, ToolExecutionBudget.UnattendedBoundBrowser.FinalizationReserve));
        Assert.Equal(now.AddSeconds(30), RunFinalization.Deadline(now, deadline, ToolExecutionBudget.InteractiveBrowser.FinalizationReserve));
        Assert.Equal(now.AddSeconds(5), RunFinalization.Deadline(now, now.AddSeconds(5), ToolExecutionBudget.InteractiveBrowser.FinalizationReserve));
    }

    [Fact]
    public void Checkpoint_receipts_restore_the_blocker_and_equivalent_failure_bound_without_replaying_effects()
    {
        var calls = new[] { Call(ToolCatalog.BrowserClick, id: "click"), Call(ToolCatalog.BrowserFind, "{\"by\":\"text\",\"value\":\"A\"}", "a"), Call(ToolCatalog.BrowserSnapshot, id: "snapshot") };
        var receipts = new List<ModelMessage>();
        foreach (var call in calls)
        {
            receipts.Add(new(ModelRole.Assistant, "", ToolCalls: [call]));
            receipts.Add(new(ModelRole.Tool, Pending, ToolCallId: call.Id, Name: call.Name));
        }
        var restored = new BrowserEvidenceProgress(receipts);
        Assert.True(restored.DialogPending);
        Assert.True(restored.DialogRecoveryExhausted);
        restored.NoteResult(Call(ToolCatalog.BrowserDialog, "{\"operation\":\"dismiss\"}"), "{\"url\":\"http://fixture.test/\"}");
        Assert.False(restored.DialogPending);
        Assert.False(restored.DialogRecoveryExhausted);
    }

    [Fact]
    public void Distinct_recovery_errors_and_malformed_calls_do_not_combine_and_success_clears_the_old_condition()
    {
        var progress = new BrowserEvidenceProgress();
        progress.NoteResult(Call(ToolCatalog.BrowserClick), Pending);
        var accept = Call(ToolCatalog.BrowserDialog, "{\"operation\":\"accept\"}");
        var dismiss = Call(ToolCatalog.BrowserDialog, "{\"operation\":\"dismiss\"}");
        progress.NoteResult(accept, "{\"error\":\"timeout\"}");
        progress.NoteResult(accept, "{\"error\":\"timeout\"}");
        progress.NoteResult(dismiss, "{\"error\":\"timeout\"}");
        progress.NoteResult(accept, "{\"error\":\"invalid\"}");
        Assert.False(progress.DialogRecoveryExhausted);
        using var invalid = JsonDocument.Parse("{\"by\":\"role\"}");
        Assert.Null(progress.Refuse(Call(ToolCatalog.BrowserFind, invalid.RootElement.GetRawText()), invalid.RootElement));
        using var valid = JsonDocument.Parse("{\"by\":\"text\",\"value\":\"A\"}");
        Assert.Contains("strategySuppressed", progress.Refuse(Call(ToolCatalog.BrowserFind, valid.RootElement.GetRawText()), valid.RootElement)!);
        progress.NoteResult(dismiss, "{\"url\":\"http://fixture.test/\"}");
        Assert.False(progress.DialogPending);
        progress.NoteResult(Call(ToolCatalog.BrowserClick), Pending);
        Assert.False(progress.DialogRecoveryExhausted);
        progress.NoteResult(accept, "{\"error\":\"dialog_missing\"}");
        Assert.Null(progress.Refuse(Call(ToolCatalog.BrowserFind, valid.RootElement.GetRawText()), valid.RootElement));
    }

    [Fact]
    public void Repeated_failed_resolution_is_suppressed_while_a_different_operation_remains_possible()
    {
        var progress = new BrowserEvidenceProgress();
        progress.NoteResult(Call(ToolCatalog.BrowserClick), Pending);
        using var args = JsonDocument.Parse("{\"operation\":\"accept\"}");
        var accept = Call(ToolCatalog.BrowserDialog, args.RootElement.GetRawText());
        progress.NoteResult(accept, "{\"error\":\"provider_unavailable\"}");
        Assert.Null(progress.Refuse(accept, args.RootElement));
        progress.NoteResult(accept, "{\"error\":\"provider_unavailable\"}");
        Assert.True(progress.DialogPending); // A failed resolution does not prove context closure.
        var refused = progress.Refuse(accept, args.RootElement);
        Assert.Contains("strategySuppressed", refused!);
        using var dismiss = JsonDocument.Parse("{\"operation\":\"dismiss\"}");
        Assert.Null(progress.Refuse(Call(ToolCatalog.BrowserDialog, dismiss.RootElement.GetRawText()), dismiss.RootElement));
        progress.NoteResult(accept, refused!);
        Assert.True(progress.DialogRecoveryExhausted);
    }

    [Fact]
    public void A_new_dialog_after_resolution_starts_a_fresh_blocking_condition()
    {
        var progress = new BrowserEvidenceProgress();
        progress.NoteResult(Call(ToolCatalog.BrowserClick), Pending);
        progress.NoteResult(Call(ToolCatalog.BrowserFind), Pending);
        progress.NoteResult(Call(ToolCatalog.BrowserDialog, "{\"operation\":\"accept\"}"), Pending);
        Assert.True(progress.DialogPending);
        Assert.False(progress.DialogRecoveryExhausted);
        progress.NoteResult(Call(ToolCatalog.BrowserClose, ""), "{\"status\":\"closed\"}");
        Assert.False(progress.DialogPending);
        progress.NoteResult(Call(ToolCatalog.BrowserFind), "{\"error\":\"not_found\"}");
        Assert.False(progress.DialogRecoveryExhausted);
        Assert.False(new BrowserEvidenceProgress().DialogPending);
    }

    [Fact]
    public void Ordinary_unsuccessful_searches_never_activate_dialog_recovery()
    {
        var progress = new BrowserEvidenceProgress();
        for (var i = 0; i < 20; i++) progress.NoteResult(Call(ToolCatalog.BrowserFind), "{\"error\":\"not_found\"}");
        Assert.False(progress.DialogPending);
        Assert.False(progress.DialogRecoveryExhausted);
    }
}
