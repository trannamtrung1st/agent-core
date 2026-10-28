using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
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
    public async Task Management_read_approval_visibility_requires_current_exact_Agent_read_before_paging(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "management-read-visibility-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new ManagementReadModel("settings.inspect", "instructions");
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantApprovalSqliteFactory(root, model) : new ApprovalExecutionFactory();
            await using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton<ILanguageModel>(model))));
            using var owner = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            var (tenant, instance) = await SetupManagementRead(host, owner, false); var api = $"/api/v2/tenants/{tenant}";
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/agents.read", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            var created = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2 })); var session = created.GetProperty("sessionId").GetGuid();
            var response = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Request exact settings consent.", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); var runId = (await Json(response)).GetProperty("runId").GetGuid();
            if (factory is ApprovalExecutionFactory memory) Volatile.Write(ref memory.Enabled, true);
            var pending = await AwaitManagementRun(host.Services.GetRequiredService<ITenantRunStore>(), tenant, runId, true);
            Assert.Equal(AgentRunStatus.WaitingForApproval, pending.Status); var requestId = pending.ApprovalRequestId!.Value;
            var role = await Json(await owner.PostAsJsonAsync(api + "/roles", new { name = "Review metadata only", permissions = new[] { "approvals.read", "sessions.read", "agents.chat" }, expectedRevision = await Revision(owner, tenant) }));
            var invitation = await Json(await owner.PostAsJsonAsync(api + "/members/invite", new { login = "management-observer@example.test", roleId = role.GetProperty("roleId").GetGuid(), expectedRevision = await Revision(owner, tenant) }));
            using var observer = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            await Csrf(observer);
            Assert.Equal(HttpStatusCode.Created, (await observer.PostAsJsonAsync("/api/v2/auth/register", new { login = "management-observer@example.test", displayName = "Management observer", password = "strong-management-observer-password",
                invitation = new { tenantId = tenant, invitationId = invitation.GetProperty("invitationId").GetGuid(), token = invitation.GetProperty("token").GetString() } })).StatusCode);
            await Login(observer, "management-observer@example.test", "strong-management-observer-password");
            async Task Hidden()
            {
                var inbox = await Json(await observer.GetAsync(api + "/approvals?pageSize=1")); Assert.Equal(0, inbox.GetProperty("total").GetInt32()); Assert.Empty(inbox.GetProperty("items").EnumerateArray());
                Assert.Equal(HttpStatusCode.Forbidden, (await observer.GetAsync(api + $"/approvals/{requestId}")).StatusCode);
            }
            await Hidden();
            var members = await Json(await owner.GetAsync(api + "/members"));
            var principal = members.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("login").GetString() == "MANAGEMENT-OBSERVER@EXAMPLE.TEST").GetProperty("principalId").GetGuid();
            var readRole = await Json(await owner.PostAsJsonAsync(api + "/roles", new { name = "Exact Agent read", permissions = new[] { "agents.read" }, expectedRevision = await Revision(owner, tenant) }));
            var other = (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 36, expectedRevision = await Revision(owner, tenant) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
            foreach (var target in new[] { other, instance })
            {
                var assignment = await owner.PostAsJsonAsync(api + "/role-assignments", new { principalId = principal, roleId = readRole.GetProperty("roleId").GetGuid(), resourceType = "agent", resourceId = target, expectedRevision = await Revision(owner, tenant) });
                Assert.Equal(HttpStatusCode.Created, assignment.StatusCode);
                if (target == other) await Hidden();
            }
            var visible = await Json(await observer.GetAsync(api + "/approvals?pageSize=1")); Assert.Equal(1, visible.GetProperty("total").GetInt32());
            Assert.Equal(requestId, Assert.Single(visible.GetProperty("items").EnumerateArray()).GetProperty("requestId").GetGuid());
            var detail = await Json(await observer.GetAsync(api + $"/approvals/{requestId}")); Assert.False(detail.GetProperty("review").GetProperty("canDecide").GetBoolean());
            Assert.True((await Json(await owner.GetAsync(api + $"/approvals/{requestId}"))).GetProperty("review").GetProperty("canDecide").GetBoolean());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
