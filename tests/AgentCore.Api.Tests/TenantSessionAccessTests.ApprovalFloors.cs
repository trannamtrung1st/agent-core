using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Api.Tests;
public sealed partial class TenantSessionAccessTests
{
    private sealed class FloorInspectionExecution : ITenantRunExecution
    {
        public ValueTask<int> ExecuteRunnableAsync(int limit, CancellationToken ct = default) => ValueTask.FromResult(0);
    }

    private static async Task<(HttpClient Client, Guid Principal, Guid Role)> FloorHuman(WebApplicationFactory<Program> host,
        HttpClient owner, Guid tenant, string name, string[] permissions)
    {
        var api = $"/api/v2/tenants/{tenant}"; var login = name + "@example.test";
        var role = (await Json(await owner.PostAsJsonAsync(api + "/roles", new { name, permissions, expectedRevision = await Revision(owner, tenant) }))).GetProperty("roleId").GetGuid();
        var invitation = await Json(await owner.PostAsJsonAsync(api + "/members/invite", new { login, roleId = role, expectedRevision = await Revision(owner, tenant) }));
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        await Csrf(client);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v2/auth/register", new { login, displayName = name, password = "explicit-floor-user-password",
            invitation = new { tenantId = tenant, invitationId = invitation.GetProperty("invitationId").GetGuid(), token = invitation.GetProperty("token").GetString() } })).StatusCode);
        await Login(client, login, "explicit-floor-user-password");
        var principal = (await Json(await client.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        return (client, principal, role);
    }

    [Theory(Timeout = 180000)]
    [InlineData(false, "owner")] [InlineData(true, "owner")]
    [InlineData(false, "requester")] [InlineData(true, "requester")]
    [InlineData(false, "scheduled")] [InlineData(true, "scheduled")]
    public async Task Canonical_approval_floors_preserve_independent_authority_and_real_requester_policy_without_false_autonomous_review(bool sqlite, string origin)
    {
        var root = Path.Combine(Path.GetTempPath(), "approval-floor-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        HttpClient? requester = null; HttpClient? reviewerClient = null;
        try
        {
            var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantApprovalSqliteFactory(root, new AgentCore.Infrastructure.Providers.Synthetic.ScriptedLanguageModel()) : new AgentCoreApiFactory();
            await using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => {
                s.RemoveAll<TimeProvider>(); s.AddSingleton<TimeProvider>(clock);
                s.Replace(ServiceDescriptor.Singleton<ITenantRunExecution, FloorInspectionExecution>());
            }));
            using var owner = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            var (tenant, instance) = await SetupManagementRead(host, owner, false); var api = $"/api/v2/tenants/{tenant}";
            var state = (await host.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!;
            var ownerPrincipal = state.Users.Single().PrincipalId; var access = host.Services.GetRequiredService<TenantAccessService>();
            var agent = (await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
            var permissionKeys = new[] { "agents.read", "agents.update", "agents.delete", "memory.manage", "browser.use", "sandbox.execute", "automations.run", "sessions.create", "agent-runs.create" };
            var role = await access.SaveRoleAsync(ownerPrincipal, tenant, null, "Explicit floor executor", permissionKeys, await Revision(owner, tenant));
            await access.AssignRoleAsync(ownerPrincipal, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            Guid? requesterPrincipal = ownerPrincipal; Guid? requesterRole = null;
            var human = owner;
            if (origin == "requester")
            {
                var lower = await FloorHuman(host, owner, tenant, "floor-requester", ["agents.chat", "sessions.read", "sessions.create"]);
                requester = lower.Client; human = requester; requesterPrincipal = lower.Principal; requesterRole = lower.Role;
            }
            Guid runId;
            if (origin == "scheduled")
            {
                runId = await TenantCredentialAccessTests.AdmitIndependentTask(host, owner, tenant, instance, "Inspect current operation policy without executing protected effects.", clock, "scheduled");
                requesterPrincipal = null;
            }
            else
            {
                var created = await human.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2 });
                Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
                var session = (await Json(created)).GetProperty("sessionId").GetGuid();
                var submitted = await human.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Inspect the exact executor action policy.", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
                Assert.True(submitted.StatusCode == HttpStatusCode.Accepted, await submitted.Content.ReadAsStringAsync());
                runId = (await Json(submitted)).GetProperty("runId").GetGuid();
            }
            var run = (await host.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, runId))!;
            Assert.Equal(AgentRunStatus.Queued, run.Status); Assert.Equal(requesterPrincipal, run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId);
            Assert.Equal(agent.PrincipalId, run.Admission.ExecutionContext.AgentPrincipalId);
            Assert.Equal(origin == "scheduled" ? ExecutionOrigin.ScheduledOccurrence : ExecutionOrigin.UserMessage, run.Admission.ExecutionContext.Origin);
            var operations = host.Services.GetRequiredService<TenantOperationAuthorizer>();
            var reference = new TenantRunReference(tenant, runId); var target = instance.ToString("D");
            async Task<OperationAuthorizationDecision> Check(string permission) => await operations.CheckAsync(reference, permission, "agent", target);
            async Task Policy(string key, string mode, bool independent = false)
            {
                var current = await Json(await owner.GetAsync(api + "/operation-policies/" + key));
                var saved = await owner.PutAsJsonAsync(api + "/operation-policies/" + key, new { mode, independentReview = independent, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = current.GetProperty("policyRevision").GetInt64() });
                Assert.True(saved.StatusCode == HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
            }
            Assert.Equal(OperationDecisionKind.Allowed, (await Check("agents.read")).Decision);
            Assert.Equal(origin == "requester" ? OperationDecisionKind.RequiresApproval : OperationDecisionKind.Allowed, (await Check("agents.update")).Decision);
            Assert.Equal(origin == "requester" ? OperationDecisionKind.RequiresApproval : OperationDecisionKind.Allowed, (await Check("memory.manage")).Decision);
            await Policy("agents.read", "whenRequesterLacksPermission");
            Assert.Equal(origin == "requester" ? OperationDecisionKind.RequiresApproval : OperationDecisionKind.Allowed, (await Check("agents.read")).Decision);
            await Policy("agents.read", "none");
            Assert.Equal(OperationDecisionKind.Allowed, (await Check("agents.read")).Decision);
            await Policy("agents.update", "whenRequesterLacksPermission", true);
            Assert.Equal(origin == "requester" ? OperationDecisionKind.RequiresApproval : OperationDecisionKind.Allowed, (await Check("agents.update")).Decision);
            await Policy("agents.update", "always");
            Assert.Equal(ApprovalKind.Review, (await Check("agents.update")).ApprovalKind);
            await Policy("agents.update", "always", true);
            var independent = await Check("agents.update");
            Assert.Equal(origin == "owner" ? OperationDecisionKind.Denied : OperationDecisionKind.RequiresApproval, independent.Decision);
            if (origin == "owner") Assert.Equal("no_eligible_approver", independent.ReasonCode);
            var reviewer = await FloorHuman(host, owner, tenant, "floor-reviewer", ["agents.update", "approvals.approve"]); reviewerClient = reviewer.Client;
            Assert.Equal(ApprovalKind.Review, (await Check("agents.update")).ApprovalKind);
            await access.SaveRoleAsync(ownerPrincipal, tenant, reviewer.Role, "floor-reviewer", ["approvals.approve"], await Revision(owner, tenant));
            Assert.Equal(origin == "owner" ? OperationDecisionKind.Denied : OperationDecisionKind.RequiresApproval, (await Check("agents.update")).Decision);
            await Policy("agents.update", "whenRequesterLacksPermission");
            await access.SaveRoleAsync(ownerPrincipal, tenant, role.RoleId, role.Name, permissionKeys.Where(k => k is not ("agents.update" or "browser.use")).ToArray(), await Revision(owner, tenant));
            var exceptional = await Check("agents.update");
            Assert.Equal(OperationDecisionKind.RequiresApproval, exceptional.Decision); Assert.Equal(ApprovalKind.ExceptionalGrant, exceptional.ApprovalKind);
            Assert.Equal("nondelegatable", (await Check("browser.use")).ReasonCode);
            Assert.Equal("human_only", (await Check("iam.roles.assign")).ReasonCode);
            Assert.Equal(ApprovalKind.Review, (await Check("agents.delete")).ApprovalKind);
            Assert.Equal(ApprovalKind.Review, (await Check("sandbox.execute")).ApprovalKind);
            var before = await Revision(owner, tenant);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync(api + "/operation-policies/agents.delete", new { mode = "whenRequesterLacksPermission", independentReview = false, expectedRevision = before, expectedPolicyRevision = 0 })).StatusCode);
            Assert.Equal(before, await Revision(owner, tenant));
            Assert.Equal(0, (await host.Services.GetRequiredService<ITenantApprovalStore>().ListApprovalsAsync(tenant, null, null, new(0, 100))).Total);
            if (requesterRole is { } lowerRole)
            {
                await access.SaveRoleAsync(ownerPrincipal, tenant, lowerRole, "floor-requester", ["sessions.read"], await Revision(owner, tenant));
                Assert.Equal("requester_unavailable", (await Assert.ThrowsAsync<AccessDeniedException>(() => Check("agents.read"))).ReasonCode);
            }
        }
        finally { requester?.Dispose(); reviewerClient?.Dispose(); SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
