using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Memory;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantAgentAccessTests
{
    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_human_memory_HTTP_inspection_exact_forget_and_reset_use_actual_sources_and_CAS(bool sqlite)
    {
        var model = new LearnedMemoryModel(); model.Turns.Enqueue([Learn("PRIVATE_MANAGEMENT_EVIDENCE")]);
        await using var app = new RuntimeStateFactory(sqlite, model: model);
        var application = typeof(AgentCore.Application.Sessions.SessionManager).Assembly;
        foreach (var retired in new[] { "Admin.AdminMemoryService", "Admin.AdminMemoryHistoryService", "Admin.AdminAutomationService", "Admin.AdminAutomationHistoryService", "Ports.IAdminP7eHistoryMutator" })
            Assert.Null(application.GetType("AgentCore.Application." + retired));
        using var owner = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); var (tenant, agent) = await OwnedMemoryAgent(owner); await MemoryExecutor(owner, tenant, agent);
        var turn = await MemoryRequest(app, owner, tenant, agent); var api = $"/api/v2/tenants/{tenant}/agents/{agent}/memory";
        var page = await Json(await owner.GetAsync(api + "?scope=identityUser")); Assert.Equal(1, page.GetProperty("total").GetInt32());
        var item = Assert.Single(page.GetProperty("items").EnumerateArray()); Assert.Equal("PRIVATE_MANAGEMENT_EVIDENCE", item.GetProperty("content").GetString());
        Assert.True(item.GetProperty("canForget").GetBoolean()); Assert.True(page.GetProperty("canReset").GetBoolean());
        Assert.False(item.TryGetProperty("accountId", out _)); Assert.False(item.TryGetProperty("sourceEntryId", out _));
        var id = item.GetProperty("memoryId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(api + $"/{id}?scope=identityUser")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(api + $"/{id}?scope=session&sessionId={turn.Session}")).StatusCode);
        var stale = await owner.PostAsJsonAsync(api + "/forget", new { scope = "identityUser", memoryId = id, expectedRevision = page.GetProperty("securityRevision").GetInt64(), expectedMemoryRevision = 99 }); Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var forgotten = await Json(await owner.PostAsJsonAsync(api + "/forget", new { scope = "identityUser", memoryId = id, expectedRevision = page.GetProperty("securityRevision").GetInt64(), expectedMemoryRevision = item.GetProperty("revision").GetInt64() }));
        Assert.Equal(1, forgotten.GetProperty("itemsForgotten").GetInt32());
        var tombstones = await Json(await owner.GetAsync(api + "?scope=identityUser")); var deleted = Assert.Single(tombstones.GetProperty("items").EnumerateArray());
        Assert.Equal("Deleted", deleted.GetProperty("status").GetString()); Assert.Equal("", deleted.GetProperty("content").GetString()); Assert.Equal("", deleted.GetProperty("subject").GetString()); Assert.False(deleted.GetProperty("canForget").GetBoolean());
        Assert.Equal(1, (await Json(await owner.GetAsync(api + "?scope=user"))).GetProperty("retainedCount").GetInt32());
        var session = await Json(await owner.GetAsync(api + $"?scope=session&sessionId={turn.Session}"));
        var reset = await Json(await owner.PostAsJsonAsync(api + "/forget", new { scope = "session", sessionId = turn.Session, expectedRevision = session.GetProperty("securityRevision").GetInt64(), expectedRetainedCount = 1 })); Assert.Equal(1, reset.GetProperty("itemsForgotten").GetInt32());
        Assert.Equal(0, (await Json(await owner.GetAsync(api + $"?scope=session&sessionId={turn.Session}"))).GetProperty("retainedCount").GetInt32());
    }
    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_human_memory_HTTP_rejects_unknown_duplicate_owner_parameters_and_missing_Session(bool sqlite)
    {
        await using var app = new RuntimeStateFactory(sqlite); using var owner = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); var (tenant, agent) = await OwnedMemoryAgent(owner); var api = $"/api/v2/tenants/{tenant}/agents/{agent}/memory";
        foreach (var query in new[] { "?scope=session", "?scope=identityUser&principalId=other", "?scope=user&scope=user", "?scope=user&page=0", "?scope=user&sessionId=" + Guid.NewGuid() })
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync(api + query)).StatusCode);
        foreach (var body in new[] { "{}", "{\"scope\":\"user\",\"expectedRevision\":1,\"principalId\":\"other\"}", "{\"scope\":\"user\",\"scope\":\"user\",\"expectedRevision\":1,\"expectedRetainedCount\":1}" })
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsync(api + "/forget", new StringContent(body, System.Text.Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{Guid.NewGuid()}/agents/{agent}/memory?scope=user")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"/api/v2/tenants/{tenant}/agents/{Guid.NewGuid()}/memory?scope=user")).StatusCode);
    }
    [Fact(Timeout = 120000)]
    public async Task Tenant_human_memory_SQLite_application_recreation_retains_forgetting_without_replaying_work()
    {
        var model = new LearnedMemoryModel(); model.Turns.Enqueue([Learn("RETAINED_OTHER_SCOPE_VALUE")]); await using var first = new RuntimeStateFactory(true, model: model);
        using var owner = first.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, agent) = await OwnedMemoryAgent(owner); await MemoryExecutor(owner, tenant, agent); var turn = await MemoryRequest(first, owner, tenant, agent);
        var api = $"/api/v2/tenants/{tenant}/agents/{agent}/memory"; var page = await Json(await owner.GetAsync(api + "?scope=identityUser")); var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        var oldRevision = page.GetProperty("securityRevision").GetInt64();
        await Json(await owner.PostAsJsonAsync(api + "/forget", new { scope = "identityUser", expectedRevision = oldRevision, expectedRetainedCount = 1 }));
        await first.DisposeAsync(); var nextModel = new LearnedMemoryModel(); await using var reopened = new RuntimeStateFactory(true, model: nextModel, root: first.Root);
        using var again = reopened.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); await Login(again, "owner@example.test", "explicit-synthetic-test-password");
        var current = await Json(await again.GetAsync(api + "?scope=identityUser")); Assert.Equal(0, current.GetProperty("retainedCount").GetInt32());
        var tombstone = Assert.Single(current.GetProperty("items").EnumerateArray()); Assert.Equal(item.GetProperty("memoryId").GetGuid(), tombstone.GetProperty("memoryId").GetGuid()); Assert.Equal("Deleted", tombstone.GetProperty("status").GetString()); Assert.Equal("", tombstone.GetProperty("content").GetString());
        Assert.Equal(1, (await Json(await again.GetAsync(api + "?scope=user"))).GetProperty("retainedCount").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await again.PostAsJsonAsync(api + "/forget", new { scope = "identityUser", expectedRevision = oldRevision, expectedRetainedCount = 1 })).StatusCode);
        await Json(await again.GetAsync($"/api/v2/tenants/{tenant}/sessions/{turn.Session}/messages")); Assert.Empty(nextModel.Requests); Assert.Single(model.Requests);
    }

}
