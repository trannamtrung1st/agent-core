using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tests;

public sealed class TenantTaskInboxContractTests
{
    [Theory] [InlineData("tasks.list")] [InlineData("tasks.inspect")]
    [InlineData("tasks.messages.list")] [InlineData("tasks.acknowledge")]
    public async Task Tenant_completion_tools_require_explicit_capability_owned_task_and_ack_claim(string tool)
    {
        var definition = await CapabilityProjectionTests.Definition(tool); var session = Guid.NewGuid(); var activation = Guid.NewGuid();
        var context = CapabilityProjectionTests.Context(definition) with { AgentInstanceId = Guid.NewGuid(), OwnedSessionId = session, CurrentContextTools = TenantTaskToolContract.Names,
            OutputContract = AgentRunOutputContract.ConversationResponse, LoadedCapabilityIds = [tool],
            ExecutionContext = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), session, Guid.NewGuid(), activation, activation, null, null, ExecutionOrigin.UserMessage) };
        Assert.Contains(ToolProjectionService.Project(definition, context, ToolConfigurationGates.AllowAll), t => t.Name == tool);
        foreach (var invalid in new[] { context with { OwnedSessionId = Guid.NewGuid() }, context with { ModelSupportsTools = false },
            context with { CurrentContextTools = [] } })
            Assert.DoesNotContain(ToolProjectionService.Project(definition, invalid, ToolConfigurationGates.AllowAll), t => t.Name == tool);
        var disabled = definition with { ExecutionPolicy = definition.ExecutionPolicy! with { Capabilities = definition.ExecutionPolicy.Capabilities! with { ResolvedCapabilities = [] } } };
        Assert.DoesNotContain(ToolProjectionService.Project(disabled, context, ToolConfigurationGates.AllowAll), t => t.Name == tool);
    }
}
