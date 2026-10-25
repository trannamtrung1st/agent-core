using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tests;

public sealed class TenantSandboxInspectionContractTests
{
    [Fact]
    public void Inspection_accepts_only_empty_or_exact_Core_receipt_arguments_and_no_authority_fields()
    {
        foreach (var name in new[] { ToolCatalog.SandboxProfiles, ToolCatalog.SandboxStatus })
        {
            Assert.Null(TenantSandboxInspectionContract.Parse(new("read", name, "{}")));
            foreach (var json in new[] { "[]", "null", "{\"sessionId\":\"other\"}", "{\"profileId\":\"other\"}", "{\"refresh\":true}", "{\"expectedRevision\":1}" })
                Assert.Throws<ArgumentException>(() => TenantSandboxInspectionContract.Parse(new("read", name, json)));
        }
        var id = Guid.NewGuid(); Assert.Equal(id, TenantSandboxInspectionContract.Parse(new("read", ToolCatalog.SandboxProcessStatus, "{\"executionId\":\"" + id + "\"}")));
        foreach (var json in new[] { "{}", "{\"executionId\":null}", "{\"executionId\":123}", "{\"executionId\":\"00000000-0000-0000-0000-000000000000\"}",
            "{\"executionId\":\"provider-handle\"}", "{\"executionId\":\"" + id + "\",\"executionId\":\"" + id + "\"}", "{\"executionId\":\"" + id + "\",\"tenantId\":\"other\"}" })
            Assert.Throws<ArgumentException>(() => TenantSandboxInspectionContract.Parse(new("read", ToolCatalog.SandboxProcessStatus, json)));
        Assert.Throws<ArgumentException>(() => TenantSandboxInspectionContract.Parse(new("read", ToolCatalog.SandboxReset, "{}")));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Exact_tenant_inspection_is_contextual_without_execution_or_host_readiness_and_keeps_legacy_projection(bool capabilities)
    {
        var definition = await CapabilityProjectionTests.Definition();
        var names = new[] { ToolCatalog.SandboxProfiles, ToolCatalog.SandboxStatus, ToolCatalog.SandboxProcessStatus };
        definition = definition with { SandboxPolicy = new("p-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", ["p-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"]),
            SelfManagement = HarnessManagementPolicy.Disabled, ExecutionPolicy = definition.ExecutionPolicy! with {
                Capabilities = capabilities ? new("Selected", names) : null, ToolAllowlist = names } };
        var session = Guid.NewGuid(); var activation = Guid.NewGuid();
        var context = CapabilityProjectionTests.Context(definition) with { AgentInstanceId = Guid.NewGuid(), OwnedSessionId = session,
            ExecutionContext = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), session, Guid.NewGuid(), activation, activation, null, null, ExecutionOrigin.UserMessage) };
        var offline = ToolConfigurationGates.From(_ => false);
        var tools = ToolCatalog.For(definition, context, offline);
        Assert.Equal(3, tools.Count(t => TenantSandboxInspectionContract.IsInspection(t.Name)));
        Assert.DoesNotContain(tools, t => t.Name == ToolCatalog.SandboxExec);
        var review = Assert.Single(tools, t => t.Name == ToolCatalog.RequestAuthorization);
        foreach (var operation in new[] { "inspect_sandbox_profiles", "inspect_sandbox_status", "inspect_sandbox_process" }) Assert.Contains(operation, review.ParametersJson);
        Assert.DoesNotContain("run_sandbox_command", review.ParametersJson);
        Assert.Contains("stored", Assert.Single(tools, t => t.Name == ToolCatalog.SandboxStatus).Description);
        foreach (var name in names)
        {
            Assert.Equal(ToolPolicyDecision.Allow, ToolPolicy.EvaluateExecution(definition, name, offline,
                admission: new(false, TriggerKind.UserTurn, AgentInstanceId: context.AgentInstanceId, AgentRunId: context.ExecutionContext.RunId,
                    OwnedSessionId: session, TenantId: context.ExecutionContext.TenantId)));
            Assert.DoesNotContain(ToolCatalog.For(definition, context with { ModelSupportsTools = false }, offline), t => t.Name == name);
            Assert.DoesNotContain(ToolCatalog.For(definition, context with { OwnedSessionId = Guid.NewGuid() }, offline), t => t.Name == name);
        }
        if (capabilities) Assert.DoesNotContain(ToolCatalog.For(definition, context with { ExecutionContext = null }, ToolConfigurationGates.AllowAll), t => t.Name == ToolCatalog.SandboxStatus);
        var withoutPolicy = definition with { SandboxPolicy = null };
        Assert.Equal(3, ToolCatalog.For(withoutPolicy, context with { Definition = withoutPolicy }, offline).Count(t => TenantSandboxInspectionContract.IsInspection(t.Name)));
        var legacy = ToolCatalog.For(definition, context with { ExecutionContext = null, LoadedCapabilityIds = names }, ToolConfigurationGates.AllowAll);
        Assert.Equal(ToolRegistry.Get(ToolCatalog.SandboxStatus).ModelDefinition.Description, Assert.Single(legacy, t => t.Name == ToolCatalog.SandboxStatus).Description);
        var restricted = definition with { ExecutionPolicy = definition.ExecutionPolicy with { Capabilities = capabilities ? new("Selected", []) : null, ToolAllowlist = [] } };
        Assert.DoesNotContain(ToolCatalog.For(restricted, context with { Definition = restricted }, offline), t => TenantSandboxInspectionContract.IsInspection(t.Name));
    }
}
