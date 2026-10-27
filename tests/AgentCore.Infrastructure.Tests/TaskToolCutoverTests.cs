using System.Text.Json;
using AgentCore.Application.Tools;

namespace AgentCore.Infrastructure.Tests;

public sealed class TaskToolCutoverTests
{
    [Theory]
    [InlineData("background.start")]
    [InlineData("background.list")]
    [InlineData("background.inspect")]
    [InlineData("background.take")]
    [InlineData("background.acknowledge")]
    public void Retired_child_tool_names_have_no_registration_or_alias(string name)
    {
        Assert.False(ToolRegistry.TryGet(name, out _));
        Assert.DoesNotContain(name, ToolRegistry.AllKnownNames());
        Assert.Throws<KeyNotFoundException>(() => ToolRegistry.Get(name));
    }

    [Fact]
    public void Task_wait_is_canonical_and_old_child_wait_is_rejected()
    {
        using var old = JsonDocument.Parse("""{"mode":"background","backgroundSessionIds":["00000000-0000-0000-0000-000000000001"],"until":"all","timeoutSeconds":30}""");
        Assert.Throws<ArgumentException>(() => ExecutionWaitContract.Parse("old", old.RootElement, DateTimeOffset.UtcNow));
        using var schema = JsonDocument.Parse(ToolRegistry.Get("execution.wait").ModelDefinition.ParametersJson);
        Assert.DoesNotContain("background", schema.RootElement.GetProperty("properties").GetProperty("mode").GetProperty("enum").EnumerateArray().Select(v => v.GetString()));
        Assert.All(TenantTaskToolContract.Names, name => Assert.True(ToolRegistry.TryGet(name, out _), name));
    }

    [Fact]
    public void Task_authorization_has_one_closed_selector_intent_and_no_trusted_identity_inputs()
    {
        using var schema = JsonDocument.Parse(TenantApprovalToolContract.Descriptor(settings: false, credentials: false, taskDelegation: true).ModelDefinition.ParametersJson);
        var branch = Assert.Single(schema.RootElement.GetProperty("oneOf").EnumerateArray());
        Assert.Equal("delegate_task", Assert.Single(branch.GetProperty("properties").GetProperty("operation").GetProperty("enum").EnumerateArray()).GetString());
        var valid = "{\"operation\":\"delegate_task\",\"task\":{\"objective\":\"Inspect approved facts\",\"target\":{\"instanceId\":\"00000000-0000-0000-0000-000000000001\"},\"requestKey\":\"one-assignment\"}}";
        Assert.NotNull(TenantApprovalToolContract.Parse(new("exact", ToolCatalog.RequestAuthorization, valid)).TaskArguments);
        foreach (var invalid in new[] {
            valid.Replace("\"objective\":", "\"sourceRunId\":\"00000000-0000-0000-0000-000000000002\",\"objective\":"),
            valid.Replace("\"instanceId\":", "\"executorPrincipalId\":\"00000000-0000-0000-0000-000000000002\",\"instanceId\":"),
            valid.Replace("00000000-0000-0000-0000-000000000001", "not-a-uuid"),
            valid.Replace("\"objective\":\"Inspect approved facts\"", "\"objective\":false"),
            valid.Replace("delegate_task", "start_background_work") })
            Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("invalid", ToolCatalog.RequestAuthorization, invalid)));
    }
    [Theory]
    [InlineData("BackgroundStart")]
    [InlineData("CompletionRead")]
    public void Retired_child_approval_payloads_cannot_decode_as_current_operations(string property)
    {
        var payload = "{\"OperationId\":\"00000000-0000-0000-0000-000000000001\",\"InstanceId\":\"00000000-0000-0000-0000-000000000002\",\"CredentialId\":null,\"ExpectedInstanceRevision\":1,\"ExpectedPersonaRevision\":1,\"CanonicalAction\":\"{}\",\"ActionHash\":\"invalid\",\"" + property + "\":{}}";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AgentCore.Domain.Security.PreparedTenantOperation>(payload));
    }
    [Fact]
    public void Retired_numeric_child_origins_cannot_be_constructed_or_deserialized()
    {
        Assert.Throws<ArgumentException>(() => new AgentCore.Domain.Conversation.SessionOrigin((AgentCore.Domain.Conversation.SessionOriginKind)1,
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => AgentCore.Domain.Conversation.Activation.Start(Guid.NewGuid(), Guid.NewGuid(),
            (AgentCore.Domain.Conversation.ActivationKind)2, [], null, null, null, null, "retired", DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => new AgentCore.Domain.Conversation.TrustedExecutionContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), (AgentCore.Domain.Conversation.ExecutionOrigin)1));
    }

    [Fact]
    public void Retired_completion_consumption_has_no_domain_methods_or_current_status()
    {
        var type = typeof(AgentCore.Domain.Conversation.CompletionInboxItem);
        Assert.Null(type.GetMethod("Take")); Assert.Null(type.GetMethod("Acknowledge"));
        Assert.False(Enum.IsDefined((AgentCore.Domain.Conversation.CompletionInboxStatus)1));
        Assert.False(Enum.IsDefined((AgentCore.Domain.Conversation.CompletionInboxStatus)2));
    }

}
