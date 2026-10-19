using AgentCore.Application.Tools;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tests;

public sealed class TenantWorkspaceReadContractTests
{
    [Theory]
    [InlineData("[]")] [InlineData("null")] [InlineData("{}")]
    [InlineData("{\"path\":\"/working/a\",\"path\":\"/home/a\"}")]
    [InlineData("{\"path\":\"/working/a\",\"tenantId\":\"other\"}")]
    [InlineData("{\"path\":\"/working/a\",\"sessionId\":\"other\"}")]
    [InlineData("{\"path\":\"/working/a\",\"cursor\":\"/working/b\"}")]
    [InlineData("{\"path\":\"relative.txt\"}")] [InlineData("{\"path\":\"/agent/private\"}")]
    [InlineData("{\"path\":5}")]
    public void Read_rejects_ambiguous_or_caller_selected_authority(string json) => Assert.Throws<ArgumentException>(() => TenantWorkspaceReadContract.Parse(ToolCatalog.WorkspaceRead, json));

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_projection_keeps_immutable_capability_and_current_execution_boundaries(bool capabilities)
    {
        var definition = await CapabilityProjectionTests.Definition(); var names = new[] { ToolCatalog.WorkspaceRead, ToolCatalog.WorkspaceList, ToolCatalog.OfficeDocumentInspect, ToolCatalog.OfficeSpreadsheetInspect };
        definition = definition with { Environment = definition.Environment! with { Capabilities = capabilities ? new("Selected", names) : null, ToolAllowlist = names } };
        var session = Guid.NewGuid(); var activation = Guid.NewGuid();
        var context = CapabilityProjectionTests.Context(definition) with { AgentInstanceId = Guid.NewGuid(), OwnedSessionId = session,
            ExecutionContext = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), session, Guid.NewGuid(), activation, activation, null, null, ExecutionOrigin.UserMessage) };
        var tools = ToolProjectionService.Project(definition, context, ToolConfigurationGates.From(_ => false));
        foreach (var name in names)
        {
            var tool = Assert.Single(tools, t => t.Name == name); Assert.Contains("additionalProperties", tool.ParametersJson); Assert.Contains("exact absolute", tool.Description);
            Assert.DoesNotContain(ToolProjectionService.Project(definition, context with { ModelSupportsTools = false }, ToolConfigurationGates.AllowAll), t => t.Name == name);
            Assert.DoesNotContain(ToolProjectionService.Project(definition, context with { OwnedSessionId = Guid.NewGuid() }, ToolConfigurationGates.AllowAll), t => t.Name == name);
            Assert.DoesNotContain(ToolProjectionService.Project(definition, context with { ExecutionContext = new(Guid.NewGuid(), Guid.NewGuid(), null, session, Guid.NewGuid(), activation, activation, null, null, ExecutionOrigin.ScheduledOccurrence) }, ToolConfigurationGates.AllowAll), t => t.Name == name);
        }
        var noGrants = definition with { Environment = definition.Environment with { Capabilities = capabilities ? new("Selected", []) : null, ToolAllowlist = [] } };
        Assert.DoesNotContain(ToolProjectionService.Project(noGrants, context with { Definition = noGrants }, ToolConfigurationGates.AllowAll), t => TenantWorkspaceReadContract.IsRead(t.Name));
        var legacy = ToolCatalog.For(definition, context with { ExecutionContext = null, LoadedCapabilityIds = names }, ToolConfigurationGates.AllowAll);
        Assert.Equal(ToolRegistry.Get(ToolCatalog.WorkspaceRead).ModelDefinition.Description, Assert.Single(legacy, t => t.Name == ToolCatalog.WorkspaceRead).Description);
    }
}
