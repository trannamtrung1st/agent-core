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
