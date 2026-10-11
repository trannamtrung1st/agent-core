using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tests;

public sealed class ConfigurationPendingTaskTests
{
    [Theory]
    [InlineData("safe", true)]
    [InlineData("uncertain", false)]
    [InlineData("otherWrite", false)]
    [InlineData("pendingCall", false)]
    [InlineData("rejected", false)]
    [InlineData("wrongRun", false)]
    [InlineData("managementTarget", false)]
    [InlineData("unchangedRevision", false)]
    public void Continuation_requires_a_committed_owned_task_and_no_effect_or_pending_call(string condition,bool expected)
    {
        var now=DateTimeOffset.UtcNow;
        var snapshot=AgentRunTestFixtures.Snapshot(new(Guid.NewGuid(),Guid.NewGuid()),SampleDefinitions.Examiner,now);
        var source=AgentRunTestFixtures.Run(snapshot,now).TakeClaim(Guid.NewGuid(),now,now.AddMinutes(5));
        var task=new ConfigurationPendingTask(condition=="wrongRun"?Guid.NewGuid():source.AgentRunId,
            condition=="managementTarget"?"settings.patch":ToolCatalog.WorkspaceList,"List my workspace",condition=="unchangedRevision"?0:1);
        List<ModelMessage> messages=[new(ModelRole.Tool,JsonSerializer.Serialize(new {outcome=condition=="rejected"?"denied":"applied",pendingTask=task},new JsonSerializerOptions(JsonSerializerDefaults.Web)),Name:"configuration.propose",ToolCallId:"configuration")];
        if(condition=="otherWrite")messages.Add(new(ModelRole.Tool,"{}",Name:ToolCatalog.EmailSend,ToolCallId:"sent"));
        if(condition=="pendingCall")messages.Add(new(ModelRole.Assistant,"",ToolCalls:[new("pending",ToolCatalog.WorkspaceList,"{}")]));
        source=source.SaveCheckpoint(source.Revision,source.Claim!.Generation,new(AgentRunToolCallCheckpoint.Write(messages,observationRequired:condition=="uncertain"),1,0,10000),null,now);
        source=source.Complete(source.Revision,source.Claim!.Generation,"Configuration completed",now,outcomeEntryId:Guid.NewGuid());
        Assert.Equal(expected,ConfigurationPendingTask.ReadSafe(source) is not null);
    }

    [Fact]
    public void Continuation_target_write_needs_new_approval_and_other_writes_management_are_denied()
    {
        var definition=SampleDefinitions.Examiner with { Environment=new AgentCore.Domain.Definitions.RoleEnvironment(ToolAllowlist:[ToolCatalog.WorkspaceWrite,ToolCatalog.WorkspaceDelete]) };
        var admission=new ToolExecutionAdmission(false,TriggerKind.UserTurn,OwnedSessionId:Guid.NewGuid(),ConfigurationContinuationTarget:ToolCatalog.WorkspaceWrite);
        Assert.Equal(ToolPolicyDecision.RequireApproval,ToolPolicy.EvaluateExecution(definition,ToolCatalog.WorkspaceWrite,ToolConfigurationGates.AllowAll,admission:admission));
        Assert.Equal(ToolPolicyDecision.Deny,ToolPolicy.EvaluateExecution(definition,ToolCatalog.WorkspaceDelete,ToolConfigurationGates.AllowAll,admission:admission));
        Assert.Equal(ToolPolicyDecision.Deny,ToolPolicy.EvaluateExecution(definition,"configuration.propose",ToolConfigurationGates.AllowAll,admission:admission));
    }
}
