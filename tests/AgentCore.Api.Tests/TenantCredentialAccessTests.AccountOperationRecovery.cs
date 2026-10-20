using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Pending_operation_read_survives_actual_SQLite_host_recreation_with_current_original_Run_access(bool revoke)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-operation-read-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            ConnectedReadSetup setup; TenantApprovalRequest read; TenantOperationReceipt original; DriveOperation native;
            Guid initial; Guid human; Guid sourceReadAssignment;
            await using (var first = new OAuthFactory(root, sqlite: true, preserve: true, execute: true, model: new CredentialModel()))
            {
                using var owner = ConnectedClient(first); setup = await ConnectedReadAccount(first, owner, "GoogleDriveAccount", tools: true);
                var api = $"/api/v2/tenants/{setup.Tenant}"; human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
                var access = first.Services.GetRequiredService<TenantAccessService>(); var agent = (await first.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(setup.Tenant, setup.Instance))!;
                var basic = await access.SaveRoleAsync(human, setup.Tenant, null, "Read and execute exact restored action", ["sessions.read", "credentials.read", "credentials.use", "drive.read", "drive.write"], await Revision(owner, setup.Tenant));
                await access.AssignRoleAsync(human, setup.Tenant, agent.PrincipalId, basic.RoleId, AccessScope.ForTenant(setup.Tenant), await Revision(owner, setup.Tenant));
                var sourceRole = await access.SaveRoleAsync(human, setup.Tenant, null, "Read original operation Run", ["agent-runs.read"], await Revision(owner, setup.Tenant));
                sourceReadAssignment = (await access.AssignRoleAsync(human, setup.Tenant, agent.PrincipalId, sourceRole.RoleId, AccessScope.ForTenant(setup.Tenant), await Revision(owner, setup.Tenant))).AssignmentId;
                var command = "synthetic account actions " + JsonSerializer.Serialize(new { steps = new object[] {
                    new { tool = ToolCatalog.DriveCreateFolder, arguments = new { accountRef = setup.Alias, authEpoch = 1, name = "Exact restored folder", parentId = "root" } },
                    new { tool = ToolCatalog.DriveGet, arguments = new { accountRef = setup.Alias, operationId = "$operation" } }
                } });
                var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = setup.Instance, expectedRevision = await Revision(owner, setup.Tenant), expectedInstanceRevision = 2 }))).GetProperty("sessionId").GetGuid();
                initial = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = command,
                    expectedRevision = await Revision(owner, setup.Tenant), expectedSessionRevision = 1 }))).GetProperty("runId").GetGuid();
                var runs = first.Services.GetRequiredService<ITenantRunStore>(); var approvals = first.Services.GetRequiredService<ITenantApprovalStore>();
                var waiting = await AwaitAccountConsent(runs, setup.Tenant, initial, new HashSet<Guid>());
                Assert.Equal(AgentRunStatus.WaitingForApproval, waiting.Status);
                var action = (await approvals.FindApprovalAsync(setup.Tenant, waiting.ApprovalRequestId!.Value))!;
                var detail = await Json(await owner.GetAsync(api + $"/approvals/{action.RequestId}"));
                using var accepted = await owner.PostAsJsonAsync(api + $"/approvals/{action.RequestId}/decision", new { decision = "approve", actionHash = action.Operation.ActionHash,
                    expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = action.Revision });
                Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
                waiting = await AwaitAccountConsent(runs, setup.Tenant, initial, new HashSet<Guid> { action.RequestId });
                Assert.True(waiting.Status == AgentRunStatus.WaitingForApproval, $"{waiting.Status}: {waiting.Failure?.Code}; {waiting.Result?.Text}");
                read = (await approvals.FindApprovalAsync(setup.Tenant, waiting.ApprovalRequestId!.Value))!;
                Assert.Equal(action.Operation.OperationId, read.Operation.AccountRead!.Source!.OperationId);
                original = (await first.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(setup.Tenant, action.Operation.OperationId))!;
                native = (await first.Services.GetRequiredService<IDriveOperationStore>().GetAsync(setup.Instance, original.OperationId, default))!;
                Assert.Equal("Succeeded", native.State); Assert.Equal(0, first.Outbound);
            }
            await using (var second = new OAuthFactory(root, sqlite: true, preserve: true, execute: true, model: new CredentialModel()))
            {
                using var owner = ConnectedClient(second); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                var api = $"/api/v2/tenants/{setup.Tenant}";
                Assert.Equal(read, await second.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(setup.Tenant, read.RequestId));
                if (revoke) await second.Services.GetRequiredService<TenantAccessService>().RevokeAssignmentAsync(human, setup.Tenant, sourceReadAssignment, await Revision(owner, setup.Tenant));
                var detail = await Json(await owner.GetAsync(api + $"/approvals/{read.RequestId}"));
                Assert.Equal(!revoke, detail.GetProperty("review").GetProperty("canDecide").GetBoolean()); Assert.True(detail.GetProperty("review").GetProperty("canCancel").GetBoolean());
                Assert.Contains("Inspect prior Drive operation", detail.GetRawText()); Assert.Contains(original.RunId.ToString("D"), detail.GetRawText());
                using var decision = revoke ? await owner.PostAsJsonAsync(api + $"/approvals/{read.RequestId}/cancel", new { actionHash = read.Operation.ActionHash,
                    expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = read.Revision })
                    : await owner.PostAsJsonAsync(api + $"/approvals/{read.RequestId}/decision", new { decision = "approve", actionHash = read.Operation.ActionHash,
                        expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = read.Revision });
                Assert.Equal(HttpStatusCode.OK, decision.StatusCode);
                var finished = await AwaitCredentialRun(second.Services.GetRequiredService<ITenantRunStore>(), setup.Tenant, initial, false);
                Assert.True(finished.Status == AgentRunStatus.Completed, $"{finished.Status}: {finished.Failure?.Code}; {finished.Result?.Text}");
                if (revoke) Assert.DoesNotContain("prior Drive operation was inspected", finished.Result!.Text);
                else Assert.Contains("Recorded outcome: Completed", finished.Result!.Text);
                Assert.Equal(original, await second.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(setup.Tenant, original.OperationId));
                Assert.Equal(native, await second.Services.GetRequiredService<IDriveOperationStore>().GetAsync(setup.Instance, original.OperationId, default));
                Assert.Equal(0, second.Outbound);
            }
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
