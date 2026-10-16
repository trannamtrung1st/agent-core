using AgentCore.Application.Tools;
namespace AgentCore.Application.Tests;
public sealed class SandboxFailureSummaryTests
{
    [Theory]
    [InlineData("Indeterminate",true,true)]
    [InlineData("Indeterminate",false,false)]
    [InlineData("Completed",true,false)]
    public void Only_current_call_uncertainty_is_attributed(string status,bool currentCall,bool unknown)
    {
        var result = new AgentCore.Application.Ports.SandboxCommandResult(Guid.NewGuid(),status,status=="Completed"?7:null,"","",false,0,true,Error:"recovery_required");
        Assert.Equal(unknown,SessionToolExecutor.SandboxEffectUnknown(result,currentCall));
    }

    [Fact]
    public void A_null_error_is_not_a_definitive_effect_rejection()
    {
        Assert.False(AgentCore.Application.Sessions.SessionRuntime.RejectedEffect("{\"status\":\"Completed\",\"error\":null}"));
        Assert.True(AgentCore.Application.Sessions.SessionRuntime.RejectedEffect("{\"error\":\"recovery_required\"}"));
    }
    [Fact]
    public void Process_cleanup_and_filesystem_effects_are_independent()
    {
        var result=SandboxFailureSummary.Describe("backgroundProcessesActive","processInspection",true,true);
        Assert.Contains("child processes",result);Assert.Contains("partial writes may remain",result);
        Assert.Contains("termination is confirmed",result);Assert.Contains("No command was replayed",result);
    }
    [Fact]
    public void Cancellation_retains_partial_writes_and_uncertain_termination()
    {
        var result=SandboxFailureSummary.Describe("cancellation","execution",true,false);
        Assert.Contains("cancelled",result);Assert.Contains("partial writes may remain",result);Assert.Contains("termination remains unconfirmed",result);
        Assert.DoesNotContain("files were not committed",result);Assert.DoesNotContain("command failed",result);
    }
    [Fact]
    public void A_checkpointed_recovery_receipt_stops_substantive_work_even_if_the_finalization_marker_was_not_saved()
    {
        var receipts=new AgentCore.Application.Ports.ModelMessage[] {
            new(AgentCore.Application.Ports.ModelRole.Tool,"{\"error\":\"recovery_required\",\"environmentState\":\"NeedsReconciliation\"}",ToolCallId:"blocked",Name:ToolCatalog.SandboxExec) };
        Assert.Equal("sandboxRecovery",AgentCore.Application.Execution.RunFinalization.ObservedRecovery(receipts));
        Assert.Null(AgentCore.Application.Execution.RunFinalization.ObservedRecovery([receipts[0] with {Name="unrelated.tool"}]));
    }
}
