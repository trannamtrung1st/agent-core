using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Domain.Security;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
namespace AgentCore.Api.Tests;
public sealed partial class TenantSessionAccessTests
{
    [Theory]
    [InlineData(false,"concise")] [InlineData(true,"concise")]
    [InlineData(false,"unsupported_goal")] [InlineData(true,"unsupported_goal")]
    [InlineData(false,"unknown_target")] [InlineData(true,"unknown_target")]
    [InlineData(false,"no_settings_scope")] [InlineData(true,"no_settings_scope")]
    [InlineData(false,"host_setup")] [InlineData(true,"host_setup")]
    [InlineData(false,"instance_selection")] [InlineData(true,"instance_selection")]
    [InlineData(false,"definition_limit")] [InlineData(true,"definition_limit")]
    public async Task Canonical_management_inspection_diagnoses_exact_configuration_remedies_without_claiming_execution_authority_or_broad_inventories(bool sqlite,string scenario)
    {
        var root=Path.Combine(Path.GetTempPath(),"management-inspection-diagnosis-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var model=new ManagementReadModel("configuration.inspect");
            await using WebApplicationFactory<Program> factory=sqlite?new TenantApprovalSqliteFactory(root,model):new ApprovalExecutionFactory();
            await using var host=factory.WithWebHostBuilder(b=>b.ConfigureTestServices(s=> {
                s.Replace(ServiceDescriptor.Singleton<ILanguageModel>(model));
                if(scenario=="host_setup")s.Replace(ServiceDescriptor.Singleton<IToolConfigurationGate>(new AgentCore.Infrastructure.Tools.ToolConfigurationGate(null,null)));
            }));
            using var owner=host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress=new("https://localhost"),AllowAutoRedirect=false });
            var (tenant,instance)=await SetupManagementInspection(host,owner);var api=$"/api/v2/tenants/{tenant}";var revision=6;
            model.ArgumentsOverride=JsonSerializer.Serialize(new { goal="Use concise responses" });
            if(scenario=="unsupported_goal")model.ArgumentsOverride="""{"goal":"Solve an unspecified configuration mystery"}""";
            if(scenario=="unknown_target")model.ArgumentsOverride="""{"target":"unknown.private.operation"}""";
            if(scenario=="host_setup")model.ArgumentsOverride="""{"target":"browser.navigate"}""";
            if(scenario=="no_settings_scope")
            {
                Assert.Equal(HttpStatusCode.OK,(await owner.PutAsJsonAsync(api+$"/agents/{instance}/self-management",new { expectedRevision=await Revision(owner,tenant),expectedInstanceRevision=6,
                    overrides=new { mode=new { value="Managed" },scopes=new { value=new[] { "Instructions" } } } })).StatusCode);revision++;
            }
            if(scenario=="instance_selection")
            {
                var view=(await Json(await owner.GetAsync(api+$"/agents/{instance}/settings"))).GetProperty("items").EnumerateArray().Single(s=>s.GetProperty("section").GetString()=="capabilities");
                var selected=view.GetProperty("effective").GetProperty("selected").EnumerateArray().Select(v=>v.GetString()!).Where(v=>v!=ToolCatalog.WebFetch).ToArray();
                Assert.Equal(HttpStatusCode.OK,(await owner.PatchAsJsonAsync(api+$"/agents/{instance}/settings/capabilities",new { expectedRevision=await Revision(owner,tenant),expectedInstanceRevision=6,set=new { selected },clear=Array.Empty<string>() })).StatusCode);revision++;
                model.ArgumentsOverride=JsonSerializer.Serialize(new { target=ToolCatalog.WebFetch });
            }
            if(scenario=="definition_limit")
            {
                var draft=await Json(await owner.PostAsJsonAsync(api+"/agent-definitions/from-template",new { templateId="general-assistant",version=35,expectedRevision=await Revision(owner,tenant) }));
                var draftId=draft.GetProperty("draftId").GetGuid();var definition=draft.GetProperty("definitionId").GetGuid();var candidate=JsonNode.Parse(draft.GetProperty("candidate").GetRawText())!;
                candidate["executionPolicy"]!["capabilities"]=JsonSerializer.SerializeToNode(new { mode="Selected",resolvedCapabilities=new[] { "configuration.inspect","settings.patch" } });
                candidate["executionPolicy"]!["projection"]=JsonSerializer.SerializeToNode(new { alwaysCapabilities=Array.Empty<string>() });
                candidate["skills"]=JsonSerializer.SerializeToNode(Array.Empty<object>());
                Assert.Equal(HttpStatusCode.OK,(await owner.PutAsJsonAsync(api+$"/definition-drafts/{draftId}",new { candidate,expectedRevision=await Revision(owner,tenant),expectedDraftRevision=1 })).StatusCode);
                var published=await owner.PostAsJsonAsync(api+$"/definition-drafts/{draftId}/publish",new { expectedRevision=await Revision(owner,tenant),expectedDraftRevision=2 });
                Assert.True(published.StatusCode==HttpStatusCode.OK,await published.Content.ReadAsStringAsync());
                instance=(await Json(await owner.PostAsJsonAsync(api+"/agents/from-publication",new { definitionId=definition,version=1,expectedRevision=await Revision(owner,tenant) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
                Assert.Equal(HttpStatusCode.OK,(await owner.PutAsJsonAsync(api+$"/agents/{instance}/self-management",new { expectedRevision=await Revision(owner,tenant),expectedInstanceRevision=1,
                    overrides=new { mode=new { value="Managed" },scopes=new { value=new[] { "Settings" } } } })).StatusCode);revision=2;
                var state=(await host.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!;var targetAgent=(await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant,instance))!;
                await host.Services.GetRequiredService<TenantAccessService>().AssignRoleAsync(state.Users.Single().PrincipalId,tenant,targetAgent.PrincipalId,state.Roles.Single(r=>r.Name=="Exact management reader").RoleId,AccessScope.ForResource(tenant,"agent",instance.ToString("D")),state.Tenant.Revision);
                model.ArgumentsOverride=JsonSerializer.Serialize(new { target=ToolCatalog.WebFetch });
            }
            var session=(await Json(await owner.PostAsJsonAsync(api+"/sessions",new { retentionKind="saved", agentInstanceId=instance,expectedRevision=await Revision(owner,tenant),expectedInstanceRevision=revision }))).GetProperty("sessionId").GetGuid();
            var submitted=await owner.PostAsJsonAsync(api+$"/sessions/{session}/messages",new { messageId=Guid.NewGuid(),text="Diagnose only this exact requested configuration change.",expectedRevision=await Revision(owner,tenant),expectedSessionRevision=1 });
            Assert.Equal(HttpStatusCode.Accepted,submitted.StatusCode);var runId=(await Json(submitted)).GetProperty("runId").GetGuid();if(factory is ApprovalExecutionFactory memory)Volatile.Write(ref memory.Enabled,true);
            var final=await AwaitManagementRun(host.Services.GetRequiredService<ITenantRunStore>(),tenant,runId);Assert.True(final.Status==AgentRunStatus.Completed,$"{final.Status}: {final.Failure?.Code}: {final.Failure?.Summary}");
            var message=model.Requests.SelectMany(r=>r.Messages).Last(m=>m.Name=="configuration.inspect");using var result=JsonDocument.Parse(message.Text);
            Assert.True(result.RootElement.TryGetProperty("observation",out var observed),message.Text);var data=observed.GetProperty("data");
            Assert.Empty(data.GetProperty("skills").EnumerateArray());Assert.Empty(data.GetProperty("resources").EnumerateArray());Assert.Empty(data.GetProperty("eligibleCredentials").EnumerateArray());
            var expected=scenario switch { "concise"=>"configuration_available","unsupported_goal"=>"unsupported_goal","unknown_target"=>"unsupported_target","no_settings_scope"=>"management_scope_required","host_setup"=>"host_setup_required","instance_selection"=>"instance_selection",_=>"definition_limit" };
            Assert.Equal(expected,data.GetProperty("blocker").GetProperty("reason").GetString());Assert.Contains($"/t/{tenant}/admin/agents/{instance}/settings",data.GetProperty("blocker").GetProperty("adminPath").GetString());
            var remedies=data.GetProperty("remediationOptions").EnumerateArray().ToArray();
            if(scenario is "concise" or "instance_selection")
            {
                var remedy=Assert.Single(remedies);Assert.Equal("settings.patch",remedy.GetProperty("operation").GetString());Assert.Equal(revision,remedy.GetProperty("expectedInstanceRevision").GetInt64());Assert.Equal(64,remedy.GetProperty("option").GetString()!.Length);
                if(scenario=="concise")Assert.Equal("concise",remedy.GetProperty("set").GetProperty("responseLength").GetString());
                else Assert.Contains(ToolCatalog.WebFetch,remedy.GetProperty("set").GetProperty("selected").EnumerateArray().Select(v=>v.GetString()));
            }
            else Assert.Empty(remedies);
            if(scenario is "unsupported_goal" or "unknown_target" or "no_settings_scope")Assert.Empty(data.GetProperty("settings").EnumerateArray());
            Assert.Equal(revision,(await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant,instance))!.Instance.Revision);
            var inspection=Assert.Single(AssertManagementPreludeAndGetInspections(final,scenario!="definition_limit"));
            Assert.Equal("configuration.inspect",inspection.ToolName);Assert.Empty(TenantManagementInspectionSources.Read(inspection.SourcesJson));
        }
        finally { SqliteConnection.ClearAllPools();Directory.Delete(root,true); }
    }
}
