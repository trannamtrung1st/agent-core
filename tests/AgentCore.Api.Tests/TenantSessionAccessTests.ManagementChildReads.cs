using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;
public sealed partial class TenantSessionAccessTests
{
    public static IEnumerable<object[]> ManagementChildReadCases =>
        from sqlite in new[] { false, true }
        from tool in new[] { "skills.list", "skills.inspect", "resources.list", "resources.inspect" }
        from readable in new[] { false, true }
        select new object[] { sqlite, tool, readable };

    private static async Task<string> ChildReadArguments(HttpClient owner, Guid tenant, Guid instance, string tool)
    {
        if (tool.EndsWith(".list", StringComparison.Ordinal)) return "{}";
        var key = "instance:metadata-private";
        if (tool.StartsWith("resources.", StringComparison.Ordinal))
        {
            var page = await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/agents/{instance}/resources?pageSize=100"));
            key = page.GetProperty("catalog").GetProperty("resources").EnumerateArray().Single(r => r.GetProperty("logicalPath").GetString() == "private/metadata.txt").GetProperty("key").GetString()!;
        }
        return JsonSerializer.Serialize(new { key });
    }

    [Theory] [MemberData(nameof(ManagementChildReadCases))]
    public async Task Canonical_management_child_reads_use_exact_current_owned_sources_without_loading_or_changing_them(bool sqlite, string tool, bool readable)
    {
        var root = Path.Combine(Path.GetTempPath(), "management-child-read-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new ManagementReadModel(tool);
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantApprovalSqliteFactory(root, model) : new ApprovalExecutionFactory();
            await using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton<ILanguageModel>(model))));
            using var owner = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            var (tenant, instance) = await SetupManagementInspection(host, owner, readable);
            model.ArgumentsOverride = await ChildReadArguments(owner, tenant, instance, tool);
            var api = $"/api/v2/tenants/{tenant}";
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 6 }))).GetProperty("sessionId").GetGuid();
            var submitted = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Inspect the exact current child without loading or changing it.", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
            Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
            var runId = (await Json(submitted)).GetProperty("runId").GetGuid(); if (factory is ApprovalExecutionFactory memory) Volatile.Write(ref memory.Enabled, true);
            var final = await AwaitManagementRun(host.Services.GetRequiredService<ITenantRunStore>(), tenant, runId);
            Assert.True(final.Status == AgentRunStatus.Completed, $"{final.Status}: {final.Failure}");
            var message = model.Requests.SelectMany(r => r.Messages).Last(m => m.Name == tool);
            Assert.DoesNotContain("PRIVATE-INSPECTION-SECRET-SENTINEL", message.Text);
            Assert.DoesNotContain("inspection-account", message.Text);
            var inspect = tool.EndsWith(".inspect", StringComparison.Ordinal);
            if (inspect && !readable)
            {
                Assert.Empty(TenantManagementReadReferences.Read(final.Checkpoint));
                Assert.DoesNotContain("\"tenantManagementRead\":true", message.Text);
                Assert.DoesNotContain("PRIVATE-INSPECTION", message.Text);
            }
            else
            {
                using var result = JsonDocument.Parse(message.Text);
                var data = result.RootElement.GetProperty("observation").GetProperty("data");
                if (!inspect)
                {
                    Assert.DoesNotContain("PRIVATE-INSPECTION", message.Text);
                    var items = data.GetProperty(tool.StartsWith("skills.", StringComparison.Ordinal) ? "skills" : "resources");
                    if (readable) Assert.NotEmpty(items.EnumerateArray()); else Assert.Empty(items.EnumerateArray());
                }
                else if (tool == "skills.inspect")
                {
                    Assert.Equal("PRIVATE-INSPECTION-PROCEDURE-SENTINEL", data.GetProperty("skill").GetProperty("procedure").GetString());
                    Assert.False(data.GetProperty("activated").GetBoolean());
                }
                else
                {
                    Assert.Equal("PRIVATE-INSPECTION-RESOURCE-BYTES-SENTINEL", data.GetProperty("content").GetString());
                    Assert.False(data.GetProperty("truncated").GetBoolean());
                }
                var reference = Assert.Single(TenantManagementReadReferences.Read(final.Checkpoint), r => r.ToolName == tool);
                var sources = TenantManagementInspectionSources.Read(reference.SourcesJson);
                if (readable) Assert.NotEmpty(sources); else Assert.Empty(sources);
                Assert.All(sources, s => Assert.Equal(tool == "skills.inspect" ? "skillProcedure" : tool == "skills.list" ? "skill" : "resource", s.Kind));
                await host.Services.GetRequiredService<ITenantManagementStore>().RequireTenantManagementReadSourcesAsync(new(tenant, runId), [reference], null);
            }
            Assert.DoesNotContain("instance:metadata-private", final.ActiveSkillKeys);
            Assert.Equal(6, final.Admission.Configuration!.InstanceRevision);
            Assert.Equal(6, (await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!.Instance.Revision);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    public static IEnumerable<object[]> ManagementChildConsentCases =>
        from sqlite in new[] { false, true }
        from tool in new[] { "skills.list", "skills.inspect", "resources.list", "resources.inspect" }
        from decision in new[] { "approve", "reject" }
        select new object[] { sqlite, tool, decision };

    [Theory] [MemberData(nameof(ManagementChildConsentCases))]
    public async Task Canonical_management_child_read_Review_withholds_private_content_and_only_authorizes_the_exact_saved_read(bool sqlite, string tool, string decision)
    {
        var root = Path.Combine(Path.GetTempPath(), "management-child-consent-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new ManagementReadModel(tool);
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantApprovalSqliteFactory(root, model) : new ApprovalExecutionFactory();
            await using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton<ILanguageModel>(model))));
            using var owner = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            var (tenant, instance) = await SetupManagementInspection(host, owner); var api = $"/api/v2/tenants/{tenant}";
            model.ArgumentsOverride = await ChildReadArguments(owner, tenant, instance, tool);
            var permission = tool.StartsWith("skills.", StringComparison.Ordinal) ? "skills.read" : "resources.read";
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/" + permission, new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 6 }))).GetProperty("sessionId").GetGuid();
            var submitted = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Review this one exact child read.", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
            Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
            var runId = (await Json(submitted)).GetProperty("runId").GetGuid(); if (factory is ApprovalExecutionFactory memory) Volatile.Write(ref memory.Enabled, true);
            var runs = host.Services.GetRequiredService<ITenantRunStore>(); var pending = await AwaitManagementRun(runs, tenant, runId, true);
            Assert.True(pending.Status == AgentRunStatus.WaitingForApproval, $"{pending.Status}: {pending.Failure}");
            Assert.DoesNotContain(model.Requests.SelectMany(r => r.Messages), m => m.Role == ModelRole.Tool && m.Text.Contains("PRIVATE-INSPECTION", StringComparison.Ordinal));
            var approvals = host.Services.GetRequiredService<ITenantApprovalStore>();
            var request = (await approvals.FindApprovalAsync(tenant, pending.ApprovalRequestId!.Value))!;
            Assert.Equal(ApprovalKind.Review, request.Kind); Assert.Equal(tool, request.Operation.ManagementRead!.ToolName);
            var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
            Assert.True(detail.GetProperty("review").GetProperty("canDecide").GetBoolean());
            if (tool.EndsWith(".inspect", StringComparison.Ordinal)) Assert.Contains(tool == "skills.inspect" ? "PRIVATE-INSPECTION-PROCEDURE-SENTINEL" : "PRIVATE-INSPECTION-RESOURCE-BYTES-SENTINEL", detail.GetRawText());
            else Assert.DoesNotContain("PRIVATE-INSPECTION", detail.GetRawText());
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision, actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = 1 })).StatusCode);
            var final = await AwaitManagementSuccessor(runs, tenant, session, request.RequestId, runId);
            Assert.True(final.Status == AgentRunStatus.Completed, $"{final.Status}: {final.Failure}");
            Assert.Equal(decision == "approve" ? 1 : 0, TenantManagementReadReferences.Read(final.Checkpoint).Count);
            var receipt = await host.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, request.Operation.OperationId);
            if (decision == "approve") Assert.Equal(TenantManagementReadRules.Completed, receipt!.ReasonCode); else Assert.Null(receipt);
            Assert.Null(await approvals.FindOperationGrantAsync(tenant, request.RequestId));
            Assert.DoesNotContain("instance:metadata-private", final.ActiveSkillKeys);
            Assert.Equal(6, (await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!.Instance.Revision);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
