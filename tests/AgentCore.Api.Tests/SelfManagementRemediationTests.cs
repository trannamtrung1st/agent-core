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
    public void Settings_schema_rejects_non_text_preferences_and_duplicate_fields()
    {
        using var invalid = JsonDocument.Parse("{\"expectedInstanceRevision\":1,\"section\":\"conversationPolicy\",\"set\":{\"responseLength\":true},\"clear\":[]}");
        Assert.Throws<ArgumentException>(()=>ConfigurationTools.Validate("settings.patch",invalid.RootElement));
        using var duplicate = JsonDocument.Parse("{\"expectedInstanceRevision\":1,\"section\":\"conversationPolicy\",\"set\":{\"responseLength\":\"concise\",\"responseLength\":\"verbose\"},\"clear\":[]}");
        Assert.Throws<ArgumentException>(()=>ConfigurationTools.Validate("settings.patch",duplicate.RootElement));
    }
    [Fact]
    public async Task Unowned_inspection_cannot_disclose_definition_or_selection_blockers()
    {
        await using var host = new AgentCoreApiFactory();
        var services = host.Services;
        var owner = await NativeInstanceConfigurationSeed.CreateAsync(services, "general-assistant", 35);
        var policy = await NativeInstanceConfigurationSeed.PolicyAsync(services, owner.InstanceId, owner.Revision,
            new(new(HarnessManagementMode.Assisted), new([HarnessManagementScope.Settings])));
        var resolver = services;
        var baseline = (await NativeInstanceConfigurationSeed.ResolveAsync(resolver, owner.InstanceId)).Configuration.Definition;
        var selected = DefinitionExecutionPolicies.Of(baseline).ToolList.Where(name => name != "workspace.read").ToArray();
        await NativeInstanceConfigurationSeed.SettingsAsync(services, owner.InstanceId, "capabilities", policy.InstanceRevision,
            new Dictionary<string, JsonElement> { ["selected"] = JsonSerializer.SerializeToElement(selected) }, []);
        var current = (await NativeInstanceConfigurationSeed.ResolveAsync(resolver, owner.InstanceId)).Configuration.Definition;
        var executor = services.GetRequiredService<SessionToolExecutor>();
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: owner.InstanceId);
        async Task<JsonDocument> Inspect(string target) => JsonDocument.Parse((await executor.ExecuteAsync(current, Guid.NewGuid(),
            new("inspect", "configuration.inspect", JsonSerializer.Serialize(new { target })), 100000, admission: admission)).Text);
        using var narrowed = await Inspect("workspace.read");
        Assert.Equal("forbidden", narrowed.RootElement.GetProperty("error").GetString());
        Assert.False(narrowed.RootElement.TryGetProperty("blocker", out _));
        using var denied = await Inspect("host.restart");
        Assert.Equal("forbidden", denied.RootElement.GetProperty("error").GetString());
        Assert.False(denied.RootElement.TryGetProperty("blocker", out _));
        Assert.False(RolePermissions.AllowsTool(current, "workspace.read"));
        Assert.False(RolePermissions.AllowsTool(current, "host.restart"));
    }

}
