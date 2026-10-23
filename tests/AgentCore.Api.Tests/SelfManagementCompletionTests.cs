using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;

namespace AgentCore.Api.Tests;

public sealed class SelfManagementCompletionTests
{
    [Theory]
    [InlineData("I cannot send this email", false)]
    [InlineData("Help me sign in", true)]
    public async Task Unowned_inspection_cannot_disclose_provider_or_credential_diagnosis(string goal,bool configured)
    {
        await using var host=new DiagnosisHost(configured);
        var s=host.Services;
        var owner=await NativeInstanceConfigurationSeed.CreateAsync(s, "general-assistant",24);
        await NativeInstanceConfigurationSeed.PolicyAsync(s, owner.InstanceId,owner.Revision,
            new(new(HarnessManagementMode.Assisted),new([HarnessManagementScope.Settings,HarnessManagementScope.Connections])));
        var definition=(await NativeInstanceConfigurationSeed.ResolveAsync(s, owner.InstanceId)).Configuration.Definition;
        using var observed=JsonDocument.Parse((await s.GetRequiredService<SessionToolExecutor>().ExecuteAsync(definition,Guid.NewGuid(),
            new("inspect","configuration.inspect",JsonSerializer.Serialize(new{goal})),100000,
            admission:new(false,TriggerKind.UserTurn,AgentInstanceId:owner.InstanceId))).Text);
        Assert.Equal("forbidden",observed.RootElement.GetProperty("error").GetString());
        foreach (var field in new[] { "blocker", "remediationOptions", "skills", "resources" })
            Assert.False(observed.RootElement.TryGetProperty(field, out _));
    }

    private sealed class DiagnosisHost(bool configured):AgentCoreApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s=>{s.RemoveAll<IToolConfigurationGate>();s.AddSingleton(configured?ToolConfigurationGates.AllowAll:ToolConfigurationGates.Unconfigured);});
        }
    }

    [Theory]
    [InlineData("skills.create")]
    [InlineData("skills.update")]
    [InlineData("skills.set_enabled")]
    [InlineData("skills.delete")]
    [InlineData("skills.customize")]
    public async Task Unowned_Skill_delegation_cannot_replace_canonical_tenant_authority(string name)
    {
        await using var host = new AgentCoreApiFactory(); var s = host.Services;
        var owner = await NativeInstanceConfigurationSeed.CreateAsync(s, "general-assistant", 24);
        await NativeInstanceConfigurationSeed.PolicyAsync(s, owner.InstanceId, owner.Revision,
            new(new(HarnessManagementMode.Managed), new([HarnessManagementScope.Skills]), new([ManagementAutoApply.SkillChanges])));
        var definition = (await NativeInstanceConfigurationSeed.ResolveAsync(s, owner.InstanceId)).Configuration.Definition;
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId:owner.InstanceId,
            Harness:new(new(HarnessManagementMode.Managed, Enum.GetValues<HarnessManagementScope>(), [], [], ManagedAutoApply: Enum.GetValues<ManagementAutoApply>()), 1, 23));
        Assert.Equal(ToolPolicyDecision.Deny, ToolPolicy.EvaluateExecution(definition, name, ToolConfigurationGates.Unconfigured, admission:admission));
    }
}
