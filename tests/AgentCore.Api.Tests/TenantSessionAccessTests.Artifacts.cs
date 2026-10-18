using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using AgentCore.Api.Http;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Artifact_HTTP_download_requires_current_independent_scopes_and_returns_exact_attachment_bytes(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-artifact-http-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            // Trusted fixture publication exercises the storage prerequisite only. No model publisher is enabled.
            void Configure(IServiceCollection services)
            {
                // Minimal-host fixture options are replaced after production registration.
                if (sqlite) services.Replace(ServiceDescriptor.Singleton<IArtifactStore>(p => new SqliteArtifactStore(
                    p.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(), p.GetRequiredService<TimeProvider>(), Path.Combine(root, "artifacts"))));
                services.Replace(ServiceDescriptor.Singleton<ITenantRunExecution>(p =>
                new MetadataExecutionGate(() => false, new TenantRunExecution(p.GetRequiredService<ITenantRuntimeStore>(), p.GetRequiredService<ITenantRunStore>(),
                    p.GetRequiredService<ITenantRunDispatcher>(), p.GetRequiredService<IIdGenerator>(), p.GetRequiredService<TimeProvider>()))));
            }
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, new ScriptedLanguageModel(), Configure) : new NativeTenantMemoryFactory(root, new ScriptedLanguageModel(), Configure);
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var runId = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Prepare a private file", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }))).GetProperty("runId").GetGuid();
            var run = (await factory.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, runId))!;
            var now = factory.Services.GetRequiredService<TimeProvider>().GetUtcNow();
            run = await factory.Services.GetRequiredService<ITenantRuntimeStore>().ApplyTenantRuntimeCommandAsync(run.Owner, runId,
                new AgentRunCommand.Claim(run.Revision, now, Guid.NewGuid(), now.AddMinutes(5)), Guid.NewGuid());
            var id = Guid.NewGuid(); var bytes = "<html>Download these exact private bytes</html>"u8.ToArray();
            await factory.Services.GetRequiredService<ITenantArtifactStore>().CreateTenantAsync(new(run.Owner, session, runId, run.Claim!.Generation), id, "../private report.html", "text/html", bytes);
            var url = api + $"/sessions/{session}/artifacts";
            var page = await Json(await owner.GetAsync(url + "?limit=1")); Assert.Single(page.GetProperty("items").EnumerateArray()); Assert.False(page.GetProperty("hasMore").GetBoolean());
            var metadata = await Json(await owner.GetAsync(url + $"/{id}")); Assert.Equal(id, metadata.GetProperty("artifactId").GetGuid());
            Assert.Equal(runId, metadata.GetProperty("agentRunId").GetGuid()); Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), metadata.GetProperty("sha256Hex").GetString());
            foreach (var field in new[] { "blobKey", "protectedPayload", "workspaceLogicalPath", "sourceAttachmentId", "tenantId" }) Assert.False(metadata.TryGetProperty(field, out _));
            using var download = await owner.GetAsync(url + $"/{id}/content"); Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync()); Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
            Assert.DoesNotContain("..", download.Content.Headers.ContentDisposition.ToString()); Assert.Equal("nosniff", Assert.Single(download.Headers.GetValues("X-Content-Type-Options"))); Assert.True(download.Headers.CacheControl?.NoStore);
            var office = factory.Services.GetRequiredService<IOfficeProcessor>();
            var officeIds = new List<Guid>();
            foreach (var format in new[] { "docx", "xlsx" })
            {
                var officeId = Guid.NewGuid(); officeIds.Add(officeId);
                var original = format == "docx" ? office.CreateDocument(new("Report", [new("Section", ["Exact readable Office text"], null)]), default).Bytes
                    : office.CreateWorkbook(new([new("Sheet", [[new("text", Text: "Exact readable Office text")]])]), default).Bytes;
                await factory.Services.GetRequiredService<ITenantArtifactStore>().CreateTenantAsync(new(run.Owner, session, runId, run.Claim!.Generation), officeId,
                    "report." + format, format == "docx" ? OfficeLimits.DocumentMime : OfficeLimits.SpreadsheetMime, original);
                var preview = await Json(await owner.GetAsync(url + $"/{officeId}/office-preview"));
                Assert.Equal(format, preview.GetProperty("format").GetString());
                Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(original)), preview.GetProperty("sha256").GetString());
                if (format == "docx") Assert.False(preview.GetProperty("emptyBody").GetBoolean());
                else Assert.Equal("Exact readable Office text", preview.GetProperty("rows")[0][0].GetProperty("value").GetString());
                Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(api + $"/sessions/{Guid.NewGuid()}/artifacts/{officeId}/office-preview")).StatusCode);
            }
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync(url + "?limit=101")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(api + $"/sessions/{Guid.NewGuid()}/artifacts/{id}/content")).StatusCode);
            var beta = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Artifact Beta", slug = "artifact-beta" }))).GetProperty("tenantId").GetGuid();
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{beta}/sessions/{session}/artifacts/{id}/content")).StatusCode);
            using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url + $"/{id}/content")).StatusCode);
            using var legacy = TestOwnerCapability.CreateOwnerClient(factory);
            Assert.Equal(HttpStatusCode.Unauthorized, (await legacy.GetAsync(url + $"/{id}/content")).StatusCode);
            var identity = factory.Services.GetRequiredService<IdentityService>(); var access = factory.Services.GetRequiredService<TenantAccessService>();
            var account = await identity.RegisterAsync("artifact-reader@example.test", "Artifact Reader", "strong-artifact-password");
            var role = await access.SaveRoleAsync(human, tenant, null, "Session reader", ["sessions.read"], await Revision(owner, tenant));
            var invite = await access.InviteAsync(human, tenant, account.LoginKey, role.RoleId, await Revision(owner, tenant));
            var member = await access.AcceptInvitationAsync(account.AccountId, tenant, invite.Invitation.InvitationId, invite.Token);
            using var reader = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            await Login(reader, account.LoginKey, "strong-artifact-password");
            Assert.Empty((await Json(await reader.GetAsync(url))).GetProperty("items").EnumerateArray());
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(url + $"/{id}/content")).StatusCode);
            role = await access.SaveRoleAsync(human, tenant, null, "Exact Artifact reader", ["artifacts.read"], await Revision(owner, tenant));
            var grant = await access.AssignRoleAsync(human, tenant, member.PrincipalId, role.RoleId, TenantArtifactRules.Scope(tenant, id), await Revision(owner, tenant));
            Assert.Equal(bytes, await reader.GetByteArrayAsync(url + $"/{id}/content"));
            foreach (var officeId in officeIds)
            {
                Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(url + $"/{officeId}/office-preview")).StatusCode);
                var officeGrant = await access.AssignRoleAsync(human, tenant, member.PrincipalId, role.RoleId, TenantArtifactRules.Scope(tenant, officeId), await Revision(owner, tenant));
                Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(url + $"/{officeId}/office-preview")).StatusCode);
                await access.RevokeAssignmentAsync(human, tenant, officeGrant.AssignmentId, await Revision(owner, tenant));
                Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(url + $"/{officeId}/office-preview")).StatusCode);
            }
            await access.RevokeAssignmentAsync(human, tenant, grant.AssignmentId, await Revision(owner, tenant));
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(url + $"/{id}/content")).StatusCode);
            Assert.Equal(0, factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
