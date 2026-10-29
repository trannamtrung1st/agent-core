using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Tenant_conversation_HTTP_reads_actual_worker_result_under_current_cookie_scope_without_changing_delivery_receipts(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-history-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantApprovalSqliteFactory(root, new ScriptedLanguageModel()) : new ExecutingTenantFactory();
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var instance = (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) })))
                .GetProperty("instance").GetProperty("instanceId").GetGuid();
            await GrantNativePrivateSpaceRead(owner, tenant, instance);
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var receipt = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Hello Synthetic conversation history", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
            var run = await AwaitCancelledTenantRun(factory.Services.GetRequiredService<ITenantRunStore>(), tenant, receipt.GetProperty("runId").GetGuid());
            Assert.Equal(AgentRunStatus.Completed, run.Status);
            var route = api + $"/sessions/{session}/messages"; HttpResponseMessage response;
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                while ((response = await owner.GetAsync(route, deadline.Token)).StatusCode == HttpStatusCode.Conflict)
                { response.Dispose(); await Task.Delay(25, deadline.Token); }
            }
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
            var history = await Json(response); var items = history.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(2, items.Length); Assert.Equal("Hello Synthetic conversation history", items[0].GetProperty("text").GetString());
            Assert.Equal(run.Result!.Text, items[1].GetProperty("text").GetString()); Assert.Equal("completed", items[1].GetProperty("status").GetString());
            Assert.Equal(0, items[1].GetProperty("receivedTextEndExclusive").GetInt32()); Assert.Equal(0, items[1].GetProperty("heardTextEndExclusive").GetInt32());
            var older = await Json(await owner.GetAsync(route + "?limit=1")); Assert.True(older.GetProperty("hasOlder").GetBoolean()); Assert.Single(older.GetProperty("items").EnumerateArray());
            var empty = await Json(await owner.GetAsync(route + "?after=" + history.GetProperty("nextAfter").GetInt64())); Assert.Empty(empty.GetProperty("items").EnumerateArray());
            foreach (var query in new[] { "?after=0&before=1", "?limit=0", "?limit=101", "?after=-1", "?before=0" })
                Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync(route + query)).StatusCode);
            var raw = history.GetRawText(); Assert.DoesNotContain("checkpoint", raw, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("definition", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("permission", raw, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("profileId", raw, StringComparison.OrdinalIgnoreCase);
            using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(route)).StatusCode);
            var beta = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "History Beta", slug = "history-beta" }))).GetProperty("tenantId").GetGuid();
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{beta}/sessions/{session}/messages")).StatusCode);
            using var legacy = RetiredOwnerHeader.CreateClient(factory);
            Assert.Equal(HttpStatusCode.NotFound, (await legacy.GetAsync($"/api/v1/sessions/{session}/messages")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await legacy.GetAsync(route)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync("/api/v2/auth/logout", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync(route)).StatusCode);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
