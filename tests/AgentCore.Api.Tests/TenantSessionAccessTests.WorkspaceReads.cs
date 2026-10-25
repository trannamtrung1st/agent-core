using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Security.Cryptography;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Infrastructure.Workspaces;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_workspace_HTTP_reads_exact_owned_bytes_and_Office_only_with_independent_current_workspace_access(bool sqlite, bool working)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-workspace-http-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, new ScriptedLanguageModel()) : new NativeTenantMemoryFactory(root, new ScriptedLanguageModel());
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var principal = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var privateSpace = (await factory.Services.GetRequiredService<ITenantSpaceStore>().FindPrivateSpaceAsync(tenant, instance))!.SpaceId;
            Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(api + $"/agents/{instance}/workspace")).StatusCode);
            var url = api + (working ? $"/sessions/{session}/workspace" : $"/spaces/{privateSpace}/workspace");
            using (var anonymous = factory.CreateClient()) Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
            var logical = working ? "/working" : "/home";
            var physical = working ? AgentWorkspacePhysicalPaths.WorkingDirectory(Path.Combine(root, "workspaces"), instance, session)
                : AgentWorkspacePhysicalPaths.SpaceHomeDirectory(Path.Combine(root, "workspaces"), tenant, privateSpace);
            Assert.Empty((await Json(await owner.GetAsync(url))).GetProperty("items").EnumerateArray()); Assert.False(Directory.Exists(physical));
            var files = new CanonicalWorkspaceFilesystem(physical, logical); files.Ensure();
            var text = Encoding.UTF8.GetBytes("Exact private UTF-8 thử nghiệm"); await files.WriteAsync(logical + "/report.txt", text, 1024, 4096);
            var office = factory.Services.GetRequiredService<IOfficeProcessor>();
            var officeSources = new Dictionary<string, byte[]>();
            foreach (var format in new[] { "docx", "xlsx" })
            {
                var bytes = format == "docx" ? office.CreateDocument(new("Report", [new("Section", ["Bounded private Office text"], null)]), default).Bytes
                    : office.CreateWorkbook(new([new("Sheet", [[new("text", Text: "Bounded private Office text")]])]), default).Bytes;
                await files.WriteAsync(logical + "/report." + format, bytes, OfficeLimits.MaxPackageBytes, 1024 * 1024);
                officeSources.Add(format, bytes);
            }
            var identity = factory.Services.GetRequiredService<IdentityService>(); var access = factory.Services.GetRequiredService<TenantAccessService>();
            var account = await identity.RegisterAsync("workspace-reader@example.test", "Workspace reader", "strong-workspace-password");
            var parentRole = await access.SaveRoleAsync(principal, tenant, null, "Parent reads", ["spaces.read", "sessions.read"], await Revision(owner, tenant));
            var invitation = await access.InviteAsync(principal, tenant, account.LoginKey, parentRole.RoleId, await Revision(owner, tenant));
            var readerUser = await access.AcceptInvitationAsync(account.AccountId, tenant, invitation.Invitation.InvitationId, invitation.Token);
            using var reader = factory.CreateClient(); await Login(reader, account.LoginKey, "strong-workspace-password");
            var denied = await reader.GetAsync(url); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); Assert.DoesNotContain("report", await denied.Content.ReadAsStringAsync());
            var role = await access.SaveRoleAsync(principal, tenant, null, "Read exact workspace", ["workspaces.read"], await Revision(owner, tenant));
            var scope = working ? TenantWorkspaceRules.Working(tenant, session) : TenantWorkspaceRules.Home(tenant, privateSpace);
            var grant = await access.AssignRoleAsync(principal, tenant, readerUser.PrincipalId, role.RoleId, scope, await Revision(owner, tenant));
            var page = await Json(await reader.GetAsync(url + "?limit=1")); Assert.Single(page.GetProperty("items").EnumerateArray()); Assert.True(page.GetProperty("truncated").GetBoolean());
            var next = await Json(await reader.GetAsync(url + "?limit=1&cursor=" + Uri.EscapeDataString(page.GetProperty("nextPath").GetString()!)));
            Assert.NotEqual(page.GetProperty("items")[0].GetProperty("logicalPath").GetString(), next.GetProperty("items")[0].GetProperty("logicalPath").GetString());
            Assert.Single((await Json(await reader.GetAsync(url + "/search?q=.txt"))).GetProperty("items").EnumerateArray());
            var metadata = await Json(await reader.GetAsync(url + "/metadata?path=" + Uri.EscapeDataString(logical + "/report.txt")));
            Assert.Equal(text.Length, metadata.GetProperty("byteSize").GetInt64()); Assert.DoesNotContain(root, metadata.GetRawText()); Assert.False(metadata.TryGetProperty("bytes", out _));
            using var download = await reader.GetAsync(url + "/content?path=" + Uri.EscapeDataString(logical + "/report.txt"));
            Assert.Equal(HttpStatusCode.OK, download.StatusCode); Assert.Equal(text, await download.Content.ReadAsByteArrayAsync());
            Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType); Assert.True(download.Headers.CacheControl?.NoStore); Assert.Equal("nosniff", Assert.Single(download.Headers.GetValues("X-Content-Type-Options")));
            foreach (var format in new[] { "docx", "xlsx" })
            {
                var preview = await Json(await reader.GetAsync(url + "/office-preview?path=" + Uri.EscapeDataString(logical + "/report." + format)));
                Assert.Equal(format, preview.GetProperty("format").GetString());
                Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(officeSources[format])), preview.GetProperty("sha256").GetString());
                if (format == "docx") Assert.False(preview.GetProperty("emptyBody").GetBoolean());
                else Assert.Equal("Bounded private Office text", preview.GetProperty("rows")[0][0].GetProperty("value").GetString());
            }
            Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync(url + "?limit=257")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync(url + "?prefix=" + logical + "&prefix=" + logical)).StatusCode);
            Assert.NotEqual(HttpStatusCode.OK, (await reader.GetAsync(url + "/content?path=" + Uri.EscapeDataString((working ? "/home" : "/working") + "/report.txt"))).StatusCode);
            await access.RevokeAssignmentAsync(principal, tenant, grant.AssignmentId, await Revision(owner, tenant));
            foreach (var suffix in new[] { "", "/metadata", "/content", "/office-preview" })
                Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(url + suffix + (suffix == "" ? "" : "?path=" + Uri.EscapeDataString(logical + "/report.docx")))).StatusCode);
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
