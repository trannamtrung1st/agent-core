using AgentCore.Application.Tools;
namespace AgentCore.Application.Tests;
public sealed class SandboxFailureSummaryTests
{
    [Fact]
    public void Known_child_process_failure_is_not_rewritten_as_an_unavailable_image_or_unknown_file_commit()
    {
        var result=SandboxFailureSummary.Describe("processInspectionFailure","processInspection","notCommitted",true);
        Assert.Contains("child processes",result);Assert.Contains("Canonical files were not committed",result);
        Assert.Contains("cleanup are confirmed",result);Assert.Contains("does not establish",result);
    }
    [Fact]
    public void Cancellation_and_unknown_commits_keep_their_distinct_safe_evidence()
    {
        var result=SandboxFailureSummary.Describe("cancellation","workspaceCommit","unknown",false);
        Assert.Contains("cancelled",result);Assert.Contains("file effects remain uncertain",result);Assert.Contains("cleanup is pending",result);
        Assert.DoesNotContain("files were not committed",result);Assert.DoesNotContain("command failed",result);
    }
    [Fact]
    public void A_checkpointed_recovery_receipt_stops_substantive_work_even_if_the_finalization_marker_was_not_saved()
    {
        var receipts=new AgentCore.Application.Ports.ModelMessage[] {
            new(AgentCore.Application.Ports.ModelRole.Tool,"{\"error\":\"recovery_required\",\"environmentState\":\"NeedsResync\"}",ToolCallId:"blocked",Name:ToolCatalog.SandboxExec) };
        Assert.Equal("sandboxRecovery",AgentCore.Application.Execution.RunFinalization.Restore(receipts));
        Assert.Null(AgentCore.Application.Execution.RunFinalization.Restore([receipts[0] with {Name="unrelated.tool"}]));
    }
}
