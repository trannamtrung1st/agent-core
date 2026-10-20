using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory(Timeout = 120000)] [InlineData(false, "review")] [InlineData(true, "review")]
    [InlineData(false, "grant")] [InlineData(true, "grant")]
    [InlineData(false, "reject")] [InlineData(true, "reject")]
    [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    [InlineData(false, "changed")] [InlineData(true, "changed")]
    [InlineData(false, "revoked")] [InlineData(true, "revoked")]
    public Task Tenant_background_report_creation_consent_preserves_finished_child_and_delivers_only_exact_authorized_report(bool sqlite, string mode) => BackgroundReportFlow(sqlite, mode);
    [Fact(Timeout = 120000)]
    public Task Tenant_background_report_pending_consent_survives_actual_SQLite_host_recreation() => BackgroundReportFlow(true, "review", true);
    [Theory(Timeout = 120000)] [InlineData(false, "review")] [InlineData(true, "review")]
    [InlineData(false, "reject")] [InlineData(true, "reject")] [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    [InlineData(false, "grant")] [InlineData(true, "grant")]
    public Task Tenant_background_report_source_read_consent_precedes_separate_creation_consent(bool sqlite, string decision) => BackgroundReportFlow(sqlite, "source-" + decision);
    [Fact(Timeout = 120000)]
    public Task Tenant_background_report_source_read_consent_survives_actual_SQLite_host_recreation() => BackgroundReportFlow(true, "source-review", readRestart: true);
    private async Task BackgroundReportFlow(bool sqlite, string mode, bool restart = false, bool readRestart = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-report-approval-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        WebApplicationFactory<Program>? reopened = null; HttpClient? reopenedOwner = null;
        try
        {
            var model = new BackgroundCreationModel();
            await using WebApplicationFactory<Program> initial = sqlite ? new TenantBackgroundSqliteFactory(root, model) : new TenantBackgroundMemoryFactory(root, model);
            var factory = initial;
            using var firstOwner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); var owner = firstOwner;
            var setup = await StartTenantBackground(factory, owner, createAuthority: false, submit: false, reportReads: true); var tenant = setup.Tenant; var api = $"/api/v2/tenants/{tenant}";
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var sessions = factory.Services.GetRequiredService<ITenantSessionStore>();
            var parent = (await sessions.FindTenantSessionAsync(tenant, setup.Session))!; var access = factory.Services.GetRequiredService<TenantAccessService>();
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var role = await access.SaveRoleAsync(human, tenant, null, "Exact reporting executor", mode is "grant" or "source-grant" ? ["sessions.create"] : ["sessions.create", "agent-runs.create"], await Revision(owner, tenant));
            var assignment = await access.AssignRoleAsync(human, tenant, parent.Owner.ExecutorPrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            if (mode is not ("grant" or "source-grant")) Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/agent-runs.create", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            if (mode.StartsWith("source-", StringComparison.Ordinal)) Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/agent-runs.read", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            var sourceId = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{setup.Session}/messages", new { messageId = Guid.NewGuid(), text = "Start exact work with independently reviewed report delivery", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }))).GetProperty("runId").GetGuid();
            var approvals = factory.Services.GetRequiredService<ITenantApprovalStore>(); var waiting = await AwaitBrowserRestartRun(runs, tenant, sourceId, true);
            var creation = (await approvals.FindApprovalAsync(tenant, waiting.ApprovalRequestId!.Value))!; Assert.NotNull(creation.Operation.BackgroundStart);
            async Task Decide(TenantApprovalRequest request, string decision)
            {
                var view = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
                var response = await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/" + (decision == "cancel" ? "cancel" : "decision"), new { decision, actionHash = request.Operation.ActionHash,
                    expectedRevision = view.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision });
                Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            }
            await Decide(creation, "approve"); var source = await AwaitBrowserRestartRun(runs, tenant, sourceId, false); Assert.Equal(AgentRunStatus.Completed, source.Status);
            var child = (await WaitTenantBackground(factory, tenant, setup.Session, source.AgentRunId, false)).Child;
            if (mode.StartsWith("source-", StringComparison.Ordinal))
            {
                TenantApprovalRequest? read = null; using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                do { read = (await approvals.ListApprovalsAsync(tenant, null, null, new(PageSize: 100))).Items.SingleOrDefault(r => r.Operation.ReportSourceRead?.SourceRunId == child.AgentRunId);
                    if (read is null) await Task.Delay(25, readTimeout.Token); } while (read is null);
                Assert.Equal(ApprovalKind.Review, read.Kind); Assert.Equal(setup.Session, read.Operation.ReportSourceRead!.ParentSessionId);
                Assert.Equal(child.Revision, (await runs.FindTenantRunAsync(tenant, child.AgentRunId))!.Revision);
                Assert.DoesNotContain((await approvals.ListApprovalsAsync(tenant, null, null, new(PageSize: 100))).Items, r => r.Operation.BackgroundReport is not null);
                if (readRestart)
                {
                    await initial.DisposeAsync(); reopened = new TenantBackgroundSqliteFactory(root, new BackgroundCreationModel()); factory = reopened;
                    reopenedOwner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); owner = reopenedOwner;
                    await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                    runs = factory.Services.GetRequiredService<ITenantRunStore>(); sessions = factory.Services.GetRequiredService<ITenantSessionStore>(); approvals = factory.Services.GetRequiredService<ITenantApprovalStore>();
                    Assert.Equal(read.Operation, (await approvals.FindApprovalAsync(tenant, read.RequestId))!.Operation);
                }
                var readDetail = await Json(await owner.GetAsync(api + $"/approvals/{read.RequestId}"));
                Assert.Equal("inspect_background_report_source", readDetail.GetProperty("preparedAction").GetProperty("operation").GetString());
                Assert.Equal(setup.Session, readDetail.GetProperty("destinationSessionId").GetGuid()); Assert.True(readDetail.GetProperty("review").GetProperty("canDecide").GetBoolean(), readDetail.GetRawText());
                var parentApprovals = await Json(await owner.GetAsync(api + $"/sessions/{setup.Session}/approvals?pageSize=100"));
                Assert.Contains(parentApprovals.GetProperty("items").EnumerateArray(), r => r.GetProperty("requestId").GetGuid() == read.RequestId);
                await Decide(read, mode is "source-review" or "source-grant" ? "approve" : mode == "source-cancel" ? "cancel" : "reject");
                if (mode is not ("source-review" or "source-grant"))
                {
                    using var skipTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); CompletionInboxItem? item;
                    do { item = await runs.FindTenantCompletionAsync(child.Owner, child.AgentRunId); if (item?.Status != CompletionInboxStatus.Skipped) await Task.Delay(25, skipTimeout.Token); } while (item?.Status != CompletionInboxStatus.Skipped);
                    Assert.Null(item.ReportActivationId); Assert.Equal(child.Revision, (await runs.FindTenantRunAsync(tenant, child.AgentRunId))!.Revision);
                    Assert.DoesNotContain((await approvals.ListApprovalsAsync(tenant, null, null, new(PageSize: 100))).Items, r => r.Operation.BackgroundReport is not null);
                    Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts); return;
                }
                mode = mode == "source-grant" ? "grant" : "review";
            }
            TenantApprovalRequest? request = null; using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            do { request = (await approvals.ListApprovalsAsync(tenant, null, null, new(PageSize: 100))).Items.SingleOrDefault(r => r.Operation.BackgroundReport?.ChildRunId == child.AgentRunId); if (request is null)
                {
                    var currentInbox = await runs.FindTenantCompletionAsync(child.Owner, child.AgentRunId);
                    Assert.True(currentInbox?.Status != CompletionInboxStatus.Skipped, $"Report skipped: {currentInbox?.SkipReason}; {string.Join('\n', model.Failures)}");
                    await Task.Delay(25, timeout.Token);
                } } while (request is null);
            var p = request.Operation.BackgroundReport!;
            Assert.Equal(mode == "grant" ? ApprovalKind.ExceptionalGrant : ApprovalKind.Review, request.Kind); Assert.Equal(setup.Session, p.ParentSessionId);
            Assert.Equal(child.AgentRunId, request.RunId); Assert.Equal(child.SessionId, request.SessionId);
            Assert.Equal(child.Revision, (await runs.FindTenantRunAsync(tenant, child.AgentRunId))!.Revision); Assert.Null(await runs.FindTenantRunAsync(tenant, p.RunId));
            var inbox = await runs.FindTenantCompletionAsync(child.Owner, child.AgentRunId); Assert.Equal(CompletionInboxStatus.Pending, inbox!.Status);
            var visible = await Json(await owner.GetAsync(api + $"/sessions/{setup.Session}/approvals?pageSize=100"));
            Assert.Contains(visible.GetProperty("items").EnumerateArray(), item => item.GetProperty("requestId").GetGuid() == request.RequestId);
            var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}")); Assert.True(detail.GetProperty("review").GetProperty("canDecide").GetBoolean(), detail.GetRawText());
            Assert.Equal("deliver_background_completion", detail.GetProperty("preparedAction").GetProperty("operation").GetString());
            if (restart)
            {
                await initial.DisposeAsync(); reopened = new TenantBackgroundSqliteFactory(root, new BackgroundCreationModel()); factory = reopened;
                reopenedOwner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); owner = reopenedOwner;
                await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                runs = factory.Services.GetRequiredService<ITenantRunStore>(); sessions = factory.Services.GetRequiredService<ITenantSessionStore>(); approvals = factory.Services.GetRequiredService<ITenantApprovalStore>();
                Assert.Equal(request.Operation, (await approvals.FindApprovalAsync(tenant, request.RequestId))!.Operation);
                Assert.Null(await runs.FindTenantRunAsync(tenant, p.RunId));
            }
            if (mode == "changed") Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync(api + $"/agents/{parent.Owner.AgentInstanceId}/settings/conversationPolicy", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1, set = new { responseLength = "concise" }, clear = Array.Empty<string>() })).StatusCode);
            if (mode == "revoked") await access.RevokeAssignmentAsync(human, tenant, assignment.AssignmentId, await Revision(owner, tenant));
            if (mode is "changed" or "revoked") Assert.False((await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"))).GetProperty("review").GetProperty("canDecide").GetBoolean());
            await Decide(request, mode is "changed" or "revoked" or "cancel" ? "cancel" : mode == "reject" ? "reject" : "approve");
            if (mode is "review" or "grant")
            {
                var report = (await WaitTenantBackground(factory, tenant, setup.Session, source.AgentRunId, true)).Report!;
                Assert.Equal(p.RunId, report.AgentRunId); Assert.Equal(p.ActivationId, report.ActivationId); Assert.Equal(p.EvidenceJson, report.Admission.Activation.EvidenceJson);
                var receipt = (await factory.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, request.Operation.OperationId))!;
                Assert.Equal(TenantOperationOutcome.Completed, receipt.Outcome); Assert.Equal("completion_report_created", receipt.ReasonCode); Assert.Equal(child.AgentRunId, receipt.RunId);
                var grant = await approvals.FindOperationGrantAsync(tenant, request.RequestId); if (mode == "grant") Assert.Equal(OperationGrantStatus.Consumed, grant!.Status); else Assert.Null(grant);
            }
            else
            {
                using var skip = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                do { inbox = await runs.FindTenantCompletionAsync(child.Owner, child.AgentRunId); if (inbox?.Status != CompletionInboxStatus.Skipped) await Task.Delay(25, skip.Token); } while (inbox?.Status != CompletionInboxStatus.Skipped);
                Assert.Null(await runs.FindTenantRunAsync(tenant, p.RunId));
            }
            Assert.Equal(child.Revision, (await runs.FindTenantRunAsync(tenant, child.AgentRunId))!.Revision); Assert.Equal(2, (await sessions.ListTenantSessionsAsync(tenant, null, 0, 100)).Items.Count);
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { reopenedOwner?.Dispose(); if (reopened is not null) await reopened.DisposeAsync(); SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
