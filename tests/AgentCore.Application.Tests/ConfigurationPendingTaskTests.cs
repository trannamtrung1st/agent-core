using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tests;

public sealed class ConfigurationPendingTaskTests
{
    [Theory]
    [InlineData("{}", false)]
    [InlineData("""{"requestId":null}""", false)]
    [InlineData("""{"requestId":7}""", false)]
    [InlineData("""{"requestId":true}""", false)]
    [InlineData("""{"requestId":{}}""", false)]
    [InlineData("""{"requestId":[]}""", false)]
    [InlineData("""{"requestId":"invalid"}""", false)]
    [InlineData("""{"requestId":"11111111-2222-3333-4444-555555555555"}""", true)]
    public void Tenant_continuation_request_ids_ignore_nonstring_observations_and_require_saved_approval_proof(string observation, bool hasId)
    {
        var now = DateTimeOffset.UtcNow;
        var tenant = Guid.NewGuid(); var executor = Guid.NewGuid(); var human = Guid.NewGuid();
        var session = Guid.NewGuid(); var id = Guid.NewGuid(); var activationId = Guid.NewGuid();
        var activation = Activation.Start(activationId, session, ActivationKind.UserTurn, [Guid.NewGuid()], null, null, null, null, "configuration-proof", now);
        var context = new TrustedExecutionContext(tenant, executor, human, session, id, activationId, activationId, null, null, ExecutionOrigin.UserMessage);
        var source = AgentRun.Create(id, AgentRunOwner.ForTenant(tenant, Guid.NewGuid(), executor),
            new(activation, SampleDefinitions.Examiner.Id, SampleDefinitions.Examiner.Version, SampleDefinitions.Examiner.Identity,
                Guid.NewGuid(), AgentRunOutputContract.ConversationResponse, executionContext: context),
            new("scripted-alpha", "primary-llm", "scripted-alpha", null), 3, now);
        source = source.TakeClaim(Guid.NewGuid(), now, now.AddMinutes(5));
        var call = new ModelToolCall("source-read", ToolCatalog.RequestAuthorization,
            """{"operation":"inspect_workspace","read":{"tool":"workspace.read","arguments":{"path":"/working/source.txt"}}}""");
        ModelMessage[] messages = [new(ModelRole.Assistant, "", ToolCalls: [call]), new(ModelRole.Tool, observation, Name: call.Name, ToolCallId: call.Id)];
        source = source.SaveCheckpoint(source.Revision, source.Claim!.Generation,
            new(AgentRunToolCallCheckpoint.Write(messages), 1, 0, 10000), null, now);
        source = source.Complete(source.Revision, source.Claim!.Generation, "Read completed", now, outcomeEntryId: Guid.NewGuid());

        var requestId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Assert.Equal(hasId ? [requestId] : Array.Empty<Guid>(), TenantConfigurationContinuationRules.RequestIds(source));
        var lookups = 0;
        Assert.Null(TenantConfigurationContinuationRules.Pending(source, requested =>
        {
            lookups++; Assert.Equal(requestId, requested); return null;
        }, _ => throw new InvalidOperationException("A missing saved approval cannot supply a receipt.")));
        Assert.Equal(hasId ? 1 : 0, lookups);
    }

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
        var definition=SampleDefinitions.Examiner with { ExecutionPolicy =new AgentCore.Domain.Definitions.DefinitionExecutionPolicy(ToolAllowlist:[ToolCatalog.WorkspaceWrite,ToolCatalog.WorkspaceDelete]) };
        var admission=new ToolExecutionAdmission(false,TriggerKind.UserTurn,OwnedSessionId:Guid.NewGuid(),ConfigurationContinuationTarget:ToolCatalog.WorkspaceWrite);
        Assert.Equal(ToolPolicyDecision.RequireApproval,ToolPolicy.EvaluateExecution(definition,ToolCatalog.WorkspaceWrite,ToolConfigurationGates.AllowAll,admission:admission));
        Assert.Equal(ToolPolicyDecision.Deny,ToolPolicy.EvaluateExecution(definition,ToolCatalog.WorkspaceDelete,ToolConfigurationGates.AllowAll,admission:admission));
        Assert.Equal(ToolPolicyDecision.Deny,ToolPolicy.EvaluateExecution(definition,"configuration.propose",ToolConfigurationGates.AllowAll,admission:admission));
    }
}
