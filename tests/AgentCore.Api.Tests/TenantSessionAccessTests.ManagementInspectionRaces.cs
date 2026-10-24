using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Contracts.Http;
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
    [InlineData(false,"skills.read")] [InlineData(true,"skills.read")]
    [InlineData(false,"resources.read")] [InlineData(true,"resources.read")]
    [InlineData(false,"credentials.read")] [InlineData(true,"credentials.read")]
    [InlineData(false,"credential_changed")] [InlineData(true,"credential_changed")]
    public async Task Canonical_management_inspection_rechecks_each_child_before_model_reentry_and_protected_output(bool sqlite,string boundary)
    {
        var root=Path.Combine(Path.GetTempPath(),"management-inspection-race-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var model=new ManagementReadModel("configuration.inspect");var race=new ManagementReadRace();
            await using WebApplicationFactory<Program> factory=sqlite?new TenantApprovalSqliteFactory(root,model):new ApprovalExecutionFactory();
            await using var host=factory.WithWebHostBuilder(b=>b.ConfigureTestServices(s=> {
                s.Replace(ServiceDescriptor.Singleton<ILanguageModel>(model));s.Replace(ServiceDescriptor.Singleton<ITenantManagementStore>(p=>new ManagementReadHook((ITenantManagementStore)p.GetRequiredService<IIamStore>(),race)));
            }));
            using var owner=host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress=new("https://localhost"),AllowAutoRedirect=false });
            var (tenant,instance)=await SetupManagementInspection(host,owner);var api=$"/api/v2/tenants/{tenant}";
            race.AfterObservation=async ()=> {
                if(boundary=="credential_changed")
                {
                    var binding=Assert.Single((await host.Services.GetRequiredService<ITenantCredentialBindingStore>().ReadTenantCredentialBindingsAsync(tenant,instance))!.Bindings);
                    var id=binding.Credential.Value.CredentialId;
                    var changed=await owner.PatchAsJsonAsync(api+$"/credentials/{id}",new TenantCredentialUpdateRequest("Changed authorized metadata","Active",await Revision(owner,tenant),binding.Credential.Value.Revision));
                    Assert.True(changed.StatusCode==HttpStatusCode.OK,await changed.Content.ReadAsStringAsync());
                }
                else
                {
                    var state=(await host.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!;var role=state.Roles.Single(r=>r.Name=="Compound management metadata reader");
                    await host.Services.GetRequiredService<TenantAccessService>().SaveRoleAsync(Assert.Single(state.Users).PrincipalId,tenant,role.RoleId,role.Name,
                        new[] { "skills.read","resources.read","credentials.read" }.Where(p=>p!=boundary).ToArray(),state.Tenant.Revision);
                }
            };
            var session=(await Json(await owner.PostAsJsonAsync(api+"/sessions",new { agentInstanceId=instance,expectedRevision=await Revision(owner,tenant),expectedInstanceRevision=6 }))).GetProperty("sessionId").GetGuid();
            var submitted=await owner.PostAsJsonAsync(api+$"/sessions/{session}/messages",new { messageId=Guid.NewGuid(),text="Read this exact compound snapshot before a child changes.",expectedRevision=await Revision(owner,tenant),expectedSessionRevision=1 });
            Assert.Equal(HttpStatusCode.Accepted,submitted.StatusCode);var runId=(await Json(submitted)).GetProperty("runId").GetGuid();if(factory is ApprovalExecutionFactory memory)Volatile.Write(ref memory.Enabled,true);
            var final=await AwaitManagementRun(host.Services.GetRequiredService<ITenantRunStore>(),tenant,runId);
            Assert.True(race.Applied==1,$"{final.Status}: {final.Failure?.Code}: {final.Failure?.Summary}");Assert.Equal(AgentRunStatus.Failed,final.Status);Assert.Equal(2,model.Requests.Count);Assert.Empty(AssertManagementPreludeAndGetInspections(final));
            Assert.DoesNotContain(model.Requests.SelectMany(r=>r.Messages),m=>m.Text.Contains("\"tenantManagementRead\":true",StringComparison.Ordinal));
            Assert.Equal(6,(await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant,instance))!.Instance.Revision);
        }
        finally { SqliteConnection.ClearAllPools();Directory.Delete(root,true); }
    }
}
