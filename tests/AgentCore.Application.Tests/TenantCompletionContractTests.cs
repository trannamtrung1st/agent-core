using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tests;

public sealed class TenantCompletionContractTests
{
    [Theory] [InlineData(ToolCatalog.BackgroundList)] [InlineData(ToolCatalog.BackgroundInspect)]
    [InlineData(ToolCatalog.BackgroundTake)] [InlineData(ToolCatalog.BackgroundAcknowledge)]
    public async Task Tenant_completion_tools_require_explicit_capability_owned_task_and_ack_claim(string tool)
    {
        var definition = await CapabilityProjectionTests.Definition(tool); var session = Guid.NewGuid(); var activation = Guid.NewGuid();
        var context = CapabilityProjectionTests.Context(definition) with { AgentInstanceId = Guid.NewGuid(), OwnedSessionId = session,
            OutputContract = AgentRunOutputContract.ConversationResponse, HasBackgroundClaim = true, LoadedCapabilityIds = [tool],
            ExecutionContext = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), session, Guid.NewGuid(), activation, activation, null, null, ExecutionOrigin.UserMessage) };
        Assert.Contains(ToolProjectionService.Project(definition, context, ToolConfigurationGates.AllowAll), t => t.Name == tool);
        foreach (var invalid in new[] { context with { OwnedSessionId = Guid.NewGuid() }, context with { ModelSupportsTools = false },
            context with { OutputContract = AgentRunOutputContract.CompletionReport }, context with { ExecutionContext = new(context.ExecutionContext!.TenantId,
                context.ExecutionContext.AgentPrincipalId, context.ExecutionContext.OriginalInitiatorPrincipalId, session, Guid.NewGuid(), activation,
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ExecutionOrigin.CompletionReport) } })
            Assert.DoesNotContain(ToolProjectionService.Project(definition, invalid, ToolConfigurationGates.AllowAll), t => t.Name == tool);
        var disabled = definition with { Environment = definition.Environment! with { Capabilities = definition.Environment.Capabilities! with { ResolvedCapabilities = [] } } };
        Assert.DoesNotContain(ToolProjectionService.Project(disabled, context, ToolConfigurationGates.AllowAll), t => t.Name == tool);
        Assert.Equal(tool != ToolCatalog.BackgroundAcknowledge, ToolProjectionService.Project(definition, context with { HasBackgroundClaim = false }, ToolConfigurationGates.AllowAll).Any(t => t.Name == tool));
    }
}
