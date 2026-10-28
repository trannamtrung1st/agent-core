using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Browser;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class ProductVerificationModel(string mode) : ILanguageModel
    {
        public string Url = "";
        public int Phase;
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public ModelCapabilities Capabilities => new(true, true, Tools: true, StructuredOutput: true);
        public IReadOnlyList<ProductPublishStep> Steps { get; private set; } = [];
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask; ct.ThrowIfCancellationRequested(); Requests.Enqueue(request);
            var phase = Phase++;
            Assert.DoesNotContain(request.Messages, m => m.Name == ToolCatalog.AppMessageSend);
            if (phase == 0 || phase == 1 && mode != "login")
            {
                var command = phase == 0 ? JsonSerializer.Serialize(new { tool = ToolCatalog.BrowserNavigate, arguments = new { url = Url } })
                    : """{"tool":"browser.click","arguments":{"target":{"by":"role","value":"button","name":"Publish","exact":true}}}""";
                yield return new ModelToolCallEvent(new("product-" + phase, ToolCatalog.RequestAuthorization,
                    "{\"operation\":\"run_browser_command\",\"command\":" + command + "}"));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            if (phase == 2 && mode == "observed")
            {
                Assert.Contains(request.Tools!, t => t.Name == ToolCatalog.BrowserSnapshot);
                yield return new ModelToolCallEvent(new("product-postcondition", ToolCatalog.BrowserSnapshot, "{}"));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            Steps = BrowserControlEvidence.Read(request.Messages).Select(item => new ProductPublishStep(item.Call.Name, item.Json)).ToArray();
            var verified = ProductPublishVerification.IsStorefrontVerified(Steps);
            Assert.Equal(mode == "observed", verified);
            if (mode == "login") Assert.Contains(Steps, s => s.Detail.Contains("user_intervention_required", StringComparison.Ordinal));
            yield return new ModelSemanticResponseReady(new(verified ? "Storefront publication verified." : "Publication remains unverified.", new(ModelSpeechMode.None, null), []));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }

    [TenantBrowserChromiumTheory]
    [InlineData(false, "observed")] [InlineData(true, "observed")]
    [InlineData(false, "click-only")] [InlineData(true, "click-only")]
    [InlineData(false, "login")] [InlineData(true, "login")]
    public async Task Tenant_product_publication_requires_post_effect_native_observation_and_hands_off_at_login(bool sqlite, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-product-verification-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var html = mode == "login"
            ? "<html><head><title>Sign in</title></head><body><form><label>Username<input name=username></label><label>Password<input type=password></label><label>Verification code<input autocomplete=one-time-code></label><button>Sign in</button></form></body></html>"
            : "<html><head><title>Product publication</title></head><body><button onclick=\"document.getElementById('store').textContent='AC Keyboard $99 Published ac-keyboard.png'\">Publish</button><div id=store>Draft product</div></body></html>";
        await using var page = new LoopbackBrowserFixtureHost(NullLogger.Instance, name => name == LoopbackBrowserFixtureHost.IndexResource
            ? new MemoryStream(Encoding.UTF8.GetBytes(html)) : typeof(LoopbackBrowserFixtureHost).Assembly.GetManifestResourceStream(name));
        await page.StartAsync(0, default); Assert.True(page.IsAvailable);
        try
        {
            var model = new ProductVerificationModel(mode) { Url = page.Origin + "/" };
            void Configure(IServiceCollection services)
            {
                var options = (BrowserOptions)services.Single(d => d.ServiceType == typeof(BrowserOptions)).ImplementationInstance!;
                options.Enabled = true; options.Headless = true; options.Channel = null; options.FixtureEnabled = false;
                options.ProfileRoot = Path.Combine(root, "browser"); options.PolicyMode = "Restricted"; options.ProfileMode = "EphemeralSession";
                options.NavigationOrigins = [page.Origin!]; options.TargetOrigins = null; options.InteractionOrigins = null; options.ResourceOrigins = [];
            }
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            using var owner = factory.CreateClient(); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 36, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid();
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Exact product observation", ["agents.read", "sessions.read", "browser.read", "browser.use"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            var sent = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Publish then verify the actual storefront postcondition", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
            var initial = sent.GetProperty("runId").GetGuid(); var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var decisions = 0;
            while (true)
            {
                var waiting = await AwaitBrowserRestartRun(runs, tenant, initial, true);
                if (waiting.IsTerminal) break;
                Assert.InRange(decisions++, 0, 1);
                var saved = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, waiting.ApprovalRequestId!.Value))!;
                using var decision = await owner.PostAsJsonAsync(api + $"/approvals/{saved.RequestId}/decision", new { decision = "approve", actionHash = saved.Operation.ActionHash,
                    expectedRevision = await Revision(owner, tenant), expectedRequestRevision = saved.Revision });
                Assert.True(decision.StatusCode == HttpStatusCode.OK, await decision.Content.ReadAsStringAsync());
                await factory.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync();
            }
            var final = await AwaitBrowserRestartRun(runs, tenant, initial, false);
            Assert.True(final.Status == AgentRunStatus.Completed, $"{final.Status}: {final.Failure?.Code}; phase={model.Phase}");
            Assert.Equal(mode == "login" ? 1 : 2, decisions);
            Assert.Equal(mode == "observed" ? 4 : mode == "login" ? 2 : 3, model.Requests.Count);
            Assert.Equal(mode == "login" ? 0 : 1, model.Steps.Count(s => s.Tool == ToolCatalog.BrowserClick));
            Assert.Equal(mode == "observed" ? 1 : 0, model.Steps.Count(s => s.Tool == ToolCatalog.BrowserSnapshot));
            var history = (await Json(await owner.GetAsync(api + $"/sessions/{session}/messages"))).GetProperty("items").EnumerateArray().ToArray();
            Assert.Single(history, h => h.GetProperty("role").GetString() == "assistant" && h.GetProperty("status").GetString() == "completed");
            Assert.DoesNotContain(history, h => h.GetProperty("role").GetString() == "applicationMessage");
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
