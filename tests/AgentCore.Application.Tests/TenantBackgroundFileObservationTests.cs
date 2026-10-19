using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tests;

public sealed class TenantBackgroundFileObservationTests
{
    [Theory] [InlineData(ExecutionOrigin.ImmediateBackground)] [InlineData(ExecutionOrigin.ApprovalContinuation)]
    public async Task Background_projection_preserves_completion_and_exposes_only_converted_observations_and_read_consent(ExecutionOrigin origin)
    {
        var definition = await CapabilityProjectionTests.Definition(ToolRegistry.All.Select(t => t.Name).ToArray());
        var session = Guid.NewGuid(); var activation = Guid.NewGuid();
        var context = CapabilityProjectionTests.Context(definition) with { AgentInstanceId = Guid.NewGuid(), OwnedSessionId = session,
            ModelSupportsTools = true, DetachedExecution = true, OutputContract = AgentRunOutputContract.BackgroundOutcome,
            LoadedCapabilityIds = ToolRegistry.All.Select(t => t.Name).ToArray(),
            ExecutionContext = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), session, Guid.NewGuid(), activation, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), origin) };
        var tools = ToolProjectionService.Project(definition, context, ToolConfigurationGates.AllowAll);
        Assert.Contains(tools, t => t.Name == ToolCatalog.WorkComplete);
        foreach (var read in new[] { ToolCatalog.WorkspaceRead, ToolCatalog.WorkspaceList, ToolCatalog.WorkspaceSearch, ToolCatalog.OfficeDocumentInspect, ToolCatalog.OfficeSpreadsheetInspect, ToolCatalog.ArtifactsVerify })
            Assert.Contains(tools, t => t.Name == read);
        Assert.All(tools, t => Assert.True(TenantFileObservationContract.IsTool(t.Name) || IamToolContracts.IsInspection(t.Name) || t.Name is ToolCatalog.RequestAuthorization or ToolCatalog.WorkComplete, t.Name));
        var authorization = Assert.Single(tools, t => t.Name == ToolCatalog.RequestAuthorization);
        using var schema = JsonDocument.Parse(authorization.ParametersJson);
        var operations = schema.RootElement.GetProperty("oneOf").EnumerateArray().Select(b => b.GetProperty("properties").GetProperty("operation").GetProperty("enum")[0].GetString()).ToArray();
        Assert.All(operations, operation => Assert.Contains(operation, new[] { "inspect_workspace", "read_attachment", "verify_artifact" }));
        Assert.Contains("inspect_workspace", operations); Assert.Contains("verify_artifact", operations);
        Assert.DoesNotContain(ToolProjectionService.Project(definition, context with { ModelSupportsTools = false }, ToolConfigurationGates.AllowAll), t => TenantFileObservationContract.IsTool(t.Name));
        Assert.DoesNotContain(ToolProjectionService.Project(definition, context with { OwnedSessionId = Guid.NewGuid() }, ToolConfigurationGates.AllowAll), t => TenantFileObservationContract.IsTool(t.Name));
    }
}
