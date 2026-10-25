using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public async Task Explicit_agent_completion_requires_delegation_and_commits_final_history_with_terminal_outcome(bool sqlite, bool allowed)
    {
        var root = Path.Combine(Path.GetTempPath(), "chat-completion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, new AgentCore.Infrastructure.Providers.Synthetic.ScriptedLanguageModel()) : new NativeTenantMemoryFactory(root, new AgentCore.Infrastructure.Providers.Synthetic.ScriptedLanguageModel());
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false }); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
            var api = $"/api/v2/tenants/{tenant}";
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
            var space = (await Json(await owner.GetAsync(api + $"/agents/{instance}/private-space"))).GetProperty("spaceId").GetGuid();
            var role = await Json(await owner.PostAsJsonAsync(api + "/roles", new { name = "Exact Chat Space read", permissions = new[] { "spaces.read" }, expectedRevision = await Revision(owner, tenant) }));
            Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync(api + "/role-assignments", new { principalId = agent.GetProperty("principalId").GetGuid(),
                roleId = role.GetProperty("roleId").GetGuid(), resourceType = "space", resourceId = space.ToString("D"), expectedRevision = await Revision(owner, tenant) })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 })).StatusCode);
            var created = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }));
            var session = created.GetProperty("sessionId").GetGuid(); var sessionRevision = created.GetProperty("revision").GetInt64();
            Assert.Equal("disabled", created.GetProperty("agentCompletion").GetString());
            if (allowed)
            {
                var policy = await owner.PutAsJsonAsync(api + $"/sessions/{session}/agent-completion", new { allowed = true,
                    expectedRevision = await Revision(owner, tenant), expectedSessionRevision = sessionRevision });
                Assert.Equal(HttpStatusCode.OK, policy.StatusCode);
                var saved = await Json(policy); Assert.Equal("allowed", saved.GetProperty("agentCompletion").GetString());
                sessionRevision = saved.GetProperty("revision").GetInt64();
            }
            var admission = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(),
                text = "[test:session-complete] Finish this conversation.", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = sessionRevision });
            Assert.True(admission.IsSuccessStatusCode, await admission.Content.ReadAsStringAsync());
            var receipt = await Json(admission);
            var run = await AwaitCancelledTenantRun(factory.Services.GetRequiredService<ITenantRunStore>(), tenant, receipt.GetProperty("runId").GetGuid());
            Assert.Equal(AgentRunStatus.Completed, run.Status);
            var metadata = await Json(await owner.GetAsync(api + $"/sessions/{session}"));
            Assert.Equal(allowed ? "completed" : "active", metadata.GetProperty("lifecycleStatus").GetString());
            var history = await Json(await owner.GetAsync(api + $"/sessions/{session}/messages"));
            var entries = history.GetProperty("items").EnumerateArray().ToArray(); Assert.Equal(2, entries.Length);
            Assert.Equal("completed", entries[1].GetProperty("status").GetString());
            Assert.Equal(allowed ? "The requested conversation is complete. Goodbye." : "Agent completion is not allowed for this conversation.", entries[1].GetProperty("text").GetString());
            if (allowed)
            {
                Assert.Equal("agent", metadata.GetProperty("lifecycleSource").GetString());
                Assert.Equal("The requested conversation is complete.", metadata.GetProperty("lifecycleReason").GetString());
                var rejected = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Late input",
                    expectedRevision = await Revision(owner, tenant), expectedSessionRevision = metadata.GetProperty("revision").GetInt64() });
                Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            }
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
