using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using AgentCore.Api.Http;
using AgentCore.Api.Realtime;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Contracts.Realtime;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Tenant_render_receipt_after_durable_completion_does_not_cancel_terminal_publication_or_retire_attachment(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-terminal-delivery-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var blocked = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, new ScriptedLanguageModel()) : new NativeTenantMemoryFactory(root, new ScriptedLanguageModel());
            factory.UseKestrel(0); using var ready = factory.CreateClient();
            var delivery = factory.Services.GetRequiredService<TenantSessionLiveDelivery>();
            var host = factory.Services.GetRequiredService<TenantSessionRuntimeHost>();
            delivery.BeforeRecipientDelivery = async (_, payload, ct) => {
                if (payload is not ResponseCompletedOutput) return;
                blocked.TrySetResult(ct); await release.Task.WaitAsync(ct);
            };
            host.RuntimeRetirementObserved = _ => retired.TrySetResult();
            try
            {
                var cookies = new CookieContainer(); using var owner = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = ready.BaseAddress };
                await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
                var principal = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
                var instance = (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
                var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
                var events = new ConcurrentQueue<ServerEvent>(); await using var hub = TenantSocket(ready.BaseAddress!, cookies, events); await hub.StartAsync();
                Assert.True((await hub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(tenant, session))).Accepted);
                var admission = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Complete before receipt", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
                var response = admission.GetProperty("responseId").GetGuid(); var runId = admission.GetProperty("runId").GetGuid();
                var token = await blocked.Task.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.Equal(AgentRunStatus.Completed, (await factory.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, runId))!.Status);
                var started = events.Single(e => e.Type == "agent.response.started");
                var auth = (await factory.Services.GetRequiredService<IdentityService>().SignInAsync("owner@example.test", "explicit-synthetic-test-password"))!;
                var saved = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, session))!;
                var receipt = host.ReceiveAsync(new(saved.Owner, new(auth.Account.AccountId, auth.Session.SessionId, principal), await Revision(owner, tenant),
                    Guid.Parse((string)started.Payload["entryId"]!), Convert.ToInt64(started.Payload["entrySequence"]), response, 21, 21, [])).AsTask();
                await retired.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.False(token.IsCancellationRequested);
                Assert.False(receipt.IsCompleted); // The owning Runtime still has the publication gate.
                release.TrySetResult(); await receipt.WaitAsync(TimeSpan.FromSeconds(15));
                await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.response.completed" && e.ResponseId == response.ToString("D")));
                var history = (await factory.Services.GetRequiredService<ITenantConversationStore>().ReadTenantConversationAsync(tenant, session, null, null, 50))!;
                Assert.Equal(21, history.History.Items.Single(e => e.ResponseId == response).ReceivedTextEndExclusive);
                var next = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Keep this attachment alive", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = history.SessionRevision });
                Assert.Equal(HttpStatusCode.Accepted, next.StatusCode);
                var second = (await Json(next)).GetProperty("responseId").GetString();
                await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.response.completed" && e.ResponseId == second));
                Assert.All(events, e => Assert.Equal(started.AttachmentId, e.AttachmentId));
                Assert.Equal(0, factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
            }
            finally { release.TrySetResult(); delivery.BeforeRecipientDelivery = null; host.RuntimeRetirementObserved = null; }
        }
        finally { release.TrySetResult(); SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
