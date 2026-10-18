using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tests;

public sealed class TenantSandboxControlContractTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public void Exact_control_intent_rejects_caller_authority_and_matches_only_its_saved_kind_and_process(bool stop)
    {
        var id = Guid.NewGuid(); var json = stop ? JsonSerializer.Serialize(new { operation = "stop_sandbox_process", executionId = id }) : "{\"operation\":\"reset_sandbox_environment\"}";
        var intent = TenantApprovalToolContract.Parse(new("call", ToolCatalog.RequestAuthorization, json));
        var control = new PreparedSandboxControl(stop ? SandboxControlKind.StopProcess : SandboxControlKind.ResetEnvironment, Guid.NewGuid(), "p-" + new string('a', 32), 1,
            Guid.NewGuid(), id, 2, 3, 1, new string('b', 64));
        var op = TenantApprovalRules.PrepareSandboxControl(Guid.NewGuid(), Guid.NewGuid(), control, 1, 1);
        Assert.True(intent.Matches(op)); Assert.False((intent with { CredentialReference = "mixed" }).Matches(op));
        Assert.False(intent.Matches(op with { SandboxControl = control with { Kind = stop ? SandboxControlKind.ResetEnvironment : SandboxControlKind.StopProcess } }));
        if (stop) Assert.False(intent.Matches(op with { SandboxControl = control with { ExecutionId = Guid.NewGuid() } }));
        Assert.Equal("sandbox.manage", op.Permission); Assert.Equal(op, TenantApprovalRules.Rebuild(op)); Assert.False(op.IsLocalConfiguration);
        Assert.Equal(op.OperationFingerprint, (op with { SandboxControl = control with { ObservedLeaseRevision = 9 } }).OperationFingerprint);
        Assert.NotEqual(op.OperationFingerprint, (op with { SandboxControl = control with { Generation = Guid.NewGuid() } }).OperationFingerprint);
        foreach (var key in new[] { "tenantId", "sessionId", "generation", "profileId", "permission", "expectedRevision", "targetHash", "persona", "command" })
        {
            var node = JsonNode.Parse(json)!.AsObject(); node[key] = "untrusted";
            Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("call", ToolCatalog.RequestAuthorization, node.ToJsonString())));
        }
        Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("call", ToolCatalog.RequestAuthorization, json.Replace("\"operation\":", "\"operation\":\"reset_sandbox_environment\",\"operation\":"))));
    }
    [Fact]
    public async Task Exact_control_capability_offers_only_its_Review_schema_without_execution_configuration_or_Settings()
    {
        var definition = await CapabilityProjectionTests.Definition();
        definition = definition with { SelfManagement = HarnessManagementPolicy.Disabled, SandboxPolicy = null, ExecutionSandboxProfiles = [],
            Environment = definition.Environment! with { Capabilities = new("Selected", [ToolCatalog.SandboxReset]) } };
        var session = Guid.NewGuid(); var activation = Guid.NewGuid();
        var context = CapabilityProjectionTests.Context(definition) with { AgentInstanceId = Guid.NewGuid(), OwnedSessionId = session,
            ExecutionContext = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), session, Guid.NewGuid(), activation, activation, null, null, ExecutionOrigin.UserMessage) };
        var offered = ToolCatalog.For(definition, context, ToolConfigurationGates.AllowAll);
        var tool = Assert.Single(offered, t => t.Name == ToolCatalog.RequestAuthorization);
        Assert.Contains("reset_sandbox_environment", tool.ParametersJson); Assert.DoesNotContain("stop_sandbox_process", tool.ParametersJson);
        Assert.DoesNotContain("run_sandbox_command", tool.ParametersJson); Assert.DoesNotContain("update_persona", tool.ParametersJson);
        Assert.DoesNotContain(offered, t => t.Name == ToolCatalog.SandboxReset);
        Assert.DoesNotContain(ToolCatalog.For(definition, context with { ExecutionContext = null }, ToolConfigurationGates.AllowAll), t => t.Name == ToolCatalog.RequestAuthorization);
    }
}
