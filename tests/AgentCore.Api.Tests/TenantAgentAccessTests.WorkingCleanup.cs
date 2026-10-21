using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Workspaces;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantAgentAccessTests
{
    [Theory(Timeout = 90000)]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_working_cleanup_actual_request_pins_retention_and_deletion_preserves_other_namespaces(bool sqlite, bool afterTerminal)
    {
        await using var factory = new RequestIsolationFactory(sqlite);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance, isolation) = await IsolationAgent(client); var api = $"/api/v2/tenants/{tenant}";
        var preview = await WorkingRetention(client, isolation, afterTerminal);
        Assert.Equal(HttpStatusCode.OK, (await ApplyIsolation(client, isolation, preview)).StatusCode);
        var agent = await Json(await client.GetAsync(api + $"/agents/{instance}"));
        var role = await Json(await client.PostAsJsonAsync(api + "/roles", new { name = "Retention request", permissions = new[] { "agents.chat" }, expectedRevision = await Revision(client, tenant) }));
        await Json(await client.PostAsJsonAsync(api + "/role-assignments", new { principalId = agent.GetProperty("principalId").GetGuid(), roleId = role.GetProperty("roleId").GetGuid(), expectedRevision = await Revision(client, tenant) }));
        var session = (await Json(await client.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedInstanceRevision = 2, expectedRevision = await Revision(client, tenant) }))).GetProperty("sessionId").GetGuid();
        var sessions = factory.Services.GetRequiredService<ITenantSessionStore>(); var owner = (await sessions.FindTenantSessionAsync(tenant, session))!.Owner;
        var store = factory.Services.GetRequiredService<ITenantWorkingCleanupStore>(); var cleanup = factory.Services.GetRequiredService<TenantWorkingCleanupService>();
        var root = factory.Services.GetRequiredService<PersistenceOptions>().WorkspaceRoot;
        var path = AgentWorkspacePhysicalPaths.SessionRoot(root, instance, session); Directory.CreateDirectory(Path.Combine(path, "working"));
        await File.WriteAllTextAsync(Path.Combine(path, "working", "owned.txt"), "private working bytes"); await File.WriteAllTextAsync(Path.Combine(path, ".provisioned"), "retained marker");
        var home = AgentWorkspacePhysicalPaths.HomeDirectory(root, instance); Directory.CreateDirectory(home); await File.WriteAllTextAsync(Path.Combine(home, "keep.txt"), "home");
        var sibling = AgentWorkspacePhysicalPaths.WorkingDirectory(root, instance, Guid.NewGuid()); Directory.CreateDirectory(sibling); await File.WriteAllTextAsync(Path.Combine(sibling, "keep.txt"), "sibling");
        var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside); await File.WriteAllTextAsync(Path.Combine(outside, "keep.txt"), "outside"); Directory.CreateSymbolicLink(Path.Combine(path, "working", "redirect"), outside);
        Assert.Null(await store.ReadTenantWorkingCleanupAsync(owner)); Assert.False(await cleanup.CleanupAsync(owner));
        var message = Guid.NewGuid();
        var receipt = await Json(await client.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = message, text = "Retain the factual Synthetic answer", expectedRevision = await Revision(client, tenant), expectedSessionRevision = 1 }));
        var run = receipt.GetProperty("runId").GetGuid();
        // Change current configuration after admission; only the accepted pin may select terminal cleanup.
        preview = await WorkingRetention(client, isolation, !afterTerminal);
        Assert.Equal(HttpStatusCode.OK, (await ApplyIsolation(client, isolation, preview)).StatusCode);
        await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
        for (var i = 0; i < 400 && (await sessions.FindTenantSessionAsync(tenant, session))?.Snapshot.LifecycleStatus != SessionLifecycleStatus.Completed; i++) await Task.Delay(25);
        var terminal = (await sessions.FindTenantSessionAsync(tenant, session))!; Assert.Equal(SessionLifecycleStatus.Completed, terminal.Snapshot.LifecycleStatus);
        var history = (await Json(await client.GetAsync(api + $"/sessions/{session}/messages?limit=100"))).GetRawText(); Assert.Contains("Retain the factual Synthetic answer", history);
        Assert.Null(await store.ReadTenantWorkingCleanupAsync(owner with { TenantId = Guid.NewGuid() }));
        Assert.Null(await store.ReadTenantWorkingCleanupAsync(owner with { ExecutorPrincipalId = Guid.NewGuid() }));
        Assert.Equal(afterTerminal, await cleanup.CleanupAsync(owner)); Assert.Equal(!afterTerminal, Directory.Exists(path));
        if (afterTerminal)
        {
            Assert.Empty((await Json(await client.GetAsync(api + $"/sessions/{session}/workspace"))).GetProperty("items").EnumerateArray());
            Assert.False(Directory.Exists(path)); // Inspection cannot recreate a purged working tree.
        }
        var replay = await Json(await client.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = message, text = "Retain the factual Synthetic answer", expectedRevision = 1, expectedSessionRevision = 1 }));
        Assert.Equal(run, replay.GetProperty("runId").GetGuid());
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(api + $"/sessions/{session}?expectedRevision={await Revision(client, tenant)}&expectedSessionRevision={terminal.Snapshot.Revision}")).StatusCode);
        var target = Assert.IsType<TenantWorkingCleanupTarget>(await store.ReadTenantWorkingCleanupAsync(owner)); Assert.NotNull(target.DeletedAt);
        if (sqlite) store = new SqliteIamStore(factory.Services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<AgentCoreDbContext>>());
        var recovered = new TenantWorkingCleanupService(store, factory.Services.GetRequiredService<ITenantWorkingCleanupBytes>());
        Assert.Equal(1, await recovered.RecoverAsync()); Assert.False(Directory.Exists(path)); Assert.True(await recovered.CleanupAsync(owner));
        Assert.Equal("home", await File.ReadAllTextAsync(Path.Combine(home, "keep.txt"))); Assert.Equal("sibling", await File.ReadAllTextAsync(Path.Combine(sibling, "keep.txt"))); Assert.Equal("outside", await File.ReadAllTextAsync(Path.Combine(outside, "keep.txt")));
    }

    private static async Task<System.Text.Json.JsonElement> WorkingRetention(HttpClient client, string isolation, bool afterTerminal)
    {
        var preset = await Json(await client.PostAsync(isolation + "/presets/Stateless", null));
        var draft = JsonNode.Parse(preset.GetProperty("plan").GetProperty("draft").GetRawText())!;
        foreach (var patch in draft["settings"]!.AsArray()) if (patch!["section"]!.GetValue<string>() == "stateIsolationPolicy") patch["set"]!["workingCleanup"] = afterTerminal ? "AfterTerminal" : "OnSessionDeletion";
        return await Json(await client.PostAsJsonAsync(isolation + "/preview", draft));
    }

    [Theory(Timeout = 90000)]
    [InlineData(false, "archive")] [InlineData(true, "archive")] [InlineData(false, "complete")] [InlineData(true, "complete")]
    public async Task Tenant_working_cleanup_unused_Session_uses_its_creation_pin_and_never_cleans_archive(bool sqlite, string action)
    {
        await using var factory = new RequestIsolationFactory(sqlite); using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance, isolation) = await IsolationAgent(client); var api = $"/api/v2/tenants/{tenant}";
        var preview = await WorkingRetention(client, isolation, true); Assert.Equal(HttpStatusCode.OK, (await ApplyIsolation(client, isolation, preview)).StatusCode);
        var session = (await Json(await client.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedInstanceRevision = 2, expectedRevision = await Revision(client, tenant) }))).GetProperty("sessionId").GetGuid();
        var owner = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, session))!.Owner;
        var path = AgentWorkspacePhysicalPaths.WorkingDirectory(factory.Services.GetRequiredService<PersistenceOptions>().WorkspaceRoot, instance, session); Directory.CreateDirectory(path); await File.WriteAllTextAsync(Path.Combine(path, "private.txt"), "unused");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(api + $"/sessions/{session}/{action}", new { expectedRevision = await Revision(client, tenant), expectedSessionRevision = 1 })).StatusCode);
        Assert.Equal(action == "complete", await factory.Services.GetRequiredService<TenantWorkingCleanupService>().CleanupAsync(owner)); Assert.Equal(action != "complete", Directory.Exists(path));
        Assert.Empty(await factory.Services.GetRequiredService<ITenantRunStore>().ListTenantRunIdsAsync(tenant, session));
    }

    [Fact(Timeout = 90000)]
    public async Task Tenant_working_cleanup_SQLite_application_restart_retries_committed_deletion_without_reclaiming_Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-working-restart-" + Guid.NewGuid().ToString("N"));
        await using var first = new RequestIsolationFactory(true, root); using var client = first.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance, _) = await IsolationAgent(client); var api = $"/api/v2/tenants/{tenant}";
        var session = (await Json(await client.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedInstanceRevision = 1, expectedRevision = await Revision(client, tenant) }))).GetProperty("sessionId").GetGuid();
        var path = AgentWorkspacePhysicalPaths.WorkingDirectory(first.Services.GetRequiredService<PersistenceOptions>().WorkspaceRoot, instance, session); Directory.CreateDirectory(path); await File.WriteAllTextAsync(Path.Combine(path, "retained.txt"), "commit-to-bytes gap");
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(api + $"/sessions/{session}?expectedRevision={await Revision(client, tenant)}&expectedSessionRevision=1")).StatusCode);
        Assert.True(Directory.Exists(path)); await first.DisposeAsync();
        await using var reopened = new RequestIsolationFactory(true, root); using var again = reopened.CreateClient();
        for (var i = 0; i < 400 && Directory.Exists(path); i++) await Task.Delay(25);
        Assert.False(Directory.Exists(path)); Assert.Empty(await reopened.Services.GetRequiredService<ITenantRunStore>().ListTenantRunIdsAsync(tenant, session));
        Assert.Contains((await reopened.Services.GetRequiredService<ITenantWorkingCleanupStore>().ListTenantWorkingCleanupAsync(null, 100)).Items, o => o.SessionId == session);
    }
}
