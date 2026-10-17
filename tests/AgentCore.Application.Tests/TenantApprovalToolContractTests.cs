using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tests;

public sealed class TenantApprovalToolContractTests
{
    private static readonly AgentIdentity Persona = new("Persona", "Assistant", "Description", "Clear");
    private static string Arguments(AgentIdentity? persona = null) => JsonSerializer.Serialize(new { operation = "update_persona", persona = persona ?? Persona }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    [Fact]
    public void Exact_persona_contract_accepts_only_its_typed_operation_and_rejects_caller_authority()
    {
        Assert.Equal(Persona, TenantApprovalToolContract.Parse(new("call", ToolCatalog.RequestAuthorization, Arguments())));
        foreach (var key in new[] { "tenantId", "runId", "principalId", "permission", "grantId", "resourceId", "expectedRevision" })
        {
            var input = JsonNode.Parse(Arguments())!.AsObject(); input[key] = "untrusted";
            Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("call", ToolCatalog.RequestAuthorization, input.ToJsonString())));
        }
        Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("call", ToolCatalog.RequestAuthorization, Arguments().Replace("update_persona", "assign_role"))));
        Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("call", ToolCatalog.RequestAuthorization, Arguments()[..^1] + ",\"operation\":\"update_persona\"}")));
        Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("call", ToolCatalog.RequestAuthorization, Arguments(Persona with { Name = new('x', 81) }))));
        Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("call", ToolCatalog.RequestAuthorization, Arguments(Persona with { Description = new('x', 1025) }))));
    }

    [Fact]
    public async Task Offering_requires_tenant_context_and_pinned_Settings_eligibility_and_never_becomes_a_definition_allowlist_permission()
    {
        var definition = await CapabilityProjectionTests.Definition();
        var session = Guid.NewGuid(); var activation = Guid.NewGuid();
        var context = CapabilityProjectionTests.Context(definition) with
        {
            AgentInstanceId = Guid.NewGuid(), OwnedSessionId = session,
            ExecutionContext = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), session, Guid.NewGuid(), activation, activation, null, null, AgentCore.Domain.Conversation.ExecutionOrigin.UserMessage)
        };
        Assert.DoesNotContain(ToolCatalog.For(definition, context, ToolConfigurationGates.AllowAll), t => t.Name == ToolCatalog.RequestAuthorization);
        definition = definition with { SelfManagement = new(HarnessManagementMode.Assisted, [HarnessManagementScope.Settings], [], []) };
        Assert.Contains(ToolCatalog.For(definition, context with { Definition = definition }, ToolConfigurationGates.AllowAll), t => t.Name == ToolCatalog.RequestAuthorization);
        Assert.DoesNotContain(ToolCatalog.For(definition, context with { Definition = definition, ExecutionContext = null }, ToolConfigurationGates.AllowAll), t => t.Name == ToolCatalog.RequestAuthorization);
        Assert.False(TenantApprovalToolContract.Descriptor().DefinitionAuthorizable);
    }
}
