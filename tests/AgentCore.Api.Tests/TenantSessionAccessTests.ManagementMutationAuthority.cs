using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
namespace AgentCore.Api.Tests;
public sealed partial class TenantSessionAccessTests
{
    [Theory]
    [InlineData(false,"stale")] [InlineData(true,"stale")]
    [InlineData(false,"read_missing")] [InlineData(true,"read_missing")]
    [InlineData(false,"forced_without_read")] [InlineData(true,"forced_without_read")]
    [InlineData(false,"scope_missing")] [InlineData(true,"scope_missing")]
    [InlineData(false,"independent_without_reviewer")] [InlineData(true,"independent_without_reviewer")]
    [InlineData(false,"cross_section")] [InlineData(true,"cross_section")]
    public async Task Management_commands_with_missing_authority_stale_or_wider_fields_create_no_effect_or_grant(bool sqlite,string boundary)
    {
        var root=Path.Combine(Path.GetTempPath(),"management-mutation-authority-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var model=new ManagementMutationModel("settings.patch") { Arguments=ManagementCommand("settings.patch",""),ForceConsent=boundary=="forced_without_read" };
            await using WebApplicationFactory<Program> factory=sqlite?new TenantApprovalSqliteFactory(root,model):new ApprovalExecutionFactory();
            await using var host=factory.WithWebHostBuilder(b=>b.ConfigureTestServices(s=>s.Replace(ServiceDescriptor.Singleton<ILanguageModel>(model))));
            using var owner=host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress=new("https://localhost"),AllowAutoRedirect=false });
            var (tenant,instance)=await SetupManagementMutation(host,owner);var api=$"/api/v2/tenants/{tenant}";var revision=3;
            if(boundary=="stale") model.Arguments=ManagementCommand("settings.patch","").Replace("\"expectedInstanceRevision\":3","\"expectedInstanceRevision\":2");
            if(boundary=="cross_section") model.Arguments="""{"expectedInstanceRevision":3,"section":"instructions","set":{"language":"vi"},"clear":[]}""";
            if(boundary is "read_missing" or "forced_without_read")
            {
                var state=(await host.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!;var role=state.Roles.Single(r=>r.Name=="Exact management reader");
                await host.Services.GetRequiredService<TenantAccessService>().SaveRoleAsync(Assert.Single(state.Users).PrincipalId,tenant,role.RoleId,role.Name,["agents.update"],state.Tenant.Revision);
            }
            if(boundary=="scope_missing")
            {
                Assert.Equal(HttpStatusCode.OK,(await owner.PutAsJsonAsync(api+$"/agents/{instance}/self-management",new { expectedRevision=await Revision(owner,tenant),expectedInstanceRevision=3,overrides=new { mode=new { value="Managed" },scopes=new { value=Array.Empty<string>() } } })).StatusCode);
                revision=4;model.Arguments=ManagementCommand("settings.patch","").Replace("\"expectedInstanceRevision\":3","\"expectedInstanceRevision\":4");
            }
            if(boundary=="independent_without_reviewer") Assert.Equal(HttpStatusCode.OK,(await owner.PutAsJsonAsync(api+"/operation-policies/agents.update",new { mode="always",independentReview=true,expectedRevision=await Revision(owner,tenant),expectedPolicyRevision=0 })).StatusCode);
            var created=await Json(await owner.PostAsJsonAsync(api+"/sessions",new { retentionKind="saved", agentInstanceId=instance,expectedRevision=await Revision(owner,tenant),expectedInstanceRevision=revision }));var session=created.GetProperty("sessionId").GetGuid();
            var response=await owner.PostAsJsonAsync(api+$"/sessions/{session}/messages",new { messageId=Guid.NewGuid(),text="Attempt an exact configuration change through its actual current authority.",expectedRevision=await Revision(owner,tenant),expectedSessionRevision=1 });
            Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);var runId=(await Json(response)).GetProperty("runId").GetGuid();if(factory is ApprovalExecutionFactory memory)Volatile.Write(ref memory.Enabled,true);
            var final=await AwaitManagementRun(host.Services.GetRequiredService<ITenantRunStore>(),tenant,runId);
            Assert.True(final.Status==AgentRunStatus.Completed,$"{final.Status}: {final.Failure?.Code}: {final.Failure?.Summary}");
            Assert.Equal(revision,(await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant,instance))!.Instance.Revision);
            Assert.Equal(0,(await Json(await owner.GetAsync(api+"/approvals?pageSize=1"))).GetProperty("total").GetInt32());
            if(boundary is "read_missing" or "forced_without_read" or "scope_missing") Assert.Empty(TenantManagementReadReferences.Read(final.Checkpoint));
            Assert.DoesNotContain(model.Requests.SelectMany(r=>r.Messages),m=>m.Text.Contains("\"applied\":true",StringComparison.Ordinal));
        }
        finally { SqliteConnection.ClearAllPools();Directory.Delete(root,true); }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Management_exact_exceptional_grant_supplies_only_the_primary_configuration_update_and_is_consumed_once(bool sqlite)
    {
        var root=Path.Combine(Path.GetTempPath(),"management-mutation-grant-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var model=new ManagementMutationModel("settings.patch") { Arguments=ManagementCommand("settings.patch","") };
            await using WebApplicationFactory<Program> factory=sqlite?new TenantApprovalSqliteFactory(root,model):new ApprovalExecutionFactory();
            await using var host=factory.WithWebHostBuilder(b=>b.ConfigureTestServices(s=>s.Replace(ServiceDescriptor.Singleton<ILanguageModel>(model))));
            using var owner=host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress=new("https://localhost"),AllowAutoRedirect=false });
            var (tenant,instance)=await SetupManagementMutation(host,owner);var api=$"/api/v2/tenants/{tenant}";
            var state=(await host.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!;var role=state.Roles.Single(r=>r.Name=="Exact management reader");
            await host.Services.GetRequiredService<TenantAccessService>().SaveRoleAsync(Assert.Single(state.Users).PrincipalId,tenant,role.RoleId,role.Name,["agents.read"],state.Tenant.Revision);
            var created=await Json(await owner.PostAsJsonAsync(api+"/sessions",new { retentionKind="saved", agentInstanceId=instance,expectedRevision=await Revision(owner,tenant),expectedInstanceRevision=3 }));var session=created.GetProperty("sessionId").GetGuid();
            var response=await owner.PostAsJsonAsync(api+$"/sessions/{session}/messages",new { messageId=Guid.NewGuid(),text="Request one exact configuration authorization without a standing write role.",expectedRevision=await Revision(owner,tenant),expectedSessionRevision=1 });
            Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);var runId=(await Json(response)).GetProperty("runId").GetGuid();if(factory is ApprovalExecutionFactory memory)Volatile.Write(ref memory.Enabled,true);
            var runs=host.Services.GetRequiredService<ITenantRunStore>();var pending=await AwaitManagementRun(runs,tenant,runId,true);Assert.Equal(AgentRunStatus.WaitingForApproval,pending.Status);
            var approvals=host.Services.GetRequiredService<ITenantApprovalStore>();var request=(await approvals.FindApprovalAsync(tenant,pending.ApprovalRequestId!.Value))!;Assert.Equal(ApprovalKind.ExceptionalGrant,request.Kind);
            Assert.Equal(HttpStatusCode.OK,(await owner.PostAsJsonAsync(api+$"/approvals/{request.RequestId}/decision",new { decision="approve",actionHash=request.Operation.ActionHash,expectedRevision=await Revision(owner,tenant),expectedRequestRevision=request.Revision })).StatusCode);
            AgentRun? final;using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while(true) { final=(await runs.ListTenantSessionRunsAsync(tenant,session,null,0,100,deadline.Token)).Items.SingleOrDefault(r=>r.ApprovalRequestId==request.RequestId&&r.AgentRunId!=runId);if(final?.IsTerminal==true)break;await Task.Delay(25,deadline.Token); }
            Assert.True(final.Status==AgentRunStatus.Completed,$"{final.Status}: {final.Failure?.Code}: {final.Failure?.Summary}");
            Assert.Equal(OperationGrantStatus.Consumed,(await approvals.FindOperationGrantAsync(tenant,request.RequestId))!.Status);
            var saved=(await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant,instance))!;Assert.Equal(4,saved.Instance.Revision);Assert.Equal(ManagementInstructions,saved.Instance.SettingsOverrides!.AgentInstructions!.Value);
            var check=await Json(await owner.PostAsJsonAsync(api+"/access/check",new { principalId=saved.PrincipalId,permission="agents.update",resourceType="agent",resourceId=instance }));Assert.False(check.GetProperty("allowed").GetBoolean());
            Assert.Single(TenantManagementReadReferences.Read(final.Checkpoint));
        }
        finally { SqliteConnection.ClearAllPools();Directory.Delete(root,true); }
    }
}
