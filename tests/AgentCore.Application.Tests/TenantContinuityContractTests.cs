using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tests;
public sealed class TenantContinuityContractTests
{
    [Fact]
    public void Historical_read_intent_binds_only_the_canonical_tool_and_entry_window_without_owner_or_grant_selectors()
    {
        var id = Guid.NewGuid(); var input = JsonSerializer.Serialize(new { operation = "inspect_continuity", continuity = new { tool = ToolCatalog.ContinuityGet, arguments = new { kind = "Session", id, afterEntrySequence = 5, limit = 3 } } });
        var intent = TenantApprovalToolContract.Parse(new("read", ToolCatalog.RequestAuthorization, input)); Assert.NotNull(intent.Continuity);
        var read = new PreparedContinuityRead(Guid.NewGuid(), Guid.NewGuid(), null, intent.Continuity.Tool, intent.Continuity.ArgumentsJson, "[]", new string('a', 64), "[]");
        var operation = new PreparedTenantOperation(Guid.NewGuid(), Guid.NewGuid(), null, 1, 1, new string('a', 64), "{}", ContinuityRead: read);
        Assert.True(intent.Matches(operation)); Assert.False((intent with { Continuity = intent.Continuity with { ArgumentsJson = intent.Continuity.ArgumentsJson.Replace("\"limit\":3", "\"limit\":4") } }).Matches(operation));
        Assert.False((intent with { MetadataArguments = "{}" }).Matches(operation));
        foreach (var field in new[] { "tenantId", "principalId", "userPrincipalId", "profileId", "sessionId", "permission", "grantId", "observationHash", "sources" })
        {
            var root = JsonNode.Parse(input)!; root[field] = "caller"; Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("bad", ToolCatalog.RequestAuthorization, root.ToJsonString())));
            root = JsonNode.Parse(input)!; root["continuity"]!["arguments"]![field] = "caller"; Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("bad", ToolCatalog.RequestAuthorization, root.ToJsonString())));
        }
    }
    [Fact]
    public async Task Historical_read_offering_is_independent_of_Memory_and_Settings_but_requires_the_same_owned_tenant_execution()
    {
        var definition = (await CapabilityProjectionTests.Definition()) with { MemoryPolicy = MemoryPolicy.Disabled, SelfManagement = HarnessManagementPolicy.Disabled };
        var session = Guid.NewGuid(); var activation = Guid.NewGuid(); var context = CapabilityProjectionTests.Context(definition) with {
            AgentInstanceId = Guid.NewGuid(), OwnedSessionId = session,
            ExecutionContext = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), session, Guid.NewGuid(), activation, activation, null, null, AgentCore.Domain.Conversation.ExecutionOrigin.UserMessage) };
        var tools = ToolCatalog.For(definition, context, ToolConfigurationGates.AllowAll); Assert.Contains(tools, t => t.Name == ToolCatalog.ContinuitySearch);
        var schema = Assert.Single(tools, t => t.Name == ToolCatalog.RequestAuthorization).ParametersJson; Assert.Contains("inspect_continuity", schema); Assert.DoesNotContain("manage_memory", schema); Assert.DoesNotContain("update_persona", schema);
        var disabled = definition with { StateIsolationPolicy = new(CrossSessionContinuity: false) };
        Assert.DoesNotContain(ToolCatalog.For(disabled, context with { Definition = disabled }, ToolConfigurationGates.AllowAll), t => t.Name is ToolCatalog.ContinuitySearch or ToolCatalog.ContinuityGet or ToolCatalog.RequestAuthorization);
        Assert.DoesNotContain(ToolCatalog.For(definition, context with { ExecutionContext = null }, ToolConfigurationGates.AllowAll), t => t.Name == ToolCatalog.RequestAuthorization);
    }
    [Theory] [InlineData("null")] [InlineData("[]")] [InlineData("{\"kind\":\"Other\",\"id\":\"00000000-0000-0000-0000-000000000001\"}")]
    [InlineData("{\"kind\":\"Session\",\"id\":\"00000000-0000-0000-0000-000000000001\",\"limit\":0}")]
    [InlineData("{\"kind\":\"Session\",\"id\":\"00000000-0000-0000-0000-000000000001\",\"limit\":21}")]
    [InlineData("{\"kind\":\"Session\",\"id\":\"00000000-0000-0000-0000-000000000001\",\"afterEntrySequence\":-1}")]
    [InlineData("{\"kind\":\"Session\",\"id\":\"00000000-0000-0000-0000-000000000001\",\"afterEntrySequence\":\"5\"}")]
    [InlineData("{\"kind\":\"Session\",\"id\":\"00000000-0000-0000-0000-000000000001\",\"limit\":1,\"limit\":2}")]
    [InlineData("{\"kind\":\"Memory\",\"id\":\"00000000-0000-0000-0000-000000000001\",\"limit\":1}")]
    public void Historical_read_rejects_unknown_kinds_invalid_windows_and_duplicate_fields(string input) => Assert.Throws<ArgumentException>(() => TenantContinuityToolContract.Parse(ToolCatalog.ContinuityGet, input));
}
