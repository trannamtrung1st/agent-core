using System.Net;
using System.Net.Http.Json;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
namespace AgentCore.Api.Tests;
public sealed partial class TenantSessionAccessTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Context_creation_retries_keep_stable_IDs_and_revisions_without_replaying_or_revealing_protected_values(bool sqlite)
    {
        var root=Path.Combine(Path.GetTempPath(),"space-create-recovery-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            await using WebApplicationFactory<Program> factory=sqlite?new NativeTenantSqliteFactory(root,new ScriptedLanguageModel()):new NativeTenantMemoryFactory(root,new ScriptedLanguageModel());
            using var owner=factory.CreateClient();await Login(owner,"owner@example.test","explicit-synthetic-test-password");
            var tenant=(await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();var api=$"/api/v2/tenants/{tenant}";
            var space=Guid.NewGuid();var initial=await Revision(owner,tenant);
            var spaceBody=new{spaceId=space,name="Recoverable create",description="Stable identity",instructions="",expectedRevision=initial};
            Assert.Equal(HttpStatusCode.Created,(await owner.PostAsJsonAsync(api+"/spaces",spaceBody)).StatusCode);
            var afterSpace=await Revision(owner,tenant);Assert.Equal(initial+1,afterSpace);
            Assert.Equal(HttpStatusCode.Created,(await owner.PostAsJsonAsync(api+"/spaces",spaceBody)).StatusCode);Assert.Equal(afterSpace,await Revision(owner,tenant));
            Assert.Equal(HttpStatusCode.Conflict,(await owner.PostAsJsonAsync(api+"/spaces",new{spaceId=space,name="Different body",description="Stable identity",instructions="",expectedRevision=afterSpace})).StatusCode);
            var environment=Guid.NewGuid();var envPath=api+$"/spaces/{space}/environments";
            var envBody=new{environmentId=environment,name="Saved context",description="Stable profile",expectedRevision=afterSpace,expectedSpaceRevision=1};
            Assert.Equal(HttpStatusCode.Created,(await owner.PostAsJsonAsync(envPath,envBody)).StatusCode);
            var afterEnvironment=await Revision(owner,tenant);
            Assert.Equal(HttpStatusCode.Created,(await owner.PostAsJsonAsync(envPath,envBody)).StatusCode);Assert.Equal(afterEnvironment,await Revision(owner,tenant));
            var variablePath=envPath+$"/{environment}/variables/MODE";
            var variableBody=new{kind="Plain",description="On demand",plainValue="PRIVATE_PLAIN_CREATE_VALUE",expectedRevision=afterEnvironment,expectedSpaceRevision=1,expectedEnvironmentRevision=1,expectedVariableRevision=0};
            Assert.Equal(HttpStatusCode.OK,(await owner.PutAsJsonAsync(variablePath,variableBody)).StatusCode);var afterVariable=await Revision(owner,tenant);
            var replay=await owner.PutAsJsonAsync(variablePath,variableBody);Assert.Equal(HttpStatusCode.OK,replay.StatusCode);Assert.DoesNotContain("PRIVATE_PLAIN_CREATE_VALUE",await replay.Content.ReadAsStringAsync());
            Assert.Equal(afterVariable,await Revision(owner,tenant));
            Assert.Equal(2,(await Json(await owner.GetAsync(envPath+$"/{environment}"))).GetProperty("revision").GetInt64());
            Assert.Equal(HttpStatusCode.Conflict,(await owner.PutAsJsonAsync(variablePath,new{kind="Plain",description="On demand",plainValue="OTHER_CREATE_VALUE",expectedRevision=afterVariable,expectedSpaceRevision=1,expectedEnvironmentRevision=2,expectedVariableRevision=0})).StatusCode);
            var secret=Guid.NewGuid();var secretBody=new{secretId=secret,name="Protected create",description="Never echo",protectedValue="SECRET_CREATE_SENTINEL",expectedRevision=afterVariable};
            var created=await owner.PostAsJsonAsync(api+"/secrets",secretBody);Assert.Equal(HttpStatusCode.Created,created.StatusCode);Assert.Equal(secret,(await Json(created)).GetProperty("secretId").GetGuid());
            var afterSecret=await Revision(owner,tenant);var duplicate=await owner.PostAsJsonAsync(api+"/secrets",secretBody);
            Assert.Equal(HttpStatusCode.Created,duplicate.StatusCode);Assert.DoesNotContain("SECRET_CREATE_SENTINEL",await duplicate.Content.ReadAsStringAsync());Assert.Equal(afterSecret,await Revision(owner,tenant));
            Assert.Equal(HttpStatusCode.Conflict,(await owner.PostAsJsonAsync(api+"/secrets",new{secretId=secret,name="Protected create",description="Never echo",protectedValue="WRONG_SECRET_CREATE_VALUE",expectedRevision=afterSecret})).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest,(await owner.PostAsJsonAsync(api+"/secrets",new{name="Missing identity",description="",protectedValue="SECRET_CREATE_SENTINEL",expectedRevision=afterSecret})).StatusCode);
            Assert.Equal(afterSecret,await Revision(owner,tenant));
            Assert.Single((await Json(await owner.GetAsync(api+"/secrets"))).GetProperty("items").EnumerateArray());
            var access=factory.Services.GetRequiredService<AgentCore.Application.Security.TenantAccessService>();
            var identity=factory.Services.GetRequiredService<AgentCore.Application.Security.IdentityService>();
            var actor=(await Json(await owner.GetAsync(api+"/access/me"))).GetProperty("principalId").GetGuid();
            var account=await identity.RegisterAsync("another-secret-manager@example.test","Other manager","strong-manager-password");
            var role=await access.SaveRoleAsync(actor,tenant,null,"Write-only Secret manager",["secrets.read","secrets.manage"],await Revision(owner,tenant));
            var invitation=await access.InviteAsync(actor,tenant,account.LoginKey,role.RoleId,await Revision(owner,tenant));
            await access.AcceptInvitationAsync(account.AccountId,tenant,invitation.Invitation.InvitationId,invitation.Token);
            using var manager=factory.CreateClient();await Login(manager,account.LoginKey,"strong-manager-password");
            var beforeProbe=await Revision(owner,tenant);
            foreach(var candidate in new[]{"SECRET_CREATE_SENTINEL","WRONG_SECRET_CREATE_VALUE"})
            {
                var probe=await manager.PostAsJsonAsync(api+"/secrets",new{secretId=secret,name="Protected create",description="Never echo",protectedValue=candidate,expectedRevision=beforeProbe});
                Assert.Equal(HttpStatusCode.Conflict,probe.StatusCode);Assert.Contains("create_identity_conflict",await probe.Content.ReadAsStringAsync());
            }
            Assert.Equal(beforeProbe,await Revision(owner,tenant));

            Assert.Equal(0,factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally{Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
