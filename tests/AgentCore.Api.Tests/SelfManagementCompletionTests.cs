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

    [Fact]
    public async Task Diagnosed_remedy_binds_exact_goal_target_values_and_revision_and_has_no_unrelated_inventory()
    {
        await using var host = new AgentCoreApiFactory();
        var s = host.Services;
        var owner = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 24);
        var policy = await s.GetRequiredService<SelfManagementService>().UpdateAsync(owner.InstanceId, owner.Revision,
            new(new(HarnessManagementMode.Assisted), new(Enum.GetValues<HarnessManagementScope>())));
        var resolver = s.GetRequiredService<AgentRunConfigurationResolver>();
        var baseline = (await resolver.ResolveAsync(owner.InstanceId)).Configuration.Definition;
        await s.GetRequiredService<AgentInstanceSettingsService>().PatchAsync(owner.InstanceId, "capabilities", policy.InstanceRevision,
            new Dictionary<string, JsonElement> { ["selected"] = JsonSerializer.SerializeToElement(RoleEnvironments.Of(baseline).ToolList.Where(t => t != ToolCatalog.WorkspaceList).ToArray()) }, []);
        var definition = (await resolver.ResolveAsync(owner.InstanceId)).Configuration.Definition;
        var tools = s.GetRequiredService<SessionToolExecutor>();
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: owner.InstanceId);
        using var inspection = JsonDocument.Parse((await tools.ExecuteAsync(definition, Guid.NewGuid(), new("inspect", "configuration.inspect",
            "{\"goal\":\"List my workspace\",\"target\":\"workspace.list\"}"), 100000, admission: admission)).Text);
        var root = inspection.RootElement;
        Assert.Equal("instance_selection", root.GetProperty("blocker").GetProperty("reason").GetString());
        foreach (var inventory in new[] { "skills", "resources", "bindings", "eligibleCredentials" }) Assert.Empty(root.GetProperty(inventory).EnumerateArray());
        Assert.Equal("capabilities", Assert.Single(root.GetProperty("settings").EnumerateArray()).GetProperty("section").GetString());
        var remedy = Assert.Single(root.GetProperty("remediationOptions").EnumerateArray());
        Assert.Single(remedy.GetProperty("set").EnumerateObject());
        Assert.Contains(ToolCatalog.WorkspaceList, remedy.GetProperty("set").GetProperty("selected").EnumerateArray().Select(e => e.GetString()));
        async Task<(ToolApprovalPreparation? Preparation, string? ErrorJson)> Preview(string goal, object set) {
            var args = JsonSerializer.SerializeToElement(new { expectedInstanceRevision = root.GetProperty("instanceRevision").GetInt64(),
                option = remedy.GetProperty("option").GetString(), target = ToolCatalog.WorkspaceList, goal, section = "capabilities", set, clear = Array.Empty<string>() });
            return await tools.PrepareManagementApprovalAsync(new("fix", "configuration.propose", args.GetRawText()), args, admission, default);
        }
        Assert.NotNull((await Preview("List my workspace", remedy.GetProperty("set"))).Preparation);
        Assert.Null((await Preview("Send email instead", remedy.GetProperty("set"))).Preparation);
        Assert.Null((await Preview("List my workspace", new { selected = new[] { ToolCatalog.WorkspaceList } })).Preparation);
        using var email = JsonDocument.Parse((await tools.ExecuteAsync(definition, Guid.NewGuid(), new("email", "configuration.inspect",
            "{\"goal\":\"I cannot send this email\"}"), 100000, admission: admission)).Text);
        Assert.Equal(ToolCatalog.EmailSend, email.RootElement.GetProperty("blocker").GetProperty("target").GetString());
        Assert.Equal("available", email.RootElement.GetProperty("blocker").GetProperty("reason").GetString());
        Assert.Empty(email.RootElement.GetProperty("remediationOptions").EnumerateArray());
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

    [Fact]
    public async Task Creation_previews_describe_the_new_content_and_source()
    {
        await using var host = new AgentCoreApiFactory(); var s = host.Services;
        var owner = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant",24);
        var policy = await s.GetRequiredService<SelfManagementService>().UpdateAsync(owner.InstanceId, owner.Revision,
            new(new(HarnessManagementMode.Assisted), new([HarnessManagementScope.Skills,HarnessManagementScope.Resources])));
        var tools=s.GetRequiredService<SessionToolExecutor>();
        var admission=new ToolExecutionAdmission(false,TriggerKind.UserTurn,AgentInstanceId:owner.InstanceId);
        foreach(var (name, body, content) in new[] {
            ("skills.create", new {expectedInstanceRevision=policy.InstanceRevision,name="Checklist",description="Review carefully",procedure="Check observed totals.",projection="OnDemand",enabled=true,requiredCapabilities=Array.Empty<string>()} as object,"Check observed totals."),
            ("resources.upsert", new {expectedInstanceRevision=policy.InstanceRevision,logicalPath="reviews/checklist.md",kind="Knowledge",mediaType="text/markdown",content="Check the observed totals.",source="conversation:user"} as object,"Check the observed totals.")}) {
            var args=JsonSerializer.SerializeToElement(body);
            var preview=await tools.PrepareManagementApprovalAsync(new("create",name,args.GetRawText()),args,admission,default);
            var preparation=preview.Preparation; Assert.NotNull(preparation);
            Assert.Contains("No ",preparation.Details!["Before"]);
            Assert.Contains(content,preparation.Details!["After"]);
            Assert.Equal(name,preparation.Details!["Exact operation"]);
            Assert.Contains("Create an independent",preparation.Details!["Effect"]);
            Assert.Equal(ToolActionHash.Compute(name,args),preparation.ActionHash);
            if(name=="resources.upsert")Assert.Equal("conversation:user",preparation.Details!["Source"]);
        }
    }

    [Fact]
    public async Task Automation_preferences_use_the_existing_model_owner_exact_approval_audit_and_scope()
    {
        await using var host=new AgentCoreApiFactory();var s=host.Services;
        var owner=await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant",24);
        var policy=await s.GetRequiredService<SelfManagementService>().UpdateAsync(owner.InstanceId,owner.Revision,
            new(new(HarnessManagementMode.Managed),new([HarnessManagementScope.AutomationPreferences]),new(Enum.GetValues<ManagementAutoApply>())));
        var definition=(await s.GetRequiredService<AgentRunConfigurationResolver>().ResolveAsync(owner.InstanceId)).Configuration.Definition;
        var tools=s.GetRequiredService<SessionToolExecutor>();var session=Guid.NewGuid();
        var admission=new ToolExecutionAdmission(false,TriggerKind.UserTurn,AgentInstanceId:owner.InstanceId);
        var inspect=await tools.ExecuteAsync(definition,session,new("read","automation.preferences.inspect","{}"),100000,admission:admission);
        Assert.Contains("unattendedModelCatalogKey",inspect.Text);
        var args=JsonSerializer.SerializeToElement(new {expectedInstanceRevision=policy.InstanceRevision,catalogKey="scripted-alpha",reasoningEffort=(string?)null});
        var call=new ModelToolCall("model","automation.preferences.patch",args.GetRawText());
        Assert.Contains("approval_required",(await tools.ExecuteAsync(definition,session,call,100000,admission:admission)).Text);
        Assert.NotNull((await tools.PrepareManagementApprovalAsync(call,args,admission,default)).Preparation);
        var grant=new ToolApprovalGrant(Guid.NewGuid(),call.Name,ToolActionHash.Compute(call.Name,args),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid());
        Assert.Contains("\"outcome\":\"applied\"",(await tools.ExecuteAsync(definition,session,call,100000,approvalGrant:grant,admission:admission)).Text);
        var next=(await s.GetRequiredService<IAgentInstanceStore>().FindAsync(owner.InstanceId))!;
        Assert.Equal("scripted-alpha",next.UnattendedModelCatalogKey);Assert.Equal(policy.InstanceRevision+1,next.Revision);
        Assert.Contains(await s.GetRequiredService<IAdminEventStore>().ListAsync(new(TargetType:"agent.instance",TargetId:owner.InstanceId.ToString("D"))),
            e=>e.ActorKind==AdminEventActorKind.Agent && e.Operation==AdminEventOperationKind.InstanceSettingsChanged && e.SummaryJson.Contains("automationPreferences"));
        Assert.DoesNotContain("\"outcome\":\"applied\"",(await tools.ExecuteAsync(definition,session,call,100000,approvalGrant:grant,admission:admission)).Text);
        await s.GetRequiredService<SelfManagementService>().FreezeAsync(owner.InstanceId,next.Revision);
        Assert.Contains("forbidden",(await tools.ExecuteAsync(definition,session,new("read","automation.preferences.inspect","{}"),100000,admission:admission)).Text);
    }
}
