using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory]
    [InlineData(false, "[]")] [InlineData(true, "[]")]
    [InlineData(false, "{broken")] [InlineData(true, "{broken")]
    [InlineData(false, "{\"query\":\"browser\",\"limit\":0}")] [InlineData(true, "{\"query\":\"browser\",\"limit\":0}")]
    [InlineData(false, "no-match")] [InlineData(true, "no-match")]
    public async Task Tenant_capability_discovery_preserves_actionable_invalid_receipts_and_bounds_equivalent_no_match(bool sqlite, string arguments)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-capability-recovery-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new OwnedCapabilityRecoveryModel(arguments);
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model) : new NativeTenantMemoryFactory(root, model);
            using var owner = factory.CreateClient(); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 32, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid();
            Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync(api + $"/agents/{instance}/settings/capabilities", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1,
                set = new { selected = Array.Empty<string>(), always = Array.Empty<string>() }, clear = Array.Empty<string>() })).StatusCode);
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2 }))).GetProperty("sessionId").GetGuid();
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Capability discovery execution", ["agents.read", "sessions.read"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            var id = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Discover a permitted capability or report a truthful limitation.", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }))).GetProperty("runId").GetGuid();
            var done = await AwaitBrowserRestartRun(factory.Services.GetRequiredService<ITenantRunStore>(), tenant, id, false);
            Assert.Equal(AgentRunStatus.Completed, done.Status); Assert.Empty(done.LoadedCapabilityIds);
            Assert.Equal(arguments == "no-match" ? 2 : 0, done.CapabilityLoadCount);
            Assert.Equal(arguments == "no-match" ? 5 : 2, model.Requests.Count);
            if (arguments == "no-match") Assert.Contains("invalidCallRecovery", done.Checkpoint!.PayloadJson);
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class OwnedCapabilityRecoveryModel(string arguments) : ILanguageModel
    {
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public ModelCapabilities Capabilities => new(true, true, Tools: true, StructuredOutput: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Enqueue(request); await Task.CompletedTask;
            var count = Requests.Count;
            if (count == (arguments == "no-match" ? 5 : 2))
            {
                var result = request.Messages.Last(m => m.Role == ModelRole.Tool);
                if (arguments == "no-match") Assert.Contains("invalid_tool_strategy_blocked", result.Text);
                else
                {
                    using var receipt = JsonDocument.Parse(result.Text);
                    Assert.Equal("load_invalid", receipt.RootElement.GetProperty("outcome").GetString());
                    Assert.Equal("invalid", receipt.RootElement.GetProperty("error").GetString());
                    Assert.Contains("query", receipt.RootElement.GetProperty("nextStep").GetString());
                }
                yield return new ModelSemanticResponseReady(new("No eligible capability was loaded; the request cannot proceed.", new(ModelSpeechMode.Same, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            var payload = arguments == "no-match" ? JsonSerializer.Serialize(new { query = count % 2 == 0 ? " BROWSER   TELEPORT " : "browser teleport", limit = count }) : arguments;
            Assert.Contains(request.Tools!, t => t.Name == ToolCatalog.CapabilitiesLoad);
            yield return new ModelToolCallEvent(new("owned-discovery-" + count, ToolCatalog.CapabilitiesLoad, payload));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }
}
