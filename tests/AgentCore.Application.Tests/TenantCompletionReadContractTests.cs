using System.Text.Json;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tests;

public sealed class TenantCompletionReadContractTests
{
    [Fact]
    public async Task Exact_completion_inspection_exposes_only_its_contextual_read_review_without_creation_or_native_authority()
    {
        var definition = await CapabilityProjectionTests.Definition(ToolCatalog.BackgroundInspect); var session = Guid.NewGuid(); var activation = Guid.NewGuid();
        var context = CapabilityProjectionTests.Context(definition) with { AgentInstanceId = Guid.NewGuid(), OwnedSessionId = session,
            LoadedCapabilityIds = [ToolCatalog.BackgroundInspect], OutputContract = AgentRunOutputContract.ConversationResponse,
            ExecutionContext = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), session, Guid.NewGuid(), activation, activation, null, null, ExecutionOrigin.UserMessage) };
        var tool = Assert.Single(ToolCatalog.For(definition, context, ToolConfigurationGates.AllowAll), t => t.Name == ToolCatalog.RequestAuthorization);
        Assert.Contains("inspect_background_completion", tool.ParametersJson); Assert.DoesNotContain("start_background_work", tool.ParametersJson);
        Assert.DoesNotContain("run_browser_command", tool.ParametersJson); Assert.DoesNotContain("publish_artifact", tool.ParametersJson);
        Assert.DoesNotContain(ToolCatalog.For(definition, context with { OwnedSessionId = Guid.NewGuid() }, ToolConfigurationGates.AllowAll), t => t.Name == ToolCatalog.RequestAuthorization);
        Assert.DoesNotContain(ToolCatalog.For(definition, context with { ModelSupportsTools = false }, ToolConfigurationGates.AllowAll), t => t.Name == ToolCatalog.RequestAuthorization);
        var id = Guid.NewGuid(); var intent = TenantApprovalToolContract.Parse(new("read", ToolCatalog.RequestAuthorization,
            JsonSerializer.Serialize(new { operation = "inspect_background_completion", read = new { backgroundSessionId = id } })));
        Assert.Equal(id, AgentCore.Application.Security.TenantCompletionReadService.Parse(intent.CompletionArguments!));
    }
    [Theory]
    [InlineData("""{"operation":"inspect_background_completion","read":{"backgroundSessionId":"00000000-0000-0000-0000-000000000000"}}""")]
    [InlineData("""{"operation":"inspect_background_completion","read":{"backgroundSessionId":"12345678-1234-1234-1234-123456789abc","sourceAgentRunId":"22345678-1234-1234-1234-123456789abc"}}""")]
    [InlineData("""{"operation":"inspect_background_completion","read":{"backgroundSessionId":"12345678-1234-1234-1234-123456789abc","backgroundSessionId":"22345678-1234-1234-1234-123456789abc"}}""")]
    [InlineData("""{"operation":"inspect_background_completion","read":[],"tenantId":"12345678-1234-1234-1234-123456789abc"}""")]
    public void Completion_read_intent_rejects_empty_duplicate_or_model_supplied_authority_witnesses(string arguments)
        => Assert.ThrowsAny<ArgumentException>(() => TenantApprovalToolContract.Parse(new("read", ToolCatalog.RequestAuthorization, arguments)));
}
