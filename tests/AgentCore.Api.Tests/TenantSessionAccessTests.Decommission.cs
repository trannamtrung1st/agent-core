using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Browser;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private static WebApplicationFactory<Program> DecommissionFactory(string root,bool sqlite)
    {
        WebApplicationFactory<Program> original=sqlite?new TenantApprovalSqliteFactory(root,new ApprovalRecordingModel()):new AgentCoreApiFactory();
        return original.WithWebHostBuilder(builder=>builder.ConfigureTestServices(services=>{
            var persistence=(PersistenceOptions)services.Last(d=>d.ServiceType==typeof(PersistenceOptions)).ImplementationInstance!;
            persistence.WorkspaceRoot=Path.Combine(root,"workspaces");persistence.ArtifactRoot=Path.Combine(root,"artifacts");persistence.AttachmentRoot=Path.Combine(root,"attachments");persistence.DefinitionResourceRoot=Path.Combine(root,"resources");persistence.CredentialProtectionKeyRoot=Path.Combine(root,"keys");
            var browser=(BrowserOptions)services.Single(d=>d.ServiceType==typeof(BrowserOptions)).ImplementationInstance!;browser.Enabled=false;browser.FixtureEnabled=false;browser.ProfileRoot=Path.Combine(root,"profiles");
        }));
    }
    private static Task<HttpResponseMessage> DecommissionDelete(HttpClient client,string path,object body)=>client.SendAsync(new HttpRequestMessage(HttpMethod.Delete,path){Content=JsonContent.Create(body)});

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Agent_decommission_HTTP_requires_current_archived_owner_and_retires_only_exact_data(bool sqlite)
    {
        var root=Path.Combine(Path.GetTempPath(),"tenant-decommission-http-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try {
            await using var factory=DecommissionFactory(root,sqlite);using var owner=factory.CreateClient(new WebApplicationFactoryClientOptions{BaseAddress=new("https://localhost")});await Login(owner,"owner@example.test","explicit-synthetic-test-password");
            var tenant=(await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();var api=$"/api/v2/tenants/{tenant}";
            var created=await Json(await owner.PostAsJsonAsync(api+"/agents",new{templateId="examiner",version=1,expectedRevision=await Revision(owner,tenant)}));var id=created.GetProperty("instance").GetProperty("instanceId").GetGuid();var path=api+$"/agents/{id}";
            var preflight=await Json(await owner.GetAsync(path+"/decommission"));Assert.False(preflight.GetProperty("canDecommission").GetBoolean());Assert.Equal("archive_required",preflight.GetProperty("blocker").GetString());
            Assert.Equal(HttpStatusCode.BadRequest,(await DecommissionDelete(owner,path,new{expectedRevision=await Revision(owner,tenant),expectedInstanceRevision=1,operationId=Guid.NewGuid(),actorPrincipalId=Guid.NewGuid()})).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict,(await DecommissionDelete(owner,path,new{expectedRevision=await Revision(owner,tenant),expectedInstanceRevision=1,operationId=Guid.NewGuid()})).StatusCode);
            var archived=await owner.PutAsJsonAsync(path+"/lifecycle",new{lifecycle=1,expectedRevision=await Revision(owner,tenant),expectedInstanceRevision=1});archived.EnsureSuccessStatusCode();
            preflight=await Json(await owner.GetAsync(path+"/decommission"));Assert.True(preflight.GetProperty("canDecommission").GetBoolean());Assert.True(preflight.GetProperty("canUpdate").GetBoolean());Assert.Equal((await Json(await owner.GetAsync(api+"/access/me"))).GetProperty("tenant").GetProperty("name").GetString(),preflight.GetProperty("organizationName").GetString());
            using var anonymous=factory.CreateClient(new WebApplicationFactoryClientOptions{BaseAddress=new("https://localhost")});Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.GetAsync(path+"/decommission")).StatusCode);
            var profile=Path.Combine(root,"profiles","tenants",tenant.ToString("D"),"agents",id.ToString("D"));Directory.CreateDirectory(profile);await File.WriteAllTextAsync(Path.Combine(profile,"sentinel"),"private profile");
            var home=AgentWorkspacePhysicalPaths.HomeDirectory(Path.Combine(root,"workspaces"),id);Directory.CreateDirectory(home);await File.WriteAllTextAsync(Path.Combine(home,"sentinel"),"private home");
            var sibling=Path.Combine(root,"profiles","tenants",Guid.NewGuid().ToString("D"),"agents",Guid.NewGuid().ToString("D"));Directory.CreateDirectory(sibling);await File.WriteAllTextAsync(Path.Combine(sibling,"sentinel"),"preserve sibling");
            var operation=Guid.NewGuid();var command=new{expectedRevision=preflight.GetProperty("securityRevision").GetInt64(),expectedInstanceRevision=preflight.GetProperty("instanceRevision").GetInt64(),operationId=operation};
            var response=await DecommissionDelete(owner,path,command);response.EnsureSuccessStatusCode();var result=await Json(response);Assert.Equal("decommissioned",result.GetProperty("status").GetString());Assert.Equal("completed",result.GetProperty("cleanupStatus").GetString());Assert.False(Directory.Exists(profile));Assert.False(Directory.Exists(home));Assert.True(File.Exists(Path.Combine(sibling,"sentinel")));
            Assert.Equal(0,(await Json(await owner.GetAsync(api+"/agents"))).GetProperty("total").GetInt32());Assert.Equal(HttpStatusCode.Forbidden,(await owner.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,(await owner.PutAsJsonAsync(path+"/lifecycle",new{lifecycle=0,expectedRevision=await Revision(owner,tenant),expectedInstanceRevision=3})).StatusCode);
            var replay=await DecommissionDelete(owner,path,command);replay.EnsureSuccessStatusCode();Assert.Equal(operation,(await Json(replay)).GetProperty("operationId").GetGuid());
            var saved=await Json(await owner.GetAsync(path+"/decommission"));Assert.True(saved.GetProperty("decommissioned").GetBoolean());Assert.False(saved.GetProperty("canDecommission").GetBoolean());Assert.False(saved.GetProperty("canUpdate").GetBoolean());
            var audit=await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant,200);Assert.Single(audit,a=>a.Action=="agents.decommission"&&a.AuditId==operation);
        }finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
    }

    [Fact]
    public async Task Tenant_Agent_decommission_SQLite_host_restart_recovers_committed_unknown_profile_cleanup()
    {
        var root=Path.Combine(Path.GetTempPath(),"tenant-decommission-restart-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);Guid tenant=default,id=default,operation=Guid.NewGuid();
        try {
            await using(var first=DecommissionFactory(root,true)) {
                using var owner=first.CreateClient(new WebApplicationFactoryClientOptions{BaseAddress=new("https://localhost")});await Login(owner,"owner@example.test","explicit-synthetic-test-password");tenant=(await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();var api=$"/api/v2/tenants/{tenant}";
                id=(await Json(await owner.PostAsJsonAsync(api+"/agents",new{templateId="examiner",version=1,expectedRevision=await Revision(owner,tenant)}))).GetProperty("instance").GetProperty("instanceId").GetGuid();var path=api+$"/agents/{id}";
                (await owner.PutAsJsonAsync(path+"/lifecycle",new{lifecycle=1,expectedRevision=await Revision(owner,tenant),expectedInstanceRevision=1})).EnsureSuccessStatusCode();
                var profile=Path.Combine(root,"profiles","tenants",tenant.ToString("D"),"agents",id.ToString("D"));Directory.CreateDirectory(profile);await File.WriteAllTextAsync(Path.Combine(profile,"sentinel"),"private bytes");
                var native=(NativePlaywrightBrowser)first.Services.GetRequiredService<IBrowser>();native.TenantProfileDeleteProbe=_=>throw new IOException("controlled private cleanup failure");var before=await Json(await owner.GetAsync(path+"/decommission"));
                var result=await Json(await DecommissionDelete(owner,path,new{expectedRevision=before.GetProperty("securityRevision").GetInt64(),expectedInstanceRevision=2,operationId=operation}));Assert.Equal("pending",result.GetProperty("cleanupStatus").GetString());Assert.True(Directory.Exists(profile));
            }
            await using(var second=DecommissionFactory(root,true)) {
                using var owner=second.CreateClient(new WebApplicationFactoryClientOptions{BaseAddress=new("https://localhost")});await Login(owner,"owner@example.test","explicit-synthetic-test-password");
                await second.Services.GetRequiredService<AgentCore.Application.Security.TenantAgentDecommissionCleanup>().RecoverAsync();
                Assert.False(Directory.Exists(Path.Combine(root,"profiles","tenants",tenant.ToString("D"),"agents",id.ToString("D"))));var saved=await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/agents/{id}/decommission"));Assert.True(saved.GetProperty("decommissioned").GetBoolean());Assert.Equal(operation,saved.GetProperty("operationId").GetGuid());
                Assert.Single(await second.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant,200),a=>a.Action=="agents.decommission");
            }
        }finally{SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
    }
}
