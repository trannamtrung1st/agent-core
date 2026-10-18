using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Api.Security;
using AgentCore.Application.Security;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Governance_HTTP_collection_detail_and_witnessed_save_keep_read_manage_and_tenants_independent(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-governance-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var proof = !sqlite && Environment.GetEnvironmentVariable("AGENTCORE_GOVERNANCE_UI_PROOF") == "1";
        try
        {
            void Configure(IServiceCollection services)
            {
                if (proof) ((IdentityHttpOptions)services.Single(d => d.ServiceType == typeof(IdentityHttpOptions)).ImplementationInstance!).AllowedOrigins = ["http://127.0.0.1:5215"];
            }
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, new ScriptedLanguageModel(), Configure) : new NativeTenantMemoryFactory(root, new ScriptedLanguageModel(), Configure);
            if (proof) factory.UseKestrel(0);
            using var owner = proof ? factory.CreateClient() : factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var principal = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var identity = factory.Services.GetRequiredService<IdentityService>(); var access = factory.Services.GetRequiredService<TenantAccessService>();
            var account = await identity.RegisterAsync("governance-reader@example.test", "Read-only governance member", "explicit-governance-reader-password");
            var role = await access.SaveRoleAsync(principal, tenant, null, "Policy inspection", ["tenant.governance.read"], await Revision(owner, tenant));
            var invitation = await access.InviteAsync(principal, tenant, account.LoginKey, role.RoleId, await Revision(owner, tenant));
            var member = await access.AcceptInvitationAsync(account.AccountId, tenant, invitation.Invitation.InvitationId, invitation.Token);
            using var reader = proof ? factory.CreateClient() : factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            await Login(reader, account.LoginKey, "explicit-governance-reader-password");
            var path = api + "/operation-policies";
            var result = await reader.GetAsync(path + "?pageSize=2&q=agents."); Assert.True(result.Headers.CacheControl?.NoStore);
            var page = await Json(result); Assert.Equal(2, page.GetProperty("items").GetArrayLength()); Assert.True(page.GetProperty("total").GetInt32() > 2);
            Assert.All(page.GetProperty("items").EnumerateArray(), p => { Assert.False(p.GetProperty("canManage").GetBoolean()); Assert.Equal(page.GetProperty("securityRevision").GetInt64(), p.GetProperty("securityRevision").GetInt64()); });
            var view = await Json(await reader.GetAsync(path + "/agents.read")); Assert.Equal("none", view.GetProperty("minimum").GetProperty("mode").GetString());
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync(path + "/agents.read", new { mode = "always", independentReview = true, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            var witness = await Revision(owner, tenant);
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(path + "/agents.read", new { mode = "always", independentReview = true, expectedRevision = witness, expectedPolicyRevision = 0 })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync(path + "/agents.read", new { mode = "none", independentReview = false, expectedRevision = witness, expectedPolicyRevision = 0 })).StatusCode);
            var current = await Json(await reader.GetAsync(path + "/agents.read")); Assert.Equal("always", current.GetProperty("effective").GetProperty("mode").GetString());
            Assert.Empty((await Json(await reader.GetAsync(path + "?q=system."))).GetProperty("items").EnumerateArray());
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync(path + "?pageSize=101")).StatusCode);
            var other = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Separate Governance", slug = "separate-governance" }))).GetProperty("tenantId").GetGuid();
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"/api/v2/tenants/{other}/operation-policies")).StatusCode);
            Assert.Equal("none", (await Json(await owner.GetAsync($"/api/v2/tenants/{other}/operation-policies/agents.read"))).GetProperty("effective").GetProperty("mode").GetString());
            using var anonymous = proof ? factory.CreateClient() : factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
            if (proof)
            {
                await File.WriteAllTextAsync("/private/tmp/agent-core-governance-ui-proof-20261018.json", JsonSerializer.Serialize(new { baseUrl = owner.BaseAddress!.ToString(), tenant }));
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
                while (!File.Exists("/private/tmp/agent-core-governance-ui-release-20261018")) await Task.Delay(100, deadline.Token);
            }
            var state = (await factory.Services.GetRequiredService<AgentCore.Application.Ports.IIamStore>().ReadTenantAsync(tenant))!;
            await access.RevokeAssignmentAsync(principal, tenant, state.Assignments.Single(a => a.PrincipalId == member.PrincipalId).AssignmentId, await Revision(owner, tenant));
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(path)).StatusCode);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
