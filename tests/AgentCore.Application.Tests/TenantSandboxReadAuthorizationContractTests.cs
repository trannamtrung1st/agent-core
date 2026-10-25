using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tests;

public sealed class TenantSandboxReadAuthorizationContractTests
{
    [Theory] [InlineData(SandboxReadKind.Profiles)] [InlineData(SandboxReadKind.Status)] [InlineData(SandboxReadKind.Process)]
    public void Typed_read_intent_matches_only_its_exact_kind_and_receipt_and_rejects_caller_scopes(SandboxReadKind kind)
    {
        var id = Guid.NewGuid(); var name = TenantSandboxReadAuthorizationContract.Operation(kind);
        var json = kind == SandboxReadKind.Process ? JsonSerializer.Serialize(new { operation = name, executionId = id }) : JsonSerializer.Serialize(new { operation = name });
        var intent = TenantApprovalToolContract.Parse(new("read", ToolCatalog.RequestAuthorization, json));
        var value = new PreparedSandboxRead(kind, Guid.NewGuid(), kind == SandboxReadKind.Process ? id : null, null, "[]", new string('a', 64));
        var operation = TenantApprovalRules.PrepareSandboxRead(Guid.NewGuid(), Guid.NewGuid(), value, 1, 1);
        Assert.True(intent.Matches(operation)); Assert.Equal(operation, TenantApprovalRules.Rebuild(operation)); Assert.Equal("sandbox.read", operation.Permission);
        Assert.False(operation.IsLocalConfiguration); Assert.False((intent with { CredentialReference = "mixed" }).Matches(operation));
        if (kind == SandboxReadKind.Process) Assert.False(intent.Matches(operation with { SandboxRead = value with { ExecutionId = Guid.NewGuid() } }));
        var changed = TenantApprovalRules.PrepareSandboxRead(Guid.NewGuid(), operation.InstanceId, value, 2, 2);
        Assert.Equal(operation.OperationFingerprint, changed.OperationFingerprint); Assert.NotEqual(operation.ActionHash, changed.ActionHash);
        Assert.NotEqual(operation.OperationFingerprint, (operation with { SandboxRead = value with { Generation = Guid.NewGuid() } }).OperationFingerprint);
        foreach (var key in new[] { "tenantId", "sessionId", "generation", "profileId", "profileReferencesJson", "targetHash", "permission", "expectedRevision", "command", "persona" })
        {
            var input = JsonNode.Parse(json)!.AsObject(); input[key] = "untrusted";
            Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("read", ToolCatalog.RequestAuthorization, input.ToJsonString())));
        }
        Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("read", ToolCatalog.RequestAuthorization, json.Replace("\"operation\":", "\"operation\":\"inspect_sandbox_status\",\"operation\":"))));
        foreach (var refs in new[] { "null", "{}", "[{}]", "[{\"Id\":\"foreign\",\"Revision\":1}]", "[] " })
            Assert.Throws<ArgumentException>(() => TenantApprovalRules.PrepareSandboxRead(Guid.NewGuid(), operation.InstanceId, value with { ProfileReferencesJson = refs }, 1, 1));
    }
    [Theory] [InlineData(SandboxReadKind.Profiles)] [InlineData(SandboxReadKind.Status)] [InlineData(SandboxReadKind.Process)]
    public async Task Read_review_offering_requires_the_exact_read_capability_without_Settings_or_execution_configuration(SandboxReadKind kind)
    {
        var definition = await CapabilityProjectionTests.Definition();
        definition = definition with { SelfManagement = HarnessManagementPolicy.Disabled, SandboxPolicy = null, ExecutionSandboxProfiles = [],
            ExecutionPolicy = definition.ExecutionPolicy! with { Capabilities = new("Selected", [TenantSandboxReadOperationRules.Tool(kind)]) } };
        var session = Guid.NewGuid(); var activation = Guid.NewGuid();
        var context = CapabilityProjectionTests.Context(definition) with { AgentInstanceId = Guid.NewGuid(), OwnedSessionId = session,
            ExecutionContext = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), session, Guid.NewGuid(), activation, activation, null, null, ExecutionOrigin.UserMessage) };
        var offered = ToolCatalog.For(definition, context, ToolConfigurationGates.AllowAll);
        var tool = Assert.Single(offered, t => t.Name == ToolCatalog.RequestAuthorization);
        Assert.Contains(TenantSandboxReadAuthorizationContract.Operation(kind), tool.ParametersJson);
        foreach (var other in Enum.GetValues<SandboxReadKind>().Where(k => k != kind)) Assert.DoesNotContain(TenantSandboxReadAuthorizationContract.Operation(other), tool.ParametersJson);
        foreach (var operation in new[] { "update_persona", "update_preferences", "run_sandbox_command", "stop_sandbox_process", "reset_sandbox_environment" }) Assert.DoesNotContain(operation, tool.ParametersJson);
        Assert.DoesNotContain(ToolCatalog.For(definition, context with { ExecutionContext = null }, ToolConfigurationGates.AllowAll), t => t.Name == ToolCatalog.RequestAuthorization);
        Assert.Empty(ToolCatalog.For(definition, context with { OwnedSessionId = Guid.NewGuid() }, ToolConfigurationGates.AllowAll));
    }
}
