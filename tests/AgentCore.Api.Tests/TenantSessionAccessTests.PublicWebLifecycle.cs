using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using AgentCore.Api.Realtime;
using AgentCore.Application.Ports;
using AgentCore.Contracts.Realtime;
using AgentCore.Domain.Conversation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    // Successor to the unowned WebPublicToolRuntimeTests cancellation/deactivation
    // cases, using actual HTTP lifecycle commands and live MessagePack progress.
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_public_web_stop_or_shutdown_discards_late_source_and_never_completes_stale_tool_progress(bool sqlite, bool shutdown)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-public-web-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var native = new TenantPublicWebProvider { Hold = true, Noncooperative = true };
        var model = new TenantPublicWebModel("web.search", "allowed");
        try
        {
            void Configure(IServiceCollection services)
            {
                services.Replace(ServiceDescriptor.Singleton<IWebSearchProvider>(native));
                services.Replace(ServiceDescriptor.Singleton<IPublicWebFetcher>(native));
            }
            await using WebApplicationFactory<Program> factory = sqlite
                ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            factory.UseKestrel(0); using var ready = factory.CreateClient();
            var cookies = new CookieContainer();
            using var owner = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = ready.BaseAddress };
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
            var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 32, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid();
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var access = factory.Services.GetRequiredService<AgentCore.Application.Security.TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Current public source", ["agents.read", "sessions.read"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AgentCore.Domain.Security.AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            var events = new ConcurrentQueue<ServerEvent>();
            await using var hub = TenantSocket(ready.BaseAddress!, cookies, events); await hub.StartAsync();
            Assert.True((await hub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(tenant, session))).Accepted);
            var admission = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Search the controlled public source", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
            var id = admission.GetProperty("runId").GetGuid(); var response = admission.GetProperty("responseId").GetGuid().ToString("D");
            await native.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var progress = await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.progress" && e.ResponseId == response
                && Equals(e.Payload["kind"], "runningTool") && Equals(e.Payload["state"], "started")));
            Assert.True(Guid.TryParse(progress.Payload["operationId"] as string, out _));
            Assert.Equal(AgentCore.Application.Events.ResponseProgressMessages.RunningTools, progress.Payload["message"]);
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var running = (await runs.FindTenantRunAsync(tenant, id))!;
            var host = factory.Services.GetRequiredService<TenantSessionRuntimeHost>();
            if (shutdown) await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(20));
            else
            {
                var command = await owner.PostAsJsonAsync(api + $"/agent-runs/{id}/cancel", new { expectedRevision = await Revision(owner, tenant), expectedRunRevision = running.Revision });
                Assert.Equal(HttpStatusCode.OK, command.StatusCode);
            }
            native.Release.TrySetResult(); await native.Returned.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await factory.Services.GetRequiredService<TenantSessionRuntimeHost>().WaitForGenerationReleaseAsync(session, id, running.Claim!.Generation, CancellationToken.None);
            // Host retirement releases the generation without claiming a user Stop.
            var done = shutdown ? (await runs.FindTenantRunAsync(tenant, id))! : await AwaitCancelledTenantRun(runs, tenant, id);
            Assert.NotEqual(AgentRunStatus.Completed, done.Status);
            if (!shutdown) Assert.Equal(AgentRunStatus.Cancelled, done.Status);
            Assert.Equal(1, native.Calls); Assert.Single(model.Requests);
            Assert.DoesNotContain(events, e => e.Type == "agent.progress" && e.ResponseId == response
                && Equals(e.Payload["kind"], "runningTool") && Equals(e.Payload["state"], "completed"));
            Assert.DoesNotContain(events, e => e.Type == "agent.response.completed" && e.ResponseId == response);
            var saved = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, session))!.Snapshot;
            Assert.All(saved.Entries, e => Assert.DoesNotContain(TenantPublicWebProvider.Witness, e.Text));
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { native.Release.TrySetResult(); SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
