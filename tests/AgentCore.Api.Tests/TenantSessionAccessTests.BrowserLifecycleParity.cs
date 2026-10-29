using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Api.Realtime;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Contracts.Realtime;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Browser;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class LifecycleReadBrowser : IBrowser, IBrowserRuntimeReadiness, ITenantBrowserCommandTarget, ITenantBrowserBinding
    {
        private readonly HandoffReadBrowser inner = new() { Challenge = false };
        public ConcurrentQueue<BrowserRequest> Requests { get; } = new();
        public BrowserProviderDescriptor Provider => inner.Provider;
        public BrowserHostPolicy HostPolicy => inner.HostPolicy;
        public bool IsAvailable => true;
        public bool IsRuntimeReady => true;
        public bool Unavailable;
        public string? Hold;
        public bool ChangingReads { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void BindTenantSession(TenantBrowserOwner owner) => inner.BindTenantSession(owner);
        public ValueTask<string?> EnsureTenantIsolationAsync(TenantBrowserOwner owner, BrowserProfileMode mode, CancellationToken ct = default) => inner.EnsureTenantIsolationAsync(owner, mode, ct);
        public ValueTask<string?> BindTenantIsolationAsync(TenantBrowserOwner owner, BrowserProfileMode mode, CancellationToken ct = default) => inner.BindTenantIsolationAsync(owner, mode, ct);
        public ValueTask ReleaseTenantAsync(TenantBrowserOwner owner, CancellationToken ct = default) => inner.ReleaseTenantAsync(owner, ct);
        public ValueTask<TenantBrowserCommandPreparation> PrepareTenantCommandAsync(TenantBrowserOwner owner, BrowserCommand command, CancellationToken ct = default) => inner.PrepareTenantCommandAsync(owner, command, ct);
        public async ValueTask<BrowserResult> ExecuteAsync(BrowserRequest request, CancellationToken ct = default)
        {
            Requests.Enqueue(request);
            if (Unavailable && request.Command is BrowserNavigate)
            {
                Assert.NotNull(request.TenantOwner); Assert.NotNull(request.TenantWitness); Assert.NotNull(request.TenantDispatchCheck);
                if (await request.TenantDispatchCheck(ct) is { } denied) return new(denied);
                return new("provider_unavailable");
            }
            var result = await inner.ExecuteAsync(request, ct);
            var held = Hold == "navigate" && request.Command is BrowserNavigate || Hold == "observe" && request.Command is BrowserObserve;
            if (held) { Entered.TrySetResult(); await Release.Task; }
            if (result.Observation is { } observation)
                result = result with { Observation = observation with { Title = held ? "LATE-PAGE" : observation.Title,
                    Content = held ? "LATE-PAGE private evidence" : ChangingReads ? "Changing owned page " + inner.Calls : observation.Content } };
            if (held) Returned.TrySetResult();
            return result;
        }
    }

    private sealed class LifecycleNavigationModel(bool readAfterNavigation = false) : ILanguageModel
    {
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield(); ct.ThrowIfCancellationRequested(); Requests.Enqueue(request);
            var next = request.Messages.LastOrDefault(m => m.Role == ModelRole.User)?.Text.Contains("next", StringComparison.OrdinalIgnoreCase) == true;
            var navigation = request.Messages.LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization);
            if (!next && navigation is null)
            {
                Assert.Contains(request.Tools!, t => t.Name == ToolCatalog.RequestAuthorization);
                yield return new ModelToolCallEvent(new("owned-navigation", ToolCatalog.RequestAuthorization,
                    """{"operation":"run_browser_command","command":{"tool":"browser.navigate","arguments":{"url":"https://a.test/page"}}}"""));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            if (!next && readAfterNavigation && !request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.BrowserSnapshot))
            {
                yield return new ModelToolCallEvent(new("post-navigation-observation", ToolCatalog.BrowserSnapshot, "{}"));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            var answer = next ? "Still here." : navigation!.Text.Contains("\"commandCompleted\":true", StringComparison.Ordinal)
                ? "Checked the record." : "The Browser result is unavailable; it was not repeated.";
            yield return new ModelSemanticResponseReady(new(answer, new(ModelSpeechMode.None, null), []));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public Task Tenant_owned_Browser_completed_or_unavailable_navigation_retains_history_after_reconnect_and_next_turn(bool sqlite, bool unavailable) =>
        BrowserLifecycleParity(sqlite, unavailable ? "unavailable" : "reconnect");

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public Task Tenant_owned_Browser_Stop_during_navigation_or_exact_steer_during_observation_withholds_late_page_and_never_navigates_again(bool sqlite, bool steer) =>
        BrowserLifecycleParity(sqlite, steer ? "steer" : "stop");

    private static async Task BrowserLifecycleParity(bool sqlite, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-browser-lifecycle-parity-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var browser = new LifecycleReadBrowser { Unavailable = mode == "unavailable", Hold = mode == "stop" ? "navigate" : mode == "steer" ? "observe" : null };
        try
        {
            var model = new LifecycleNavigationModel(mode == "steer");
            void Configure(IServiceCollection services)
            { ((BrowserOptions)services.Single(d => d.ServiceType == typeof(BrowserOptions)).ImplementationInstance!).Enabled = true; services.Replace(ServiceDescriptor.Singleton<IBrowser>(browser)); }
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            try
            {
            factory.UseKestrel(0); using var ready = factory.CreateClient(); var cookies = new CookieContainer();
            using var owner = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = ready.BaseAddress };
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 36, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
            await GrantNativePrivateSpaceRead(owner, tenant, instance); var executor = agent.GetProperty("principalId").GetGuid();
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Actual Browser lifecycle", ["agents.read", "sessions.read", "browser.read", "browser.use"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant)); browser.BindTenantSession(new(tenant, instance, executor, session));
            var events = new ConcurrentQueue<ServerEvent>(); await using var hub = TenantSocket(ready.BaseAddress!, cookies, events); await hub.StartAsync();
            Assert.True((await hub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(tenant, session))).Accepted);
            var sent = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Open the record", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
            var source = sent.GetProperty("runId").GetGuid(); var runs = factory.Services.GetRequiredService<ITenantRunStore>();
            var waiting = await AwaitBrowserRestartRun(runs, tenant, source, true); Assert.Equal(AgentRunStatus.WaitingForApproval, waiting.Status);
            var approval = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, waiting.ApprovalRequestId!.Value))!;
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{approval.RequestId}/decision", new { decision = "approve", actionHash = approval.Operation.ActionHash,
                expectedRevision = await Revision(owner, tenant), expectedRequestRevision = approval.Revision })).StatusCode);
            Guid? replacement = null;
            if (mode is "stop" or "steer")
            {
                await browser.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                var running = (await CurrentApprovalExecution(runs, tenant, source))!; Assert.Equal(AgentRunStatus.Running, running.Status);
                var count = model.Requests.Count;
                if (mode == "stop") Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/agent-runs/{running.AgentRunId}/cancel", new { expectedRevision = await Revision(owner, tenant), expectedRunRevision = running.Revision })).StatusCode);
                else
                {
                    var current = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, session))!;
                    var steered = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "next", behavior = "interrupt", interruptedRunId = running.AgentRunId,
                        expectedRunRevision = running.Revision, expectedRevision = await Revision(owner, tenant), expectedSessionRevision = current.Snapshot.Revision });
                    Assert.Equal(HttpStatusCode.Accepted, steered.StatusCode); replacement = (await Json(steered)).GetProperty("runId").GetGuid();
                }
                browser.Release.TrySetResult(); await browser.Returned.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await factory.Services.GetRequiredService<TenantSessionRuntimeHost>().WaitForGenerationReleaseAsync(session, running.AgentRunId, running.Claim!.Generation, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
                Assert.DoesNotContain(model.Requests.Skip(count), r => r.Messages.Any(m => m.Role == ModelRole.Tool && m.Text.Contains("LATE-PAGE", StringComparison.Ordinal)));
            }
            else
            {
                var done = await AwaitBrowserRestartRun(runs, tenant, source, false); Assert.Equal(AgentRunStatus.Completed, done.Status);
                var expected = mode == "unavailable" ? "The Browser result is unavailable; it was not repeated." : "Checked the record.";
                var history = (await Json(await owner.GetAsync(api + $"/sessions/{session}/messages"))).GetProperty("items");
                var answer = Assert.Single(history.EnumerateArray(), e => e.GetProperty("role").GetString() == "assistant" && e.GetProperty("text").GetString() == expected);
                var calls = model.Requests.Count;
                await hub.StopAsync(); var again = new ConcurrentQueue<ServerEvent>(); await using var reconnected = TenantSocket(ready.BaseAddress!, cookies, again); await reconnected.StartAsync();
                Assert.True((await reconnected.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(tenant, session))).Accepted);
                var snapshot = await AwaitTenantWire(() => again.FirstOrDefault(e => e.Type == "session.ready"));
                var payload = JsonSerializer.SerializeToElement(snapshot.Payload);
                Assert.Single(payload.GetProperty("history").EnumerateArray(), e => e.GetProperty("text").GetString() == expected);
                Assert.Equal(calls, model.Requests.Count); Assert.Single(browser.Requests, r => r.Command is BrowserNavigate);
            }
            if (replacement is null)
            {
                var current = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, session))!;
                var next = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "next", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = current.Snapshot.Revision }));
                replacement = next.GetProperty("runId").GetGuid();
            }
            Assert.Equal(AgentRunStatus.Completed, (await AttachmentTerminal(runs, tenant, replacement.Value)).Status);
            var retained = (await Json(await owner.GetAsync(api + $"/sessions/{session}/messages"))).GetProperty("items");
            Assert.Contains(retained.EnumerateArray(), e => e.GetProperty("role").GetString() == "assistant" && e.GetProperty("text").GetString() == "Still here.");
            Assert.DoesNotContain("LATE-PAGE", retained.GetRawText()); Assert.Single(browser.Requests, r => r.Command is BrowserNavigate);
            var receipt = await factory.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, approval.Operation.OperationId); Assert.NotNull(receipt);
            Assert.Equal(mode == "stop" ? TenantOperationOutcome.Indeterminate : mode == "unavailable" ? TenantOperationOutcome.Denied : TenantOperationOutcome.Completed, receipt.Outcome);
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
            }
            finally { browser.Release.TrySetResult(); }
        }
        finally { browser.Release.TrySetResult(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
