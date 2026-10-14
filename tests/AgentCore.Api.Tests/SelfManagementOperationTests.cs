using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Application.Sessions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed class SelfManagementOperationTests
{
    private sealed class SelectionReadySandboxFactory : AgentCoreApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => {
                services.RemoveAll<ISandboxReadiness>();
                services.AddSingleton<ISandboxReadiness>(new SelectionReady());
            });
        }
    }
    private sealed class SelectionReady : ISandboxReadiness
    {
        public bool IsAvailable => true;
        public string State => "Ready";
        public string SafeMessage => "Isolated settings-selection fixture.";
    }

    [Fact]
    public async Task Existing_sandbox_profile_management_remains_available_without_disclosing_or_approving_setup_scripts()
    {
        await using var factory=new SelectionReadySandboxFactory();var services=factory.Services;
        var store=services.GetRequiredService<ISandboxStore>();
        var oldProfileRevision=(await store.GetProfileAsync("general-workbench"))?.Revision ?? 0;
        await store.SaveProfileAsync(new("general-workbench",oldProfileRevision+1,"Workbench","",true,"docker.io/agent-core/workbench@sha256:"+new string('a',64),"profile-private-sentinel"),oldProfileRevision);
        var owner=await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant",30);
        var policy=await services.GetRequiredService<SelfManagementService>().UpdateAsync(owner.InstanceId,owner.Revision,new(new(HarnessManagementMode.Managed),new([HarnessManagementScope.Settings]),new([])));
        var settings=services.GetRequiredService<AgentInstanceSettingsService>();
        // Server readiness alone cannot grant selection. This test supplies explicit compatibility evidence.
        var profile=(await store.GetProfileAsync("general-workbench"))!;
        await Assert.ThrowsAsync<AgentCoreException>(()=>settings.ValidatePatchAsync(owner.InstanceId,"sandboxPolicy",policy.InstanceRevision,
            new Dictionary<string,JsonElement>{["profileId"]=JsonSerializer.SerializeToElement(profile.Id)},[]).AsTask());
        await store.SaveVerificationAsync(new(profile.Id,profile.Revision,DateTimeOffset.UtcNow,true,"Selection fixture; no provisioning is exercised here.",ContractVersion:1));
        var saved=await settings.PatchAsync(owner.InstanceId,"sandboxPolicy",policy.InstanceRevision,new Dictionary<string,JsonElement>{["initializationScript"]=JsonSerializer.SerializeToElement("instance-private-sentinel")},[]);
        var definition=(await services.GetRequiredService<AgentRunConfigurationResolver>().ResolveAsync(owner.InstanceId)).Configuration.Definition;
        var executor=services.GetRequiredService<SessionToolExecutor>();var admission=new ToolExecutionAdmission(false,TriggerKind.UserTurn,AgentInstanceId:owner.InstanceId);
        foreach(var (name,json) in new[]{("settings.list","{}"),("settings.inspect","{\"section\":\"sandboxPolicy\"}"),("configuration.inspect","{}")})
        {
            var result=await executor.ExecuteAsync(definition,Guid.NewGuid(),new("inspect",name,json),100000,admission:admission);
            Assert.DoesNotContain("private-sentinel",result.Text);Assert.Contains("profileId",result.Text);Assert.DoesNotContain("initializationScript",result.Text);
        }
        var args=JsonSerializer.SerializeToElement(new{expectedInstanceRevision=saved.InstanceRevision,section="sandboxPolicy",set=new{profileId="general-workbench"},clear=Array.Empty<string>()});
        var call=new ModelToolCall("choose","settings.patch",args.GetRawText());
        var profilePreview=await executor.PrepareManagementApprovalAsync(call,args,admission,default);
        Assert.Null(profilePreview.ErrorJson);Assert.NotNull(profilePreview.Preparation);
        var grant=new ToolApprovalGrant(Guid.NewGuid(),call.Name,ToolActionHash.Compute(call.Name,args),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid());
        var applied=await executor.ExecuteAsync(definition,Guid.NewGuid(),call,100000,approvalGrant:grant,admission:admission);
        Assert.Contains("\"outcome\":",applied.Text);Assert.DoesNotContain("private-sentinel",applied.Text);
        args=JsonSerializer.SerializeToElement(new{expectedInstanceRevision=saved.InstanceRevision,section="sandboxPolicy",set=new{},clear=new[]{"initializationScript"}});
        var preview=await executor.PrepareManagementApprovalAsync(new("blocked","settings.patch",args.GetRawText()),args,admission,default);
        Assert.Null(preview.Preparation);Assert.DoesNotContain("private-sentinel",preview.ErrorJson!);
        using var denied=JsonDocument.Parse(preview.ErrorJson!);Assert.Equal("Forbidden",denied.RootElement.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Managed_settings_and_text_resources_share_admin_owners_remain_pinned_and_survive_freeze(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "self-management-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Guid id;
        try
        {
            await using (AgentCoreApiFactory factory = sqlite ? new HarnessSqliteFactory(root) : new AgentCoreApiFactory())
            {
                var services = factory.Services;
                var owner = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 23);
                id = owner.InstanceId;
                var policy = services.GetRequiredService<SelfManagementService>();
                var view = await policy.UpdateAsync(id, owner.Revision, new(new(HarnessManagementMode.Managed),
                    new([HarnessManagementScope.Settings, HarnessManagementScope.Resources, HarnessManagementScope.Skills]),
                    new([ManagementAutoApply.ConversationPreferences, ManagementAutoApply.TextResources])));
                var resolver = services.GetRequiredService<AgentRunConfigurationResolver>();
                var admitted = (await resolver.ResolveAsync(id)).Configuration;
                var executor = services.GetRequiredService<SessionToolExecutor>();
                var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: id, OwnerTurnText: "Keep this concise review procedure.");
                var patch = JsonSerializer.Serialize(new { expectedInstanceRevision = view.InstanceRevision, section = "conversationPolicy", set = new { responseLength = "concise" }, clear = Array.Empty<string>() });
                var saved = await executor.ExecuteAsync(admitted.Definition, Guid.NewGuid(), new("change", "settings.patch", patch), 100000, admission: admission);
                Assert.Contains("\"outcome\":\"applied\"", saved.Text);
                var settings = await services.GetRequiredService<AgentInstanceSettingsService>().ReadAsync(id, "conversationPolicy");
                Assert.Equal("concise", settings.Effective["responseLength"]);
                Assert.Equal("instance", settings.Sources["responseLength"]);
                Assert.Equal("balanced", admitted.Definition.ConversationPolicy.ResponseLength);
                var stale = await executor.ExecuteAsync(admitted.Definition, Guid.NewGuid(), new("retry", "settings.patch", patch), 100000, admission: admission);
                Assert.DoesNotContain("\"outcome\":\"applied\"", stale.Text);
                Assert.Equal("concise", (await resolver.ResolveAsync(id)).Configuration.Definition.ConversationPolicy.ResponseLength);
                view = await policy.ReadAsync(id);
                var resource = JsonSerializer.Serialize(new { expectedInstanceRevision = view.InstanceRevision, logicalPath = "reviews/checklist.md", kind = "Knowledge", mediaType = "text/markdown", content = "Check the observed totals before answering.", source = "conversation:user" });
                var retained = await executor.ExecuteAsync(admitted.Definition, Guid.NewGuid(), new("resource", "resources.upsert", resource), 100000, admission: admission);
                Assert.DoesNotContain("\"error\"", retained.Text);
                var catalog = await services.GetRequiredService<AgentInstanceResourceService>().ListAsync(id);
                var local = Assert.Single(catalog.Resources, r => r.Origin == "Instance");
                Assert.Equal("reviews/checklist.md", local.LogicalPath);
                Assert.Equal(64, local.ContentSha256.Length);
                Assert.DoesNotContain(admitted.Resources, r => r.Key == local.Key);
                Assert.Contains((await resolver.ResolveAsync(id)).Configuration.Resources, r => r.Key == local.Key);
                var frozen = await policy.FreezeAsync(id, catalog.InstanceRevision);
                var denied = await executor.ExecuteAsync(admitted.Definition, Guid.NewGuid(), new("blocked", "settings.patch", patch), 100000, admission: admission);
                Assert.Contains("forbidden", denied.Text);
                Assert.True(frozen.Effective.Frozen);
                Assert.Equal("concise", (await services.GetRequiredService<AgentInstanceSettingsService>().ReadAsync(id, "conversationPolicy")).Effective["responseLength"]);
            }
            if (sqlite)
            {
                await using var reopened = new HarnessSqliteFactory(root);
                Assert.True((await reopened.Services.GetRequiredService<SelfManagementService>().ReadAsync(id)).Effective.Frozen);
                Assert.Equal("concise", (await reopened.Services.GetRequiredService<AgentInstanceSettingsService>().ReadAsync(id, "conversationPolicy")).Effective["responseLength"]);
                Assert.Contains((await reopened.Services.GetRequiredService<AgentInstanceResourceService>().ListAsync(id)).Resources, r => r.LogicalPath == "reviews/checklist.md");
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("memoryPolicy", "userRetrieval", false)]
    [InlineData("triggerPolicy", "enabled", false)]
    [InlineData("conversationPolicy", "maxOutputTokens", 2048)]
    [InlineData("capabilities", "selected", "workspace.read")]
    public async Task Managed_delegation_never_bypasses_sensitive_exact_approval(string section, string field, object value)
    {
        await using var factory = new AgentCoreApiFactory();
        var services = factory.Services;
        var owner = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 23);
        var view = await services.GetRequiredService<SelfManagementService>().UpdateAsync(owner.InstanceId, owner.Revision,
            new(new(HarnessManagementMode.Managed), new([HarnessManagementScope.Settings]), new(Enum.GetValues<ManagementAutoApply>())));
        var definition = (await services.GetRequiredService<AgentRunConfigurationResolver>().ResolveAsync(owner.InstanceId)).Configuration.Definition;
        var args = JsonSerializer.SerializeToElement(new { expectedInstanceRevision = view.InstanceRevision, section, set = new Dictionary<string, object> { [field] = section == "capabilities" ? new[] { value } : value }, clear = Array.Empty<string>() });
        var executor = services.GetRequiredService<SessionToolExecutor>();
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: owner.InstanceId);
        Assert.Equal(ToolPolicyDecision.RequireApproval, await executor.EvaluateExecutionPolicyAsync(definition, Guid.NewGuid(), new("change", "settings.patch", args.GetRawText()), args, admission, default));
        var result = await executor.ExecuteAsync(definition, Guid.NewGuid(), new("change", "settings.patch", args.GetRawText()), 100000, admission: admission);
        Assert.Contains("approval_required", result.Text);
        Assert.Equal(view.InstanceRevision, (await services.GetRequiredService<SelfManagementService>().ReadAsync(owner.InstanceId)).InstanceRevision);
    }

    [Fact]
    public async Task Management_projection_is_lazy_builtin_procedures_are_read_only_and_off_discovery_is_silent()
    {
        await using var factory = new AgentCoreApiFactory();
        var services = factory.Services;
        var owner = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 23);
        var policy = services.GetRequiredService<SelfManagementService>();
        var view = await policy.UpdateAsync(owner.InstanceId, owner.Revision, new(new(HarnessManagementMode.Managed),
            new([HarnessManagementScope.Settings, HarnessManagementScope.Skills, HarnessManagementScope.Instructions]), new([])));
        var resolved = await services.GetRequiredService<AgentRunConfigurationResolver>().ResolveAsync(owner.InstanceId);
        var executor = services.GetRequiredService<SessionToolExecutor>();
        var context = new AgentContext(resolved.Configuration.Definition, [], "", null, AgentCore.Domain.Conversation.SessionMode.Text, null, false, null,
            new(Guid.NewGuid(), TriggerKind.UserTurn, "Hello"), AgentInstanceId: owner.InstanceId,
            Harness: await executor.HarnessContextAsync(owner.InstanceId, default), PinnedSkillCatalog: resolved.Skills);
        var prompt = new PromptContextBuilder().Build(context, Guid.NewGuid());
        Assert.DoesNotContain(new PromptContextBuilder().OfferTools(context.Definition, context), t => ManagementTools.IsManagement(t.Name));
        var system = string.Join('\n', prompt.Messages.Where(m => m.Role == ModelRole.System).Select(m => m.Text));
        Assert.DoesNotContain("configuration.inspect", system);
        Assert.DoesNotContain("Configuration assistance", system);
        Assert.Contains(resolved.Skills, s => s.Key == ManagementBuiltInSkills.ConfigurationKey);
        var builtin = (await services.GetRequiredService<AgentInstanceSkillService>().ListAsync(owner.InstanceId)).Single(s => s.Key == ManagementBuiltInSkills.ConfigurationKey);
        Assert.Null(builtin.DefinitionVersion);
        Assert.Null(builtin.SourceDefinitionId);
        await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(async () => await services.GetRequiredService<AgentInstanceSkillService>().WriteAsync(owner.InstanceId, "delete", builtin.Key, builtin.Revision));
        using var query = JsonDocument.Parse("{\"query\":\"configuration.inspect\"}");
        Assert.Contains("configuration.inspect", CapabilityDiscoveryMatcher.Load(context.Definition, context, ToolConfigurationGates.Unconfigured, query.RootElement, 0).Loaded);
        await policy.FreezeAsync(owner.InstanceId, view.InstanceRevision);
        context = context with { Harness = null };
        var unavailable = CapabilityDiscoveryMatcher.Load(context.Definition, context, ToolConfigurationGates.Unconfigured, query.RootElement, 0);
        Assert.Empty(unavailable.Loaded); Assert.Empty(unavailable.Unavailable);
        prompt = new PromptContextBuilder().Build(context, Guid.NewGuid());
        Assert.DoesNotContain(new PromptContextBuilder().OfferTools(context.Definition, context), t => ManagementTools.IsManagement(t.Name));
        Assert.DoesNotContain("configuration.inspect", string.Join('\n', prompt.Messages.Select(m => m.Text)));
    }
}
