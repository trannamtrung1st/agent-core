using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers.Synthetic;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
namespace AgentCore.Api.Tests;
public sealed partial class TenantSessionAccessTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Space_snapshot_metadata_requires_its_exact_source_scope_and_does_not_disclose_unreadable_live_home_paths(bool sqlite)
    {
        var root=Path.Combine(Path.GetTempPath(),"space-source-metadata-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            await using WebApplicationFactory<Program> factory=sqlite?new NativeTenantSqliteFactory(root,new ScriptedLanguageModel()):new NativeTenantMemoryFactory(root,new ScriptedLanguageModel());
            using var owner=factory.CreateClient();await Login(owner,"owner@example.test","explicit-synthetic-test-password");
            var tenant=(await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();var api=$"/api/v2/tenants/{tenant}";
            var human=(await Json(await owner.GetAsync(api+"/access/me"))).GetProperty("principalId").GetGuid();var space=Guid.NewGuid();var resource=Guid.NewGuid();
            Assert.Equal(HttpStatusCode.Created,(await owner.PostAsJsonAsync(api+"/spaces",new{spaceId=space,name="Source metadata",description="",instructions="",expectedRevision=await Revision(owner,tenant)})).StatusCode);
            var files=new CanonicalWorkspaceFilesystem(AgentWorkspacePhysicalPaths.SpaceHomeDirectory(Path.Combine(root,"workspaces"),tenant,space),"/home");files.Ensure();
            await files.WriteAsync("/home/restricted-source-name.txt","Native source bytes"u8.ToArray(),1024,4096);
            var path=api+$"/spaces/{space}/resources/{resource}";
            Assert.Equal(HttpStatusCode.OK,(await owner.PutAsJsonAsync(path,new{sourcePath="/home/restricted-source-name.txt",logicalPath="shared-policy.txt",kind="Knowledge",expectedRevision=await Revision(owner,tenant),expectedSpaceRevision=1,expectedResourceRevision=0})).StatusCode);
            var identities=factory.Services.GetRequiredService<IdentityService>();var access=factory.Services.GetRequiredService<TenantAccessService>();
            var account=await identities.RegisterAsync("source-reader@example.test","Snapshot reader","strong-source-reader-password");
            var baseRole=await access.SaveRoleAsync(human,tenant,null,"Organization presence",["agents.chat"],await Revision(owner,tenant));
            var invitation=await access.InviteAsync(human,tenant,account.LoginKey,baseRole.RoleId,await Revision(owner,tenant));
            var user=await access.AcceptInvitationAsync(account.AccountId,tenant,invitation.Invitation.InvitationId,invitation.Token);
            var contextRole=await access.SaveRoleAsync(human,tenant,null,"One Space metadata",["spaces.read"],await Revision(owner,tenant));
            await access.AssignRoleAsync(human,tenant,user.PrincipalId,contextRole.RoleId,TenantSpaceService.Scope(tenant,space),await Revision(owner,tenant));
            using var reader=factory.CreateClient();await Login(reader,account.LoginKey,"strong-source-reader-password");
            Assert.Empty((await Json(await reader.GetAsync(api+$"/spaces/{space}/resources"))).GetProperty("items").EnumerateArray());
            Assert.Equal(HttpStatusCode.Forbidden,(await reader.GetAsync(path+"/access")).StatusCode);
            var sourceRole=await access.SaveRoleAsync(human,tenant,null,"One exact snapshot",["resources.read"],await Revision(owner,tenant));
            var scope=TenantResourceScopes.Source(tenant,Guid.Empty,AgentCore.Domain.Spaces.SpaceResourceKeys.Create(space,resource));
            var grant=await access.AssignRoleAsync(human,tenant,user.PrincipalId,sourceRole.RoleId,scope,await Revision(owner,tenant));
            var visible=await Json(await reader.GetAsync(path+"/access"));var value=visible.GetProperty("resource");
            Assert.Equal(resource,value.GetProperty("resourceId").GetGuid());Assert.False(value.TryGetProperty("sourcePath",out _));
            Assert.DoesNotContain("restricted-source-name",visible.GetRawText());Assert.DoesNotContain(visible.GetProperty("permissions").EnumerateArray(),p=>p.GetString()=="resources.manage");
            var page=await Json(await reader.GetAsync(api+$"/spaces/{space}/resources"));Assert.Single(page.GetProperty("items").EnumerateArray());Assert.DoesNotContain("restricted-source-name",page.GetRawText());
            Assert.Equal(HttpStatusCode.Forbidden,(await reader.GetAsync(api+$"/spaces/{space}/workspace")).StatusCode);
            await access.RevokeAssignmentAsync(human,tenant,grant.AssignmentId,await Revision(owner,tenant));
            Assert.Equal(HttpStatusCode.Forbidden,(await reader.GetAsync(path+"/access")).StatusCode);
            Assert.Empty((await Json(await reader.GetAsync(api+$"/spaces/{space}/resources"))).GetProperty("items").EnumerateArray());
            Assert.Equal(0,factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
