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
    [InlineData("I cannot send this email", false, "provider_unavailable")]
    [InlineData("Help me sign in", true, "credential_binding_missing")]
    public async Task Goal_reports_actual_provider_or_credential_blocker_without_settings_escalation(string goal,bool configured,string reason)
    {
        await using var host=new DiagnosisHost(configured);
        var s=host.Services;
        var owner=await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant",24);
        await s.GetRequiredService<SelfManagementService>().UpdateAsync(owner.InstanceId,owner.Revision,
            new(new(HarnessManagementMode.Assisted),new([HarnessManagementScope.Settings,HarnessManagementScope.Connections])));
        var definition=(await s.GetRequiredService<AgentRunConfigurationResolver>().ResolveAsync(owner.InstanceId)).Configuration.Definition;
        using var observed=JsonDocument.Parse((await s.GetRequiredService<SessionToolExecutor>().ExecuteAsync(definition,Guid.NewGuid(),
            new("inspect","configuration.inspect",JsonSerializer.Serialize(new{goal})),100000,
            admission:new(false,TriggerKind.UserTurn,AgentInstanceId:owner.InstanceId))).Text);
        Assert.Equal(reason,observed.RootElement.GetProperty("blocker").GetProperty("reason").GetString());
        Assert.Empty(observed.RootElement.GetProperty("remediationOptions").EnumerateArray());
        Assert.Empty(observed.RootElement.GetProperty("skills").EnumerateArray());
        Assert.Empty(observed.RootElement.GetProperty("resources").EnumerateArray());
    }

    private sealed class DiagnosisHost(bool configured):AgentCoreApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s=>{s.RemoveAll<IToolConfigurationGate>();s.AddSingleton(configured?ToolConfigurationGates.AllowAll:ToolConfigurationGates.Unconfigured);});
        }
    }

    [Theory]
    [InlineData("skills.create", ToolPolicyDecision.Allow)]
    [InlineData("skills.update", ToolPolicyDecision.Allow)]
    [InlineData("skills.set_enabled", ToolPolicyDecision.Allow)]
    [InlineData("skills.delete", ToolPolicyDecision.RequireApproval)]
    [InlineData("skills.customize", ToolPolicyDecision.RequireApproval)]
    public async Task Managed_skill_delegation_keeps_delete_and_customization_exact(string name, ToolPolicyDecision expected)
    {
        await using var host = new AgentCoreApiFactory(); var s = host.Services;
        var owner = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 24);
        await s.GetRequiredService<SelfManagementService>().UpdateAsync(owner.InstanceId, owner.Revision,
            new(new(HarnessManagementMode.Managed), new([HarnessManagementScope.Skills]), new([ManagementAutoApply.SkillChanges])));
        var definition = (await s.GetRequiredService<AgentRunConfigurationResolver>().ResolveAsync(owner.InstanceId)).Configuration.Definition;
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId:owner.InstanceId,
            Harness:await s.GetRequiredService<SessionToolExecutor>().HarnessContextAsync(owner.InstanceId, default));
        Assert.Equal(expected, ToolPolicy.EvaluateExecution(definition, name, ToolConfigurationGates.Unconfigured, admission:admission));
    }
}
