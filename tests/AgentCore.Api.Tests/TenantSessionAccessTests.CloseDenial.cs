using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Browser;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class UnavailableCloseTarget : IBrowser, IBrowserRuntimeReadiness, ITenantBrowserCommandTarget, ITenantBrowserBinding
    {
        private TenantBrowserOwner? owner;
        public int Preparations, Executions;
        public BrowserProviderDescriptor Provider => new("controlled-close-refusal", "Controlled current native target", new HashSet<BrowserFeature> { BrowserFeature.Close });
        public BrowserHostPolicy HostPolicy => new(true, true, BrowserInteractionMode.ReadNavigation, ["http://127.0.0.1:5091"]);
        public bool IsAvailable => true;
        public bool IsRuntimeReady => true;
        public void BindTenantSession(TenantBrowserOwner current) { if (owner is not null) Assert.Equal(owner, current); owner = current; }
        public ValueTask<string?> EnsureTenantIsolationAsync(TenantBrowserOwner current, BrowserProfileMode mode, CancellationToken ct = default) { BindTenantSession(current); return ValueTask.FromResult<string?>(null); }
        public ValueTask<string?> BindTenantIsolationAsync(TenantBrowserOwner current, BrowserProfileMode mode, CancellationToken ct = default) => throw new InvalidOperationException("Refusal cannot change isolation.");
        public ValueTask ReleaseTenantAsync(TenantBrowserOwner current, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask<TenantBrowserCommandPreparation> PrepareTenantCommandAsync(TenantBrowserOwner current, BrowserCommand command, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Assert.Equal(owner, current); Assert.IsType<BrowserClose>(command); Preparations++;
            // The offered engine was ready; the actual owned physical target is now unavailable.
            return ValueTask.FromResult(new TenantBrowserCommandPreparation("provider_unavailable"));
        }
        public ValueTask<BrowserResult> ExecuteAsync(BrowserRequest request, CancellationToken ct = default)
        { Executions++; throw new InvalidOperationException("No native command may follow refusal."); }
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_owned_close_refusal_reports_current_capability_or_target_blocker_without_native_dispatch_or_success(bool sqlite, bool authorized)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-close-refusal-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var browser = new UnavailableCloseTarget(); var model = new OwnedCloseRefusalModel(authorized);
            void Configure(IServiceCollection services)
            {
                ((BrowserOptions)services.Single(d => d.ServiceType == typeof(BrowserOptions)).ImplementationInstance!).Enabled = true;
                services.Replace(ServiceDescriptor.Singleton<IBrowser>(browser));
            }
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            using var owner = factory.CreateClient(); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 36, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid(); var revision = 1;
            if (!authorized)
            {
                var settings = await Json(await owner.GetAsync(api + $"/agents/{instance}/settings"));
                var selected = settings.GetProperty("items").EnumerateArray().Single(s => s.GetProperty("section").GetString() == "capabilities")
                    .GetProperty("effective").GetProperty("selected").EnumerateArray().Select(v => v.GetString()!).Where(v => v != ToolCatalog.BrowserClose).ToArray();
                Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync(api + $"/agents/{instance}/settings/capabilities", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1,
                    set = new { selected }, clear = Array.Empty<string>() })).StatusCode); revision++;
            }
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = revision }))).GetProperty("sessionId").GetGuid();
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Current close-refusal execution", ["agents.read", "sessions.read", "browser.read", "browser.use"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            browser.BindTenantSession(new(tenant, instance, executor, session));
            var id = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Close the browser and report only confirmed results.", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }))).GetProperty("runId").GetGuid();
            var final = await AwaitBrowserRestartRun(factory.Services.GetRequiredService<ITenantRunStore>(), tenant, id, false);
            Assert.True(final.Status == AgentRunStatus.Completed, $"{final.Status}: {final.Failure?.Code}: {final.Failure?.Summary}");
            Assert.Equal(2, model.Requests.Count); Assert.Equal(authorized ? 1 : 0, browser.Preparations); Assert.Equal(0, browser.Executions);
            Assert.Equal(AgentRunSideEffectDisposition.None, final.SideEffect.Disposition); Assert.False(RunBudgetDiagnosticProjection.From(final)!.ClosureConfirmed);
            Assert.Empty((await factory.Services.GetRequiredService<ITenantApprovalStore>().ListApprovalsAsync(tenant, null, null, new())).Items);
            var history = (await Json(await owner.GetAsync(api + $"/sessions/{session}/messages"))).GetProperty("items").EnumerateArray();
            Assert.Equal(authorized ? "Unable to confirm closure: provider_unavailable" : "Browser closure is not authorized.", history.Last(e => e.GetProperty("role").GetString() == "assistant").GetProperty("text").GetString());
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private sealed class OwnedCloseRefusalModel(bool authorized) : ILanguageModel
    {
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public ModelCapabilities Capabilities => new(true, true, Tools: true, StructuredOutput: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask; ct.ThrowIfCancellationRequested(); Requests.Enqueue(request);
            if (Requests.Count == 1)
            {
                if (authorized)
                {
                    Assert.Contains(ToolCatalog.BrowserClose, Assert.Single(request.Tools!, t => t.Name == ToolCatalog.RequestAuthorization).ParametersJson);
                    yield return new ModelToolCallEvent(new("refused-close", ToolCatalog.RequestAuthorization, """{"operation":"run_browser_command","command":{"tool":"browser.close","arguments":{}}}"""));
                }
                else
                {
                    Assert.DoesNotContain(request.Tools ?? [], t => t.Name == ToolCatalog.BrowserClose);
                    Assert.DoesNotContain(request.Tools ?? [], t => t.Name == ToolCatalog.RequestAuthorization && t.ParametersJson.Contains(ToolCatalog.BrowserClose, StringComparison.Ordinal));
                    yield return new ModelToolCallEvent(new("discover-close", ToolCatalog.CapabilitiesLoad, """{"query":"browser.close","limit":1}"""));
                }
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            var result = request.Messages.Last(m => m.Role == ModelRole.Tool);
            if (authorized) Assert.Contains("provider_unavailable", result.Text);
            else
            {
                using var json = JsonDocument.Parse(result.Text);
                Assert.Contains(json.RootElement.GetProperty("outcome").GetString(), new[] { "load_no_match", "load_unavailable" });
            }
            yield return new ModelSemanticResponseReady(new(authorized ? "Unable to confirm closure: provider_unavailable" : "Browser closure is not authorized.", new(ModelSpeechMode.Same, null), []));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }
}
