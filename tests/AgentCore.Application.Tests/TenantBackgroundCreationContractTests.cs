using System.Text.Json;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tests;

public sealed class TenantBackgroundCreationContractTests
{
    [Theory] [InlineData(ExecutionOrigin.UserMessage)] [InlineData(ExecutionOrigin.ApprovalContinuation)]
    public async Task Creation_only_capability_offers_closed_consent_in_owned_conversational_tasks(ExecutionOrigin origin)
    {
        var definition = await CapabilityProjectionTests.Definition(ToolCatalog.BackgroundStart);
        var session = Guid.NewGuid(); var activation = Guid.NewGuid();
        var context = CapabilityProjectionTests.Context(definition) with { AgentInstanceId = Guid.NewGuid(), OwnedSessionId = session,
            DetachedExecution = origin == ExecutionOrigin.ApprovalContinuation, LoadedCapabilityIds = [ToolCatalog.BackgroundStart],
            ExecutionContext = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), session, Guid.NewGuid(), activation, origin == ExecutionOrigin.UserMessage ? activation : Guid.NewGuid(), origin == ExecutionOrigin.UserMessage ? null : Guid.NewGuid(), origin == ExecutionOrigin.UserMessage ? null : Guid.NewGuid(), origin) };
        var tools = ToolProjectionService.Project(definition, context, ToolConfigurationGates.AllowAll);
        Assert.Contains(tools, t => t.Name == ToolCatalog.BackgroundStart);
        Assert.Contains("start_background_work", Assert.Single(tools, t => t.Name == ToolCatalog.RequestAuthorization).ParametersJson);
        foreach (var invalid in new[] { context with { ModelSupportsTools = false }, context with { OwnedSessionId = Guid.NewGuid() },
            context with { OutputContract = AgentRunOutputContract.CompletionReport } })
            Assert.DoesNotContain(ToolProjectionService.Project(definition, invalid, ToolConfigurationGates.AllowAll), t => t.Name == ToolCatalog.BackgroundStart);
    }
    [Theory] [InlineData("tenantId")] [InlineData("principalId")] [InlineData("childSessionId")] [InlineData("runId")]
    [InlineData("permission")] [InlineData("model")] [InlineData("configurationHash")] [InlineData("rootActivationId")]
    public void Background_proposals_cannot_supply_authority_child_identity_or_admitted_witnesses(string field)
    {
        var task = new Dictionary<string, object> { ["objective"] = "Exact task", [field] = "forged" };
        Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("request", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = "start_background_work", task }))));
    }
    [Theory] [InlineData("{\"objective\":\"first\",\"objective\":\"second\"}")]
    [InlineData("{\"objective\":\"task\",\"reportCompletion\":\"false\"}")] [InlineData("{\"objective\":\"   \"}")]
    public void Background_proposals_refuse_ambiguous_or_invalid_task_semantics(string task) => Assert.Throws<ArgumentException>(() => TenantBackgroundStartArguments.Parse(task));
}
