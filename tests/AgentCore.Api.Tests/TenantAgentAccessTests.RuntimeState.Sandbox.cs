using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantAgentAccessTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Tenant_runtime_state_sandbox_counts_filter_retained_profile_session_and_executor_before_projection(bool sqlite)
    {
        await using var factory = new RuntimeStateFactory(sqlite);
        using var owner = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, id, isolation) = await IsolationAgent(owner); var api = $"/api/v2/tenants/{tenant}";
        var principal = (await Json(await owner.GetAsync(api + $"/agents/{id}"))).GetProperty("principalId").GetGuid();
        async Task<Guid> Session() => (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = id, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
        var a = await Session(); var b = await Session(); var now = DateTimeOffset.UtcNow;
        var first = new SandboxProfile("p-" + Guid.NewGuid().ToString("N"), 1, "Private profile", "Private metadata", true,
            "docker.io/library/python@sha256:" + new string('a', 64), InitializationScript: "echo private-state-source-sentinel", TenantId: tenant);
        var second = first with { Id = "p-" + Guid.NewGuid().ToString("N") };
        var leases = new[] {
            new SandboxLease(a, id, Guid.NewGuid(), 1, first, SandboxLeaseState.Ready, "private-provider-handle", now, now.AddHours(1), TenantId: tenant, ExecutorPrincipalId: principal),
            new SandboxLease(b, id, Guid.NewGuid(), 1, second, SandboxLeaseState.Terminating, "other-private-handle", now, now.AddHours(1), TenantId: tenant, ExecutorPrincipalId: principal) };
        if (!sqlite)
        {
            var backing = (InMemorySandboxStore)factory.Services.GetRequiredService<ISandboxStore>();
            lock (backing.TenantGate)
            {
                foreach (var profile in new[] { first, second }) Assert.True(backing.SaveTenantProfile(new(tenant, profile.Id, now), profile, 0));
                foreach (var lease in leases) backing.SaveTenantLease(lease);
            }
        }
        else
        {
            await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContextAsync();
            foreach (var profile in new[] { first, second }) { db.Add(new TenantSandboxProfileRecord(profile.Id, tenant, now)); db.Add(new SandboxProfileRow {
                Id = profile.Id, TenantId = tenant, Revision = 1, Json = JsonSerializer.Serialize(profile, SandboxRuntimePersistence.Json) }); }
            foreach (var lease in leases) db.Add(SandboxRuntimePersistence.Row(lease));
            await db.SaveChangesAsync();
        }
        var storage = factory.Services.GetRequiredService<ITenantRuntimeStateStore>();
        var query = new TenantRuntimeStateQuery(AgentRunOwner.ForTenant(tenant, id, principal), null, null, null);
        var all = await storage.ReadTenantSandboxStateAsync(query, now); Assert.Equal(1, all.ActiveGenerations); Assert.Equal(1, all.CleanupPending);
        var filtered = await storage.ReadTenantSandboxStateAsync(query with { SandboxProfiles = [first.Id] }, now);
        Assert.Equal(1, filtered.ActiveGenerations); Assert.Equal(0, filtered.CleanupPending);
        filtered = await storage.ReadTenantSandboxStateAsync(query with { Sessions = [b] }, now); Assert.Equal(0, filtered.ActiveGenerations); Assert.Equal(1, filtered.CleanupPending);
        filtered = await storage.ReadTenantSandboxStateAsync(query with { Sessions = [] }, now); Assert.Equal(0, filtered.ActiveGenerations); Assert.Equal(0, filtered.CleanupPending);
        filtered = await storage.ReadTenantSandboxStateAsync(query with { Owner = AgentRunOwner.ForTenant(tenant, id, Guid.NewGuid()) }, now); Assert.Equal(0, filtered.CleanupPending);
        var result = await Json(await owner.GetAsync(isolation.Replace("state-isolation", "runtime-state")));
        var sandbox = result.GetProperty("observation").GetProperty("resources").EnumerateArray().Single(r => r.GetProperty("kind").GetString() == "sandbox");
        Assert.Equal(1, sandbox.GetProperty("cleanupPending").GetInt32());
        foreach (var hidden in new[] { "private-state-source-sentinel", "private-provider-handle", first.Id, second.Id }) Assert.DoesNotContain(hidden, result.GetRawText());
    }
}
