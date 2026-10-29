using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Contracts.Realtime;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory]
    [InlineData(false, "approve")] [InlineData(true, "approve")]
    [InlineData(false, "reject")] [InlineData(true, "reject")]
    public async Task Original_conversation_receives_saved_wait_and_recovers_decision_after_independent_work(bool sqlite, string decision)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-chat-approval-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, new ScriptedLanguageModel()) : new NativeTenantMemoryFactory(root, new ScriptedLanguageModel());
            factory.UseKestrel(0); using var ready = factory.CreateClient();
            var cookies = new CookieContainer(); using var owner = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = ready.BaseAddress };
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var instance = (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
            await GrantNativePrivateSpaceRead(owner, tenant, instance);
            await Json(await owner.PutAsJsonAsync(api + $"/agents/{instance}/self-management", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1,
                overrides = new { mode = new { value = "Assisted" }, scopes = new { value = new[] { "Settings" } } } }));
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2 }))).GetProperty("sessionId").GetGuid();
            var events = new ConcurrentQueue<ServerEvent>(); await using var socket = TenantSocket(ready.BaseAddress!, cookies, events); await socket.StartAsync();
            Assert.True((await socket.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(tenant, session))).Accepted);
            var sent = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = ScriptedLanguageModel.ApprovalReviewMarker, expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
            var pending = await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.approval.pending"));
            Assert.Equal(sent.GetProperty("responseId").GetGuid().ToString("D"), pending.ResponseId); Assert.Equal(2, pending.Payload.Count);
            Assert.DoesNotContain(events, e => e.Type == "agent.approval.requested");
            var feed = await Json(await owner.GetAsync(api + $"/sessions/{session}/approvals?pageSize=1")); var item = Assert.Single(feed.GetProperty("items").EnumerateArray());
            var request = item.GetProperty("requestId").GetGuid(); Assert.Equal(request.ToString("D"), pending.Payload["requestId"]?.ToString()); Assert.True(item.GetProperty("waiting").GetBoolean());
            foreach (var key in new[] { "preparedAction", "actionHash", "targetResource", "permission", "persona", "grant" }) Assert.False(item.TryGetProperty(key, out _));
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync(api + $"/sessions/{session}/approvals?page=-1")).StatusCode);
            Assert.Empty((await Json(await owner.GetAsync(api + $"/sessions/{session}/approvals?page=1&pageSize=1"))).GetProperty("items").EnumerateArray());
            var reattached = new ConcurrentQueue<ServerEvent>(); await using var replacement = TenantSocket(ready.BaseAddress!, cookies, reattached); await replacement.StartAsync();
            Assert.True((await replacement.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(tenant, session))).Accepted);
            var snapshot = await AwaitTenantWire(() => reattached.FirstOrDefault(e => e.Type == "session.ready")); Assert.Null(snapshot.Payload["activeResponseId"]);
            var revision = (await Json(await owner.GetAsync(api + $"/sessions/{session}"))).GetProperty("revision").GetInt64();
            var independent = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Independent message while waiting", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = revision }));
            await AwaitTenantWire(() => reattached.FirstOrDefault(e => e.Type == "agent.response.completed" && e.ResponseId == independent.GetProperty("responseId").GetGuid().ToString("D")));
            var detail = await Json(await owner.GetAsync(api + $"/approvals/{request}"));
            await Json(await owner.PostAsJsonAsync(api + $"/approvals/{request}/decision", new { decision, actionHash = detail.GetProperty("preparedAction").GetProperty("actionHash").GetString(), expectedRevision = await Revision(owner, tenant), expectedRequestRevision = detail.GetProperty("requestRevision").GetInt64() }));
            await AwaitTenantWire(() => reattached.FirstOrDefault(e => e.Type == "agent.response.completed" && e.ResponseId == pending.ResponseId));
            var terminal = Assert.Single((await Json(await owner.GetAsync(api + $"/sessions/{session}/approvals"))).GetProperty("items").EnumerateArray());
            Assert.Equal(decision == "approve" ? "approved" : "rejected", terminal.GetProperty("status").GetString()); Assert.False(terminal.GetProperty("waiting").GetBoolean());
            Assert.Equal(decision == "approve" ? "completed" : null, terminal.GetProperty("outcome").GetString());
            Assert.Equal(0, (await Json(await owner.GetAsync(api + "/approvals?status=pending"))).GetProperty("total").GetInt32());
            Assert.Equal(AgentRunStatus.Completed, (await CurrentApprovalExecution(factory.Services.GetRequiredService<ITenantRunStore>(), tenant, sent.GetProperty("runId").GetGuid()))!.Status);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
