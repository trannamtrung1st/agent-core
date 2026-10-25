using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    [Theory] [InlineData("email", false)] [InlineData("drive", false)] [InlineData("email", true)] [InlineData("drive", true)]
    public async Task Account_family_discovery_exact_pending_Review_survives_actual_SQLite_host_recreation(string family, bool revoke)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-account-discovery-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            ConnectedReadSetup setup; TenantApprovalRequest request; Guid initial; Guid human; Guid familyAssignment;
            await using (var first = new OAuthFactory(root, sqlite: true, preserve: true, execute: true, model: new AccountDiscoveryModel(TenantCredentialDiscoveryContract.Tool(family)!, true)))
            {
                using var owner = ConnectedClient(first); setup = await ConnectedReadAccount(first, owner, family == "email" ? "EmailAccount" : "GoogleDriveAccount", tools: true);
                var api = $"/api/v2/tenants/{setup.Tenant}"; human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
                var access = first.Services.GetRequiredService<TenantAccessService>(); var agent = (await first.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(setup.Tenant, setup.Instance))!;
                var basic = await access.SaveRoleAsync(human, setup.Tenant, null, "Read resumed discovery metadata", ["sessions.read", "credentials.read"], await Revision(owner, setup.Tenant));
                await access.AssignRoleAsync(human, setup.Tenant, agent.PrincipalId, basic.RoleId, AccessScope.ForTenant(setup.Tenant), await Revision(owner, setup.Tenant));
                var role = await access.SaveRoleAsync(human, setup.Tenant, null, "Read resumed account family", [family + ".read"], await Revision(owner, setup.Tenant));
                familyAssignment = (await access.AssignRoleAsync(human, setup.Tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(setup.Tenant), await Revision(owner, setup.Tenant))).AssignmentId;
                Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/" + family + ".read", new { mode = "always", independentReview = false,
                    expectedRevision = await Revision(owner, setup.Tenant), expectedPolicyRevision = 0 })).StatusCode);
                var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = setup.Instance, expectedRevision = await Revision(owner, setup.Tenant), expectedInstanceRevision = 2 }))).GetProperty("sessionId").GetGuid();
                initial = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Discover one exact family after application restart",
                    expectedRevision = await Revision(owner, setup.Tenant), expectedSessionRevision = 1 }))).GetProperty("runId").GetGuid();
                var waiting = await AwaitAccountConsent(first.Services.GetRequiredService<ITenantRunStore>(), setup.Tenant, initial, new HashSet<Guid>());
                Assert.Equal(AgentRunStatus.WaitingForApproval, waiting.Status);
                request = (await first.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(setup.Tenant, waiting.ApprovalRequestId!.Value))!;
                Assert.Equal(family, TenantCredentialDiscoveryContract.Parse(request.Operation.CredentialRead!.ArgumentsJson).Family);
                Assert.Equal(0, first.Outbound);
            }
            var model = new AccountDiscoveryModel(TenantCredentialDiscoveryContract.Tool(family)!, true);
            await using (var second = new OAuthFactory(root, sqlite: true, preserve: true, execute: true, model: model))
            {
                using var owner = ConnectedClient(second); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                var api = $"/api/v2/tenants/{setup.Tenant}";
                Assert.Equal(request, await second.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(setup.Tenant, request.RequestId));
                if (revoke) await second.Services.GetRequiredService<TenantAccessService>().RevokeAssignmentAsync(human, setup.Tenant, familyAssignment, await Revision(owner, setup.Tenant));
                var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
                Assert.Equal(!revoke, detail.GetProperty("review").GetProperty("canDecide").GetBoolean()); Assert.True(detail.GetProperty("review").GetProperty("canCancel").GetBoolean());
                using var decision = revoke ? await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/cancel", new { actionHash = request.Operation.ActionHash,
                    expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision })
                    : await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash,
                        expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision });
                Assert.Equal(HttpStatusCode.OK, decision.StatusCode);
                var finished = await AwaitCredentialRun(second.Services.GetRequiredService<ITenantRunStore>(), setup.Tenant, initial, false);
                Assert.True(finished.Status == AgentRunStatus.Completed, $"{finished.Status}: {finished.Failure?.Code}; {finished.Result?.Text}");
                if (revoke) Assert.DoesNotContain("Exact connected account", finished.Result!.Text);
                else Assert.Contains("Exact connected account", finished.Result!.Text);
                Assert.Equal(0, second.Outbound);
                Assert.Null(await second.Services.GetRequiredService<ITenantApprovalStore>().FindOperationGrantAsync(setup.Tenant, request.RequestId));
            }
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
