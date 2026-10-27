using System.Text.Json;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tests;

public sealed class TenantTaskCreationContractTests
{
    [Theory] [InlineData(ExecutionOrigin.UserMessage)] [InlineData(ExecutionOrigin.ApprovalContinuation)]
    public async Task Task_creation_offers_one_closed_consent_in_owned_conversational_work(ExecutionOrigin origin)
    {
        var definition = await CapabilityProjectionTests.Definition("tasks.delegate");
        var session = Guid.NewGuid(); var activation = Guid.NewGuid();
        var context = CapabilityProjectionTests.Context(definition) with { AgentInstanceId = Guid.NewGuid(), OwnedSessionId = session, CurrentContextTools = TenantTaskToolContract.Names,
            DetachedExecution = origin == ExecutionOrigin.ApprovalContinuation, LoadedCapabilityIds = ["tasks.delegate"],
            ExecutionContext = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), session, Guid.NewGuid(), activation, origin == ExecutionOrigin.UserMessage ? activation : Guid.NewGuid(), origin == ExecutionOrigin.UserMessage ? null : Guid.NewGuid(), origin == ExecutionOrigin.UserMessage ? null : Guid.NewGuid(), origin) };
        var tools = ToolProjectionService.Project(definition, context, ToolConfigurationGates.AllowAll);
        Assert.Contains(tools, t => t.Name == "tasks.delegate");
        var schema = Assert.Single(tools, t => t.Name == ToolCatalog.RequestAuthorization).ParametersJson;
        Assert.Contains("delegate_task", schema); Assert.DoesNotContain("start_background_work", schema);
        foreach (var invalid in new[] { context with { ModelSupportsTools = false }, context with { OwnedSessionId = Guid.NewGuid() },
            context with { OutputContract = AgentRunOutputContract.CompletionReport } })
            Assert.DoesNotContain(ToolProjectionService.Project(definition, invalid, ToolConfigurationGates.AllowAll), t => t.Name == "tasks.delegate");
    }
    [Theory] [InlineData("tenantId")] [InlineData("principalId")] [InlineData("workerSessionId")] [InlineData("runId")]
    [InlineData("permission")] [InlineData("model")] [InlineData("configurationHash")] [InlineData("rootActivationId")]
    public void Task_proposals_cannot_supply_authority_or_admitted_identities(string field)
    {
        var task = new Dictionary<string, object> { ["objective"] = "Exact task", ["target"] = new { instanceId = Guid.NewGuid() }, ["requestKey"] = "exact", [field] = "forged" };
        Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("request", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = "delegate_task", task }))));
    }
    [Theory] [InlineData("\"objective\":\"first\",\"objective\":\"second\"")]
    [InlineData("\"objective\":\"task\",\"reportOnCompletion\":\"false\"")] [InlineData("\"objective\":\"   \"")]
    public void Task_proposals_refuse_ambiguous_or_invalid_semantics(string fields)
    {
        var json = "{\"operation\":\"delegate_task\",\"task\":{" + fields + ",\"target\":{\"instanceId\":\"" + Guid.NewGuid().ToString("D") + "\"},\"requestKey\":\"once\"}}";
        Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("invalid", ToolCatalog.RequestAuthorization, json)));
    }
}
