using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
namespace AgentCore.Api.Tests;
public sealed partial class TenantSessionAccessTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Canonical_management_inspection_filters_compound_human_source_rights_before_inbox_and_conversation_counts(bool sqlite)
    {
        var root=Path.Combine(Path.GetTempPath(),"management-inspection-visibility-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var model=new ManagementReadModel("configuration.inspect");
            await using WebApplicationFactory<Program> factory=sqlite?new TenantApprovalSqliteFactory(root,model):new ApprovalExecutionFactory();
            await using var host=factory.WithWebHostBuilder(b=>b.ConfigureTestServices(s=>s.Replace(ServiceDescriptor.Singleton<ILanguageModel>(model))));
            using var owner=host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress=new("https://localhost"),AllowAutoRedirect=false });
            var (tenant,instance)=await SetupManagementInspection(host,owner);var api=$"/api/v2/tenants/{tenant}";
            Assert.Equal(HttpStatusCode.OK,(await owner.PutAsJsonAsync(api+"/operation-policies/skills.read",new { mode="always",independentReview=false,expectedRevision=await Revision(owner,tenant),expectedPolicyRevision=0 })).StatusCode);
            var session=(await Json(await owner.PostAsJsonAsync(api+"/sessions",new { agentInstanceId=instance,expectedRevision=await Revision(owner,tenant),expectedInstanceRevision=6 }))).GetProperty("sessionId").GetGuid();
            var submitted=await owner.PostAsJsonAsync(api+$"/sessions/{session}/messages",new { messageId=Guid.NewGuid(),text="Review this exact compound metadata observation.",expectedRevision=await Revision(owner,tenant),expectedSessionRevision=1 });
            Assert.Equal(HttpStatusCode.Accepted,submitted.StatusCode);var runId=(await Json(submitted)).GetProperty("runId").GetGuid();if(factory is ApprovalExecutionFactory memory)Volatile.Write(ref memory.Enabled,true);
            var pending=await AwaitManagementRun(host.Services.GetRequiredService<ITenantRunStore>(),tenant,runId,true);Assert.Equal(AgentRunStatus.WaitingForApproval,pending.Status);
            var request=(await host.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant,pending.ApprovalRequestId!.Value))!;
            var sources=TenantManagementInspectionSources.Read(request.Operation.ManagementRead!.SourcesJson);Assert.Contains(sources,s=>s.Kind=="skill");Assert.Contains(sources,s=>s.Kind=="resource");Assert.Contains(sources,s=>s.Kind=="credential");
            var role=await Json(await owner.PostAsJsonAsync(api+"/roles",new { name="Compound status observer",permissions=new[] { "approvals.read","agents.read","sessions.read","agents.chat" },expectedRevision=await Revision(owner,tenant) }));
            var invitation=await Json(await owner.PostAsJsonAsync(api+"/members/invite",new { login="compound-observer@example.test",roleId=role.GetProperty("roleId").GetGuid(),expectedRevision=await Revision(owner,tenant) }));
            using var observer=host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress=new("https://localhost"),AllowAutoRedirect=false });await Csrf(observer);
            Assert.Equal(HttpStatusCode.Created,(await observer.PostAsJsonAsync("/api/v2/auth/register",new { login="compound-observer@example.test",displayName="Compound observer",password="strong-compound-observer-password",invitation=new { tenantId=tenant,invitationId=invitation.GetProperty("invitationId").GetGuid(),token=invitation.GetProperty("token").GetString() } })).StatusCode);
            await Login(observer,"compound-observer@example.test","strong-compound-observer-password");
            var members=await Json(await owner.GetAsync(api+"/members"));var principal=members.GetProperty("items").EnumerateArray().Single(m=>m.GetProperty("login").GetString()=="COMPOUND-OBSERVER@EXAMPLE.TEST").GetProperty("principalId").GetGuid();
            async Task Hidden()
            {
                Assert.Equal(0,(await Json(await observer.GetAsync(api+"/approvals?pageSize=1"))).GetProperty("total").GetInt32());
                Assert.Equal(0,(await Json(await observer.GetAsync(api+$"/sessions/{session}/approvals?pageSize=1"))).GetProperty("total").GetInt32());
                Assert.Equal(HttpStatusCode.Forbidden,(await observer.GetAsync(api+$"/approvals/{request.RequestId}")).StatusCode);
            }
            await Hidden();
            var roles=new Dictionary<string,Guid>();
            var targets=sources.Select(s=>(Permission:TenantManagementInspectionSources.Permission(s.Kind),Type:s.Kind is "credential" or "binding"?"credential":s.Kind,Id:s.ResourceId)).Distinct().ToArray();
            for(var i=0;i<targets.Length;i++)
            {
                var target=targets[i];
                if(!roles.TryGetValue(target.Permission,out var roleId))
                {
                    roleId=(await Json(await owner.PostAsJsonAsync(api+"/roles",new { name="Exact compound "+target.Permission,permissions=new[] { target.Permission },expectedRevision=await Revision(owner,tenant) }))).GetProperty("roleId").GetGuid();roles.Add(target.Permission,roleId);
                }
                Assert.Equal(HttpStatusCode.Created,(await owner.PostAsJsonAsync(api+"/role-assignments",new { principalId=principal,roleId,resourceType=target.Type,resourceId=target.Id,expectedRevision=await Revision(owner,tenant) })).StatusCode);
                if(i<targets.Length-1)await Hidden();
            }
            Assert.Equal(1,(await Json(await observer.GetAsync(api+"/approvals?pageSize=1"))).GetProperty("total").GetInt32());
            Assert.Equal(1,(await Json(await observer.GetAsync(api+$"/sessions/{session}/approvals?pageSize=1"))).GetProperty("total").GetInt32());
            var detail=await Json(await observer.GetAsync(api+$"/approvals/{request.RequestId}"));Assert.False(detail.GetProperty("review").GetProperty("canDecide").GetBoolean());
            Assert.True((await Json(await owner.GetAsync(api+$"/approvals/{request.RequestId}"))).GetProperty("review").GetProperty("canDecide").GetBoolean());
        }
        finally { SqliteConnection.ClearAllPools();Directory.Delete(root,true); }
    }
}
