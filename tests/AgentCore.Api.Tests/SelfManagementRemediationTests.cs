using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Credentials;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;
public sealed class SelfManagementRemediationTests
{
    [Theory]
    [InlineData("capabilities", "{\"selected\":[\"unknown.external.effect\"]}")]
    [InlineData("providerPreferences", "{\"languageModel\":\"unknown-provider\"}")]
    [InlineData("modelDefaults", "{\"catalogKey\":\"unknown-model\"}")]
    [InlineData("memoryPolicy", "{\"userRetrieval\":true}")]
    public async Task Invalid_definition_or_host_selection_is_rejected_before_approval(string section, string patch)
    {
        await using var host = new AgentCoreApiFactory();
        var services = host.Services;
        var owner = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 23);
        var policy = await services.GetRequiredService<SelfManagementService>().UpdateAsync(owner.InstanceId, owner.Revision,
            new(new(HarnessManagementMode.Assisted), new([HarnessManagementScope.Settings])));
        var args = JsonSerializer.SerializeToElement(new { expectedInstanceRevision = policy.InstanceRevision, section,
            set = JsonSerializer.Deserialize<JsonElement>(patch), clear = Array.Empty<string>() });
        var preview = await services.GetRequiredService<SessionToolExecutor>().PrepareManagementApprovalAsync(
            new("invalid", "settings.patch", args.GetRawText()), args,
            new(false, TriggerKind.UserTurn, AgentInstanceId: owner.InstanceId), default);
        Assert.Null(preview.Preparation);
        Assert.NotNull(preview.ErrorJson);
        Assert.Equal(policy.InstanceRevision, (await services.GetRequiredService<SelfManagementService>().ReadAsync(owner.InstanceId)).InstanceRevision);
    }

    [Fact]
    public async Task Options_bind_owner_section_policy_and_revision_and_invalid_schema_never_writes()
    {
        await using var host = new AgentCoreApiFactory();
        var s = host.Services; var owner = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant",23);
        var management = s.GetRequiredService<SelfManagementService>();
        var view = await management.UpdateAsync(owner.InstanceId,owner.Revision,new(new(HarnessManagementMode.Managed),new([HarnessManagementScope.Settings]),new([ManagementAutoApply.ConversationPreferences])));
        var definition = (await s.GetRequiredService<AgentRunConfigurationResolver>().ResolveAsync(owner.InstanceId)).Configuration.Definition;
        var tools = s.GetRequiredService<SessionToolExecutor>();var session = Guid.NewGuid();
        var admission = new ToolExecutionAdmission(false,TriggerKind.UserTurn,AgentInstanceId:owner.InstanceId);
        var inspect = await tools.ExecuteAsync(definition,session,new("inspect","configuration.inspect", "{\"goal\":\"Always answer briefly.\",\"target\":\"settings.patch\"}"),100000,admission:admission);
        using var metadata = JsonDocument.Parse(inspect.Text);
        var option = metadata.RootElement.GetProperty("remediationOptions").EnumerateArray().Single(o=>o.GetProperty("section").GetString()=="conversationPolicy").GetProperty("option").GetString();
        async Task<string> Patch(string token) => (await tools.ExecuteAsync(definition,session,new("patch","configuration.propose",JsonSerializer.Serialize(new {
            expectedInstanceRevision=view.InstanceRevision,option=token,target="settings.patch",goal="Always answer briefly.",section="conversationPolicy",set=new {responseLength="concise"},clear=Array.Empty<string>()})),100000,admission:admission)).Text;
        Assert.Contains("Conflict",await Patch(new string('0',64)));
        Assert.Equal(view.InstanceRevision,(await management.ReadAsync(owner.InstanceId)).InstanceRevision);
        Assert.Contains("\"outcome\":\"applied\"",await Patch(option!));
        Assert.Contains("Conflict",await Patch(option!));
        Assert.Equal("concise",(await s.GetRequiredService<AgentInstanceSettingsService>().ReadAsync(owner.InstanceId,"conversationPolicy")).Effective["responseLength"]);
        using var invalid = JsonDocument.Parse("{\"expectedInstanceRevision\":1,\"section\":\"conversationPolicy\",\"set\":{\"responseLength\":true},\"clear\":[]}");
        Assert.Throws<ArgumentException>(()=>ConfigurationTools.Validate("settings.patch",invalid.RootElement));
        using var duplicate = JsonDocument.Parse("{\"expectedInstanceRevision\":1,\"section\":\"conversationPolicy\",\"set\":{\"responseLength\":\"concise\",\"responseLength\":\"verbose\"},\"clear\":[]}");
        Assert.Throws<ArgumentException>(()=>ConfigurationTools.Validate("settings.patch",duplicate.RootElement));
    }

    [Fact]
    public async Task Credential_binding_requires_exact_approval_preserves_pins_and_audits_without_secret()
    {
        await using var host = new AgentCoreApiFactory();var s=host.Services;
        var owner=await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant",23);
        var view=await s.GetRequiredService<SelfManagementService>().UpdateAsync(owner.InstanceId,owner.Revision,new(new(HarnessManagementMode.Managed),new([HarnessManagementScope.Connections]),new(Enum.GetValues<ManagementAutoApply>())));
        var credentials=s.GetRequiredService<CredentialService>();const string secret="fixture-secret-keep-private-93856";
        var credential=await credentials.CreateAsync("Test store","Password",null,["https://store.example.test"],secret);
        var admitted=(await s.GetRequiredService<AgentRunConfigurationResolver>().ResolveAsync(owner.InstanceId)).Configuration;
        var executor=s.GetRequiredService<SessionToolExecutor>();var session=Guid.NewGuid();
        var admission=new ToolExecutionAdmission(false,TriggerKind.UserTurn,AgentInstanceId:owner.InstanceId);
        var inspect=await executor.ExecuteAsync(admitted.Definition,session,new("inspect","configuration.inspect","{}"),100000,admission:admission);
        Assert.DoesNotContain(secret,inspect.Text);Assert.Contains(credential.CredentialId.ToString(),inspect.Text);
        var args=JsonSerializer.SerializeToElement(new {expectedInstanceRevision=view.InstanceRevision,credentialId=credential.CredentialId,reference="store"});
        var call=new ModelToolCall("bind","credentials.binding.propose",args.GetRawText());
        Assert.Contains("approval_required",(await executor.ExecuteAsync(admitted.Definition,session,call,100000,admission:admission)).Text);
        var preview=await executor.PrepareManagementApprovalAsync(call,args,admission,default);
        Assert.NotNull(preview.Preparation);Assert.DoesNotContain(secret,JsonSerializer.Serialize(preview.Preparation));
        var grant=new ToolApprovalGrant(Guid.NewGuid(),call.Name,ToolActionHash.Compute(call.Name,args),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid());
        var bound=await executor.ExecuteAsync(admitted.Definition,session,call,100000,approvalGrant:grant,admission:admission);
        Assert.Contains("\"outcome\":\"applied\"",bound.Text);Assert.DoesNotContain(secret,bound.Text);
        Assert.Equal(view.InstanceRevision+1,(await s.GetRequiredService<IAgentInstanceStore>().FindAsync(owner.InstanceId))!.Revision);
        await Assert.ThrowsAsync<AgentCoreException>(()=>credentials.ResolvePasswordAsync(owner.InstanceId,"store","https://store.example.test",default,admitted.CredentialBindings).AsTask());
        var next=(await s.GetRequiredService<AgentRunConfigurationResolver>().ResolveAsync(owner.InstanceId)).Configuration;
        Assert.Equal(secret,await credentials.ResolvePasswordAsync(owner.InstanceId,"store","https://store.example.test",default,next.CredentialBindings));
        Assert.NotEqual(admitted.ConfigurationHash,next.ConfigurationHash);
        var events=await s.GetRequiredService<IAdminEventStore>().ListAsync(new(TargetType:"agent.instance",TargetId:owner.InstanceId.ToString("D")));
        var audit=Assert.Single(events,e=>e.Operation==AdminEventOperationKind.CredentialBindingChanged);
        Assert.Equal(AdminEventActorKind.Agent,audit.ActorKind);Assert.DoesNotContain(secret,audit.SummaryJson);
        Assert.DoesNotContain("\"outcome\":\"applied\"",(await executor.ExecuteAsync(admitted.Definition,session,call,100000,approvalGrant:grant,admission:admission)).Text);
    }
    [Fact]
    public async Task Inspection_distinguishes_definition_denial_from_instance_selection_without_forging_authority()
    {
        await using var host = new AgentCoreApiFactory();
        var services = host.Services;
        var owner = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 23);
        var policy = await services.GetRequiredService<SelfManagementService>().UpdateAsync(owner.InstanceId, owner.Revision,
            new(new(HarnessManagementMode.Assisted), new([HarnessManagementScope.Settings])));
        var resolver = services.GetRequiredService<AgentRunConfigurationResolver>();
        var baseline = (await resolver.ResolveAsync(owner.InstanceId)).Configuration.Definition;
        var selected = RoleEnvironments.Of(baseline).ToolList.Where(name => name != "workspace.read").ToArray();
        await services.GetRequiredService<AgentInstanceSettingsService>().PatchAsync(owner.InstanceId, "capabilities", policy.InstanceRevision,
            new Dictionary<string, JsonElement> { ["selected"] = JsonSerializer.SerializeToElement(selected) }, []);
        var current = (await resolver.ResolveAsync(owner.InstanceId)).Configuration.Definition;
        var executor = services.GetRequiredService<SessionToolExecutor>();
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: owner.InstanceId);
        async Task<JsonDocument> Inspect(string target) => JsonDocument.Parse((await executor.ExecuteAsync(current, Guid.NewGuid(),
            new("inspect", "configuration.inspect", JsonSerializer.Serialize(new { target })), 100000, admission: admission)).Text);
        using var narrowed = await Inspect("workspace.read");
        Assert.Equal("instance_restriction", narrowed.RootElement.GetProperty("blocker").GetProperty("outcome").GetString());
        using var denied = await Inspect("host.restart");
        Assert.Equal("definition_limit", denied.RootElement.GetProperty("blocker").GetProperty("reason").GetString());
        Assert.Contains(owner.InstanceId.ToString("D"), denied.RootElement.GetProperty("blocker").GetProperty("adminPath").GetString());
        Assert.False(RolePermissions.AllowsTool(current, "workspace.read"));
        Assert.False(RolePermissions.AllowsTool(current, "host.restart"));
    }

}
