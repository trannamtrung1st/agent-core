using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Tests;

public sealed class TenantWorkspaceMutationAuthorizationContractTests
{
    [Fact]
    public async Task Mutation_only_capability_offers_only_its_closed_intent_for_human_tenant_execution()
    {
        var definition = await CapabilityProjectionTests.Definition();
        definition = definition with { SelfManagement = AgentCore.Domain.Definitions.HarnessManagementPolicy.Disabled,
            ExecutionPolicy = definition.ExecutionPolicy! with { Capabilities = null, ToolAllowlist = [ToolCatalog.WorkspaceWrite] } };
        var session = Guid.NewGuid(); var activation = Guid.NewGuid();
        var context = CapabilityProjectionTests.Context(definition) with { AgentInstanceId = Guid.NewGuid(), OwnedSessionId = session,
            ExecutionContext = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), session, Guid.NewGuid(), activation, activation, null, null, AgentCore.Domain.Conversation.ExecutionOrigin.UserMessage) };
        var tool = Assert.Single(ToolCatalog.For(definition, context, ToolConfigurationGates.AllowAll), t => t.Name == ToolCatalog.RequestAuthorization);
        Assert.Contains("mutate_workspace", tool.ParametersJson); Assert.Contains("workspace.write", tool.ParametersJson); Assert.DoesNotContain("workspace.delete", tool.ParametersJson);
        Assert.DoesNotContain("inspect_workspace", tool.ParametersJson); Assert.DoesNotContain("update_persona", tool.ParametersJson);
        Assert.DoesNotContain(ToolCatalog.For(definition, context with { ExecutionContext = null }, ToolConfigurationGates.AllowAll), t => t.Name == ToolCatalog.RequestAuthorization);
        Assert.DoesNotContain(ToolCatalog.For(definition, context with { ExecutionContext = new(context.ExecutionContext.TenantId, context.ExecutionContext.AgentPrincipalId, null, session, context.ExecutionContext.RunId, activation, activation, null, null, AgentCore.Domain.Conversation.ExecutionOrigin.ScheduledOccurrence) }, ToolConfigurationGates.AllowAll), t => t.Name == ToolCatalog.RequestAuthorization);
    }
    [Fact]
    public void Exact_mutation_envelope_accepts_full_32_KiB_literal_without_the_normal_16_KiB_truncation()
    {
        var content = new string('a', 32768); var session = Guid.NewGuid();
        var call = new ModelToolCall("literal", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = "mutate_workspace",
            mutation = new { tool = ToolCatalog.WorkspaceWrite, arguments = new { path = "/working/exact.txt", content } } }));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(call.ArgumentsJson) > TenantApprovalToolContract.MaxArgumentBytes);
        var intent = TenantApprovalToolContract.Parse(call); var mutation = Assert.IsType<TenantWorkspaceMutationIntent>(intent.WorkspaceMutation);
        var args = TenantWorkspaceMutationContract.Parse(session, mutation.Tool, mutation.ArgumentsJson); Assert.Equal(content, args.Content);
        var op = TenantApprovalRules.PrepareWorkspaceMutation(Guid.NewGuid(), Guid.NewGuid(), TenantApprovalRules.MutationParameters(session, args, new('a', 64)), 1, 1);
        Assert.True(intent.Matches(op)); Assert.False((intent with { Persona = new("n", "r", "d", "t") }).Matches(op));
        Assert.False(intent.Matches(TenantApprovalRules.PrepareWorkspaceMutation(Guid.NewGuid(), op.InstanceId,
            TenantApprovalRules.MutationParameters(session, TenantWorkspaceMutationContract.Parse(session, mutation.Tool, """{"path":"/working/exact.txt","content":"different"}"""), new('a', 64)), 1, 1)));
    }

    [Theory]
    [InlineData("""{"operation":"mutate_workspace","mutation":{"tool":"workspace.write","arguments":{"path":"/working/a","content":"x"},"principalId":"forged"}}""")]
    [InlineData("""{"operation":"mutate_workspace","mutation":{"tool":"workspace.read","arguments":{"path":"/working/a"}}}""")]
    [InlineData("""{"operation":"mutate_workspace","mutation":{"tool":"workspace.delete","arguments":{"path":"/working/a","grantId":"forged"}}}""")]
    [InlineData("""{"operation":"mutate_workspace","mutation":{"tool":"workspace.mkdir","arguments":{"path":"/working/a"}},"permission":"workspaces.write"}""")]
    public void Exact_mutation_envelope_rejects_read_tools_and_caller_authority_fields(string json) =>
        Assert.Throws<ArgumentException>(() => TenantApprovalToolContract.Parse(new("forged", ToolCatalog.RequestAuthorization, json)));
}
