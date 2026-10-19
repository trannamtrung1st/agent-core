using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Api.Security;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Human_materialization_access_hints_require_original_source_reads_and_report_independent_current_copy_authority(bool sqlite)
    {
        var proof = !sqlite && Environment.GetEnvironmentVariable("AGENTCORE_MATERIALIZATION_UI_PROOF") == "1";
        var root = Path.Combine(Path.GetTempPath(), "tenant-materialize-hints-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new ApprovalRecordingModel();
            void Configure(IServiceCollection services) { if (proof) ((IdentityHttpOptions)services.Single(d => d.ServiceType == typeof(IdentityHttpOptions)).ImplementationInstance!).AllowedOrigins = ["http://127.0.0.1:5233"]; }
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            if (proof) factory.UseKestrel(0);
            using var owner = factory.CreateClient(); var v = await PrepareMaterialization(owner, root);
            var hints = await Json(await owner.GetAsync(v.Url)); Assert.True(hints.GetProperty("canMaterialize").GetBoolean()); Assert.False(hints.GetProperty("readOnly").GetBoolean()); Assert.Equal("source.txt", hints.GetProperty("displayName").GetString());
            if (proof)
            {
                var sent = await Json(await owner.PostAsJsonAsync(v.SessionUrl + "/messages", new { messageId = Guid.NewGuid(), text = "Keep this file for later", attachmentIds = new[] { v.Attachment }, expectedRevision = await Revision(owner, v.Tenant), expectedSessionRevision = 1 }));
                await AwaitBrowserRestartRun(factory.Services.GetRequiredService<ITenantRunStore>(), v.Tenant, sent.GetProperty("runId").GetGuid(), false);
                const string path = "/private/tmp/agent-core-materialization-ui";
                await File.WriteAllTextAsync(path + ".json", JsonSerializer.Serialize(new { baseUrl = owner.BaseAddress!.ToString(), v.Tenant, v.Session, v.Attachment, v.Instance, root }));
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(18));
                while (!File.Exists(path + "-release")) await Task.Delay(100, deadline.Token);
                var page = await factory.Services.GetRequiredService<ITenantArtifactStore>().ListTenantAsync(v.Tenant, v.Session, null, null, 20);
                Assert.Equal(2, page.Items.Count); foreach (var item in page.Items) Assert.Equal(v.Bytes, await factory.Services.GetRequiredService<ITenantArtifactStore>().ReadTenantContentAsync(v.Tenant, v.Session, item.ArtifactId));
                Assert.Equal(v.Bytes, await factory.Services.GetRequiredService<ITenantAttachmentStore>().ReadTenantContentAsync(v.Tenant, v.Session, v.Attachment));
                return;
            }
            var access = factory.Services.GetRequiredService<TenantAccessService>(); var identity = factory.Services.GetRequiredService<IdentityService>();
            var account = await identity.RegisterAsync("copy-hints@example.test", "Copy hints", "strong-hints-password");
            var role = await access.SaveRoleAsync(v.Actor, v.Tenant, null, "Session only", ["sessions.read"], await Revision(owner, v.Tenant));
            var invite = await access.InviteAsync(v.Actor, v.Tenant, account.LoginKey, role.RoleId, await Revision(owner, v.Tenant));
            var member = await access.AcceptInvitationAsync(account.AccountId, v.Tenant, invite.Invitation.InvitationId, invite.Token);
            using var reader = factory.CreateClient(); await Login(reader, account.LoginKey, "strong-hints-password");
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(v.Url)).StatusCode);
            role = await access.SaveRoleAsync(v.Actor, v.Tenant, null, "Exact source", ["attachments.read"], await Revision(owner, v.Tenant));
            var grant = await access.AssignRoleAsync(v.Actor, v.Tenant, member.PrincipalId, role.RoleId, TenantAttachmentRules.Scope(v.Tenant, v.Attachment), await Revision(owner, v.Tenant));
            hints = await Json(await reader.GetAsync(v.Url)); Assert.False(hints.GetProperty("canMaterialize").GetBoolean()); Assert.False(hints.GetProperty("readOnly").GetBoolean());
            await access.RevokeAssignmentAsync(v.Actor, v.Tenant, grant.AssignmentId, await Revision(owner, v.Tenant)); Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(v.Url)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(v.SessionUrl + "/archive", new { expectedRevision = await Revision(owner, v.Tenant), expectedSessionRevision = 1 })).StatusCode);
            hints = await Json(await owner.GetAsync(v.Url)); Assert.False(hints.GetProperty("canMaterialize").GetBoolean()); Assert.True(hints.GetProperty("readOnly").GetBoolean());
            Assert.False(Directory.Exists(v.Physical)); Assert.Empty(model.Requests);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
