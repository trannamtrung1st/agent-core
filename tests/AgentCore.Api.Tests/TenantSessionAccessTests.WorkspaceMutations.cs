using System.Net;
using System.Net.Http.Json;
using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Infrastructure.Providers.Synthetic;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_workspace_HTTP_mutations_enforce_current_independent_IAM_CSRF_and_body_free_truthful_results(bool sqlite, bool working)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-workspace-mutate-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, new ScriptedLanguageModel()) : new NativeTenantMemoryFactory(root, new ScriptedLanguageModel());
            using var owner = factory.CreateClient(); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var principal = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var url = api + (working ? $"/sessions/{session}/workspace" : $"/agents/{instance}/workspace"); var logical = working ? "/working" : "/home";
            var physical = working ? AgentWorkspacePhysicalPaths.WorkingDirectory(Path.Combine(root, "workspaces"), instance, session) : AgentWorkspacePhysicalPaths.HomeDirectory(Path.Combine(root, "workspaces"), instance);
            var identity = factory.Services.GetRequiredService<IdentityService>(); var access = factory.Services.GetRequiredService<TenantAccessService>();
            var account = await identity.RegisterAsync("workspace-writer@example.test", "Workspace writer", "strong-workspace-password");
            var parents = await access.SaveRoleAsync(principal, tenant, null, "Parent reads", ["agents.read", "sessions.read"], await Revision(owner, tenant));
            var invitation = await access.InviteAsync(principal, tenant, account.LoginKey, parents.RoleId, await Revision(owner, tenant));
            var member = await access.AcceptInvitationAsync(account.AccountId, tenant, invitation.Invitation.InvitationId, invitation.Token);
            using var writer = factory.CreateClient(); await Login(writer, account.LoginKey, "strong-workspace-password");
            async Task<string> Target(string path, long? revision = null) => url + "/content?path=" + Uri.EscapeDataString(path) + "&expectedRevision=" + (revision ?? await Revision(writer, tenant));
            var bytes = Encoding.UTF8.GetBytes("Exact supplied UTF-8 thử nghiệm");
            Assert.Equal(HttpStatusCode.Forbidden, (await writer.PutAsync(await Target(logical + "/private-name.txt"), new ByteArrayContent(bytes))).StatusCode);
            Assert.False(Directory.Exists(physical));
            var role = await access.SaveRoleAsync(principal, tenant, null, "Workspace write", ["workspaces.write"], await Revision(owner, tenant));
            var scope = working ? TenantWorkspaceRules.Working(tenant, session) : TenantWorkspaceRules.Home(tenant, instance);
            await access.AssignRoleAsync(principal, tenant, member.PrincipalId, role.RoleId, scope, await Revision(owner, tenant));
            using (var anonymous = factory.CreateClient())
                Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsync(await Target(logical + "/anonymous.txt"), new ByteArrayContent(bytes))).StatusCode);
            foreach (var (invalid, status) in new[] { (logical, HttpStatusCode.BadRequest),
                (logical + "/../escape.txt", working ? HttpStatusCode.Forbidden : HttpStatusCode.BadRequest),
                ((working ? "/home" : "/working") + "/wrong-root.txt", HttpStatusCode.BadRequest) })
                Assert.Equal(status, (await writer.PutAsync(await Target(invalid), new ByteArrayContent(bytes))).StatusCode);
            Assert.False(Directory.Exists(physical));
            var saved = await writer.PutAsync(await Target(logical + "/private-name.txt"), new ByteArrayContent(bytes));
            Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode); Assert.Equal("", await saved.Content.ReadAsStringAsync());
            Assert.Equal(bytes, await owner.GetByteArrayAsync(url + "/content?path=" + Uri.EscapeDataString(logical + "/private-name.txt")));
            Assert.Equal(HttpStatusCode.Forbidden, (await writer.GetAsync(url + "/content?path=" + Uri.EscapeDataString(logical + "/private-name.txt"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await writer.DeleteAsync(await Target(logical + "/private-name.txt"))).StatusCode);
            var stale = await Revision(writer, tenant);
            role = await access.SaveRoleAsync(principal, tenant, null, "Workspace delete", ["workspaces.delete"], await Revision(owner, tenant));
            await access.AssignRoleAsync(principal, tenant, member.PrincipalId, role.RoleId, scope, await Revision(owner, tenant));
            Assert.Equal(HttpStatusCode.Conflict, (await writer.PutAsync(await Target(logical + "/private-name.txt", stale), new ByteArrayContent("stale"u8.ToArray()))).StatusCode);
            Assert.Equal(bytes, await owner.GetByteArrayAsync(url + "/content?path=" + Uri.EscapeDataString(logical + "/private-name.txt")));
            Assert.Equal(HttpStatusCode.NoContent, (await writer.DeleteAsync(await Target(logical + "/private-name.txt"))).StatusCode);
            Assert.False(File.Exists(Path.Combine(physical, "private-name.txt")));
            var files = new CanonicalWorkspaceFilesystem(physical, logical); files.Mkdir(logical + "/folder");
            await files.WriteAsync(logical + "/folder/retained.txt", "retained"u8.ToArray(), 1024, 4096);
            var uncertain = await writer.DeleteAsync(await Target(logical + "/folder")); Assert.Equal(HttpStatusCode.Conflict, uncertain.StatusCode);
            Assert.True((await Json(uncertain)).GetProperty("mutationsMayHaveOccurred").GetBoolean()); Assert.True(File.Exists(Path.Combine(physical, "folder", "retained.txt")));
            Assert.Equal(HttpStatusCode.BadRequest, (await writer.DeleteAsync(await Target(logical + "/folder") + "&recursive=true")).StatusCode);
            files.Mkdir(logical + "/empty"); Assert.Equal(HttpStatusCode.NoContent, (await writer.DeleteAsync(await Target(logical + "/empty"))).StatusCode);
            writer.DefaultRequestHeaders.Remove("X-AgentCore-CSRF");
            var csrf = await writer.PutAsync(await Target(logical + "/csrf-blocked.txt"), new ByteArrayContent(bytes));
            Assert.Equal(HttpStatusCode.BadRequest, csrf.StatusCode);
            Assert.Equal("invalid_csrf", (await Json(csrf)).GetProperty("reasonCode").GetString());
            Assert.False(File.Exists(Path.Combine(physical, "csrf-blocked.txt")));
            var audit = (await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 200)).Where(a => a.ReasonCode.StartsWith("human_workspace_", StringComparison.Ordinal)).ToArray();
            Assert.Contains(audit, a => a.ActorPrincipalId == member.PrincipalId && a.Action == "workspaces.write" && a.Decision == "allowed");
            Assert.Contains(audit, a => a.ActorPrincipalId == member.PrincipalId && a.Action == "workspaces.delete" && a.Decision == "denied");
            Assert.DoesNotContain("private-name", System.Text.Json.JsonSerializer.Serialize(audit));
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
