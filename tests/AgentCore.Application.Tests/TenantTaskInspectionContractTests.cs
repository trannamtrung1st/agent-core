using System.Text.Json;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tests;

public sealed class TenantTaskInspectionContractTests
{
    [Fact]
    public async Task Task_inspection_has_one_closed_selector_and_no_retired_result_read_consent()
    {
        var definition = await CapabilityProjectionTests.Definition("tasks.inspect"); var session = Guid.NewGuid(); var activation = Guid.NewGuid();
        var context = CapabilityProjectionTests.Context(definition) with { AgentInstanceId = Guid.NewGuid(), OwnedSessionId = session, CurrentContextTools = TenantTaskToolContract.Names,
            LoadedCapabilityIds = ["tasks.inspect"], OutputContract = AgentRunOutputContract.ConversationResponse,
            ExecutionContext = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), session, Guid.NewGuid(), activation, activation, null, null, ExecutionOrigin.UserMessage) };
        var tools = ToolProjectionService.Project(definition, context, ToolConfigurationGates.AllowAll);
        using var schema = JsonDocument.Parse(Assert.Single(tools, t => t.Name == "tasks.inspect").ParametersJson);
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("taskId", Assert.Single(schema.RootElement.GetProperty("required").EnumerateArray()).GetString());
        Assert.DoesNotContain(tools, t => t.Name.StartsWith("background.", StringComparison.Ordinal));
        Assert.All(tools, tool => Assert.DoesNotContain("inspect_background_completion", tool.ParametersJson));
        Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("retired", ToolCatalog.RequestAuthorization,
            JsonSerializer.Serialize(new { operation = "inspect_background_completion", read = new { backgroundSessionId = Guid.NewGuid() } }))));
    }
    [Theory]
    [InlineData("""{"operation":"inspect_background_completion","read":{"backgroundSessionId":"00000000-0000-0000-0000-000000000000"}}""")]
    [InlineData("""{"operation":"inspect_background_completion","read":{"backgroundSessionId":"12345678-1234-1234-1234-123456789abc","sourceAgentRunId":"22345678-1234-1234-1234-123456789abc"}}""")]
    [InlineData("""{"operation":"inspect_background_completion","read":{"backgroundSessionId":"12345678-1234-1234-1234-123456789abc","backgroundSessionId":"22345678-1234-1234-1234-123456789abc"}}""")]
    [InlineData("""{"operation":"inspect_background_completion","read":[],"tenantId":"12345678-1234-1234-1234-123456789abc"}""")]
    public void Completion_read_intent_rejects_empty_duplicate_or_model_supplied_authority_witnesses(string arguments)
        => Assert.ThrowsAny<ArgumentException>(() => TenantApprovalToolContract.Parse(new("read", ToolCatalog.RequestAuthorization, arguments)));
}
