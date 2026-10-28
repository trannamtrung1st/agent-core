using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Browser;
using AgentCore.Infrastructure.Providers.SemanticResponses;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class HandoffReadBrowser : IBrowser, IBrowserRuntimeReadiness, ITenantBrowserCommandTarget, ITenantBrowserBinding
    {
        public int Calls;
        public bool Challenge;
        public string CurrentUrl = "https://a.test/page";
        private TenantBrowserOwner? bound;
        private readonly Guid context = Guid.NewGuid();
        public BrowserProviderDescriptor Provider => new("controlled-handoff", "Controlled native read port", new HashSet<BrowserFeature> { BrowserFeature.Snapshot, BrowserFeature.Navigate, BrowserFeature.Close, BrowserFeature.Click });
        public BrowserHostPolicy HostPolicy => new(true, false, BrowserInteractionMode.InteractiveDemo, ["https://a.test", "https://b.test", "https://cars.test", "https://www.cars.test"]);
        public bool IsAvailable => true;
        public bool IsRuntimeReady => true;
        public void BindTenantSession(TenantBrowserOwner owner) { if (bound is not null) Assert.Equal(bound, owner); bound = owner; }
        public ValueTask<string?> EnsureTenantIsolationAsync(TenantBrowserOwner owner, BrowserProfileMode mode, CancellationToken ct = default) { BindTenantSession(owner); return ValueTask.FromResult<string?>(null); }
        public ValueTask<string?> BindTenantIsolationAsync(TenantBrowserOwner owner, BrowserProfileMode mode, CancellationToken ct = default) => throw new InvalidOperationException("A read cannot change isolation.");
        public ValueTask ReleaseTenantAsync(TenantBrowserOwner owner, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask<TenantBrowserCommandPreparation> PrepareTenantCommandAsync(TenantBrowserOwner owner, BrowserCommand command, CancellationToken ct = default)
        {
            Assert.Equal(bound, owner); Assert.True(command is BrowserObserve or BrowserNavigate or BrowserClose or BrowserClick);
            return ValueTask.FromResult(new TenantBrowserCommandPreparation(null, new(owner, context, 0, Calls, BrowserProfileMode.EphemeralSession,
                context, "pg_handoff", 1, null, new string('a', 64), BrowserCommandIdentity.Hash(command)), "https://a.test"));
        }
        public async ValueTask<BrowserResult> ExecuteAsync(BrowserRequest request, CancellationToken ct = default)
        {
            Assert.Equal(bound, request.TenantOwner); Assert.Equal(bound!.SessionId, request.SessionId);
            Assert.Equal(request.Command is BrowserObserve, request.TenantReadOnly); Assert.NotNull(request.TenantDispatchCheck); Assert.NotNull(request.TenantWitness);
            if (await request.TenantDispatchCheck(ct) is { } denied) return new(denied);
            Calls++;
            if (request.Command is BrowserClose) return new(null, Status: "closed", EffectAttempted: true, EffectConfirmedBySdk: true);
            if (request.Command is BrowserNavigate navigation)
            {
                Assert.NotNull(navigation.Url);
                CurrentUrl = navigation.Url.Replace("https://cars.test/", "https://www.cars.test/", StringComparison.Ordinal);
            }
            return new(null, new(CurrentUrl, "Owned handoff page", "Unchanged bounded page evidence", false, [],
                Intervention: Challenge && new Uri(CurrentUrl).Host != "b.test" ? BrowserInterventionKind.HumanVerificationRequired : BrowserInterventionKind.None),
                EffectAttempted: request.Command is BrowserNavigate or BrowserClick, EffectConfirmedBySdk: request.Command is BrowserNavigate or BrowserClick);
        }
    }

    private sealed class HandoffReadModel(string mode) : ILanguageModel
    {
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public bool NextTurn;
        private int generation;
        public ModelCapabilities Capabilities => new(true, true, Tools: true, StructuredOutput: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask; ct.ThrowIfCancellationRequested(); Requests.Enqueue(request);
            var index = generation++;
            if (mode.StartsWith("approved-", StringComparison.Ordinal))
            {
                ModelToolCall Navigate(string url) => new("open-" + index, ToolCatalog.RequestAuthorization,
                    JsonSerializer.Serialize(new { operation = "run_browser_command", command = new { tool = ToolCatalog.BrowserNavigate, arguments = new { url } } }));
                var read = new ModelToolCall("read-" + index, ToolCatalog.BrowserSnapshot, "{}");
                ModelToolCall[] actions = mode switch {
                    "approved-other" => [Navigate("https://a.test/page"), Navigate("https://b.test/page"), Navigate("https://a.test/again"), read, Navigate("https://a.test/later"),
                        new("act-" + index, ToolCatalog.RequestAuthorization, """{"operation":"run_browser_command","command":{"tool":"browser.click","arguments":{"target":{"by":"role","value":"button","name":"Save"}}}}""")],
                    "approved-redirect" => [Navigate("https://cars.test/page"), Navigate("https://cars.test/again"), Navigate("https://www.cars.test/again")],
                    "approved-close" => [Navigate("https://a.test/page"), new("close-" + index, ToolCatalog.RequestAuthorization,
                        """{"operation":"run_browser_command","command":{"tool":"browser.close","arguments":{}}}"""), Navigate("https://a.test/again"), Navigate("https://b.test/page"), read],
                    "approved-after-action" => [Navigate("https://a.test/page"), Navigate("https://b.test/page"),
                        new("act-" + index, ToolCatalog.RequestAuthorization, """{"operation":"run_browser_command","command":{"tool":"browser.click","arguments":{"target":{"by":"role","value":"button","name":"Save"}}}}"""),
                        Navigate("https://a.test/again")],
                    "approved-after-redirect-action" => [Navigate("https://cars.test/page"), Navigate("https://b.test/page"),
                        new("act-" + index, ToolCatalog.RequestAuthorization, """{"operation":"run_browser_command","command":{"tool":"browser.click","arguments":{"target":{"by":"role","value":"button","name":"Save"}}}}"""),
                        Navigate("https://cars.test/again"), Navigate("https://www.cars.test/again")],
                    _ => [Navigate("https://a.test/page"), read] };
                if (index < actions.Length)
                {
                    if (mode == "approved-other" && index == 1)
                    {
                        Assert.DoesNotContain(request.Tools ?? [], t => t.Name == ToolCatalog.BrowserSnapshot);
                        var schema = Assert.Single(request.Tools!, t => t.Name == ToolCatalog.RequestAuthorization).ParametersJson;
                        Assert.DoesNotContain(ToolCatalog.BrowserClick, schema); Assert.Contains(ToolCatalog.BrowserNavigate, schema); Assert.Contains(ToolCatalog.BrowserClose, schema);
                    }
                    if (mode == "approved-other" && index == 2)
                    {
                        Assert.Contains(request.Tools ?? [], t => t.Name == ToolCatalog.BrowserSnapshot);
                        Assert.Contains(ToolCatalog.BrowserClick, Assert.Single(request.Tools!, t => t.Name == ToolCatalog.RequestAuthorization).ParametersJson);
                    }
                    if (mode == "approved-close" && index == 2)
                    {
                        Assert.Contains(request.Tools ?? [], t => t.Name == ToolCatalog.BrowserSnapshot);
                        Assert.DoesNotContain(request.Messages, m => m.Role == ModelRole.System && m.Text.Contains("requires human intervention", StringComparison.Ordinal));
                    }
                    yield return new ModelToolCallEvent(actions[index]); yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
                }
            }
            var count = request.Messages.Count(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.BrowserSnapshot);
            var limit = mode is "next-turn" or "approved-challenge" ? 1 : 3;
            if (!mode.StartsWith("approved-", StringComparison.Ordinal) && (count < limit || mode == "after-stop" && count == limit))
            {
                if (count == 0 && mode != "approved-challenge") Assert.Contains(request.Tools ?? [], t => t.Name == ToolCatalog.BrowserSnapshot);
                yield return new ModelToolCallEvent(new("handoff-read-" + index, ToolCatalog.BrowserSnapshot, "{}"));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            yield return new ModelSemanticResponseReady(new(NextTurn ? "Continued with current page evidence." : "Reported the bounded browser limitation.", new(ModelSpeechMode.Same, null), []));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }

    [Theory]
    [InlineData(false, "challenge")] [InlineData(true, "challenge")]
    [InlineData(false, "after-stop")] [InlineData(true, "after-stop")]
    [InlineData(false, "next-turn")] [InlineData(true, "next-turn")]
    [InlineData(false, "repeated")] [InlineData(true, "repeated")]
    [InlineData(false, "approved-challenge")] [InlineData(true, "approved-challenge")]
    [InlineData(false, "approved-other")] [InlineData(true, "approved-other")]
    [InlineData(false, "approved-redirect")] [InlineData(true, "approved-redirect")]
    [InlineData(false, "approved-close")] [InlineData(true, "approved-close")]
    [InlineData(false, "approved-after-action")] [InlineData(true, "approved-after-action")]
    [InlineData(false, "approved-after-redirect-action")] [InlineData(true, "approved-after-redirect-action")]
    public async Task Tenant_Browser_owned_read_handoff_bounds_blocked_or_repeated_evidence_and_next_user_turn_rechecks(bool sqlite, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-browser-handoff-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var browser = new HandoffReadBrowser { Challenge = mode != "repeated" }; var model = new HandoffReadModel(mode);
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
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid();
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var approved = mode.StartsWith("approved-", StringComparison.Ordinal);
            var role = await access.SaveRoleAsync(human, tenant, null, "Read owned Browser", approved
                ? ["agents.read", "browser.read", "browser.use", "sessions.read"] : ["agents.read", "browser.read", "sessions.read"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            if (approved) browser.BindTenantSession(new(tenant, instance, executor, session));
            var submitted = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Inspect the current Browser page", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var initial = submitted.GetProperty("runId").GetGuid();
            if (approved)
            {
                for (var decisions = 0; ; decisions++)
                {
                    var waiting = await AwaitBrowserRestartRun(runs, tenant, initial, true);
                    if (waiting.IsTerminal) break;
                    Assert.InRange(decisions, 0, 3); Assert.Equal(AgentRunStatus.WaitingForApproval, waiting.Status);
                    var request = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, waiting.ApprovalRequestId!.Value))!;
                    var decision = await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash,
                        expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision });
                    Assert.True(decision.IsSuccessStatusCode, await decision.Content.ReadAsStringAsync());
                    await factory.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync();
                }
            }
            var run = await AwaitBrowserRestartRun(runs, tenant, initial, false);
            Assert.Equal(mode == "after-stop" ? AgentRunStatus.Failed : AgentRunStatus.Completed, run.Status);
            Assert.Equal(mode switch { "repeated" or "approved-after-action" or "approved-after-redirect-action" => 3, "approved-other" or "approved-close" => 4, _ => 1 }, browser.Calls);
            if (mode is "approved-after-action" or "approved-after-redirect-action") Assert.Equal("https://b.test/page", browser.CurrentUrl);
            if (mode == "approved-other") Assert.Contains(model.Requests.Last().Tools ?? [], t => t.Name == ToolCatalog.BrowserSnapshot);
            if (mode is "challenge" or "repeated" or "after-stop")
            {
                var finalRequest = model.Requests.Last();
                Assert.All(finalRequest.Tools ?? [], t => Assert.Equal(AssistantResponseSchema.ResponseFunctionName, t.Name));
                Assert.Contains(finalRequest.Messages, m => m.Role == ModelRole.System && m.Text.Contains(mode == "repeated" ? "No new browser evidence" : "Do not request more browser actions", StringComparison.Ordinal));
            }
            if (mode == "next-turn")
            {
                browser.Challenge = false; model.NextTurn = true;
                var saved = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, session))!;
                var next = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Continue", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = saved.Snapshot.Revision }));
                Assert.Equal(AgentRunStatus.Completed, (await AwaitBrowserRestartRun(runs, tenant, next.GetProperty("runId").GetGuid(), false)).Status);
                Assert.Equal(2, browser.Calls);
            }
            Assert.Equal(mode switch { "approved-other" or "approved-close" or "approved-after-action" or "approved-after-redirect-action" => 3, _ => approved ? 1 : 0 }, (await factory.Services.GetRequiredService<ITenantApprovalStore>().ListApprovalsAsync(tenant, null, null, new())).Items.Count);
            var history = (await Json(await owner.GetAsync(api + $"/sessions/{session}/messages"))).GetProperty("items").EnumerateArray().ToArray();
            Assert.DoesNotContain(history, h => h.GetProperty("text").GetString()!.Contains("Do not request more browser actions", StringComparison.Ordinal));
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
