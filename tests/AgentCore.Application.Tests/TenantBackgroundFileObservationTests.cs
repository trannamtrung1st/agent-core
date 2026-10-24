using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tests;

public sealed class TenantBackgroundFileObservationTests
{
    [Fact]
    public async Task Legacy_background_output_does_not_activate_the_tenant_file_gate()
    {
        var definition = await CapabilityProjectionTests.Definition([ToolCatalog.WorkspaceWrite]);
        var context = CapabilityProjectionTests.Context(definition) with { ExecutionContext = null,
            OutputContract = AgentRunOutputContract.BackgroundOutcome, LoadedCapabilityIds = [ToolCatalog.WorkspaceWrite] };
        Assert.False(TenantTaskExecutionContract.IsBackground(context));
        Assert.Contains(ToolProjectionService.Project(definition, context, ToolConfigurationGates.AllowAll), t => t.Name == ToolCatalog.WorkspaceWrite);
    }
    [Theory] [InlineData(ExecutionOrigin.ImmediateBackground)] [InlineData(ExecutionOrigin.ApprovalContinuation)]
    public async Task Background_projection_preserves_completion_and_exposes_converted_file_and_native_operations_with_exact_consent(ExecutionOrigin origin)
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
        Assert.All(tools, t => Assert.True(TenantTaskExecutionContract.IsTool(t.Name) || TenantTaskExecutionContract.IsNativeInspection(t.Name) || IamToolContracts.IsInspection(t.Name) || TenantCompletionToolContract.IsTool(t.Name) || TenantBackgroundToolContract.IsTool(t.Name) || t.Name is ToolCatalog.ExecutionWait or ToolCatalog.RequestAuthorization or ToolCatalog.CapabilitiesLoad or ToolCatalog.SkillsLoad, t.Name));
        var authorization = Assert.Single(tools, t => t.Name == ToolCatalog.RequestAuthorization);
        using var schema = JsonDocument.Parse(authorization.ParametersJson);
        static IEnumerable<string?> Operations(JsonElement branch)
        {
            if (branch.TryGetProperty("oneOf", out var alternatives))
                foreach (var alternative in alternatives.EnumerateArray()) foreach (var operation in Operations(alternative)) yield return operation;
            if (branch.TryGetProperty("properties", out var properties) && properties.TryGetProperty("operation", out var operationSchema))
            {
                if (operationSchema.TryGetProperty("const", out var constant)) yield return constant.GetString();
                else foreach (var operation in operationSchema.GetProperty("enum").EnumerateArray()) yield return operation.GetString();
            }
        }
        var operations = Operations(schema.RootElement).Distinct().ToArray();
        Assert.All(operations, operation => Assert.Contains(operation, new[] { "send_http_request", "mutate_automation", "inspect_management", "inspect_automations", "inspect_continuity", "manage_memory", "start_background_work", "inspect_workspace", "read_attachment", "verify_artifact", "mutate_workspace", "publish_artifact", "publish_workspace_artifact", "run_browser_command", "run_sandbox_command", "stop_sandbox_process", "reset_sandbox_environment", "inspect_sandbox_profiles", "inspect_sandbox_status", "inspect_sandbox_process", "inspect_background_completion", "read_connected_account", "use_connected_account", "inspect_credential_metadata" }));
        Assert.DoesNotContain(ToolCatalog.BrowserFillCredential, authorization.ParametersJson);
        Assert.Contains("run_browser_command", operations);
        Assert.Contains("inspect_workspace", operations); Assert.Contains("verify_artifact", operations);
        Assert.Contains("mutate_workspace", operations); Assert.Contains("publish_artifact", operations); Assert.Contains("publish_workspace_artifact", operations);
        Assert.DoesNotContain(ToolProjectionService.Project(definition, context with { ModelSupportsTools = false }, ToolConfigurationGates.AllowAll), t => TenantTaskExecutionContract.IsTool(t.Name));
        Assert.DoesNotContain(ToolProjectionService.Project(definition, context with { OwnedSessionId = Guid.NewGuid() }, ToolConfigurationGates.AllowAll), t => TenantTaskExecutionContract.IsTool(t.Name));
    }
}
