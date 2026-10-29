using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using AgentCore.Api.Http;
using AgentCore.Api.Realtime;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Contracts.Realtime;
using AgentCore.Domain.Conversation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_cancelled_source_publication_keeps_current_attachment_available_for_queued_successor(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-delivery-cancel-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var model = new TenantPrefixModel(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model) : new NativeTenantMemoryFactory(root, model);
            factory.UseKestrel(0); using var ready = factory.CreateClient(); var cookies = new CookieContainer();
            using var owner = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = ready.BaseAddress };
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var instance = (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
            await GrantNativePrivateSpaceRead(owner, tenant, instance);
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var events = new ConcurrentQueue<ServerEvent>(); await using var hub = TenantSocket(ready.BaseAddress!, cookies, events); await hub.StartAsync();
            Assert.True((await hub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(tenant, session))).Accepted);
            var attached = await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "session.ready"));
            var initial = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Hello held source",
                expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
            await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.text.delta"));
            var delivery = factory.Services.GetRequiredService<TenantSessionLiveDelivery>();
            using var cancellation = new CancellationTokenSource();
            var marker = Guid.NewGuid();
            delivery.BeforeRecipientDelivery = async (_, payload, ct) => {
                if (payload is StateChangedOutput) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
            };
            try
            {
                // Cancel only this owning source publication while it holds the recipient gate.
                // The real attachment is independently authorized for subsequent commands/output.
                var source = delivery.ObserveOutputSource(tenant, session);
                var publication = source.PublishAsync(new SessionOutput(new(marker, session, Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), null),
                    initial.GetProperty("responseId").GetGuid(), new StateChangedOutput(SessionStatus.Created, SessionMode.Text, null, "Idle", "WaitingForAgent", false, null)), cancellation.Token).AsTask();
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancellation.Cancel();
                await publication.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { cancellation.Cancel(); delivery.BeforeRecipientDelivery = null; }
            var saved = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, session))!;
            var ack = await hub.InvokeAsync<CommandAck>("SendText", new ClientCommand<UserTextPayload> { ProtocolVersion = 1, Type = "user.text",
                SessionId = session.ToString("D"), AttachmentId = attached.AttachmentId, Sequence = 1, EventId = Guid.NewGuid().ToString("D"),
                Payload = new() { Text = "Hello queued successor", Behavior = "queue", ExpectedRevision = await Revision(owner, tenant), ExpectedSessionRevision = saved.Snapshot.Revision } });
            Assert.True(ack.Accepted, ack.Error?.Code);
            model.Release.TrySetResult();
            var successor = Convert.ToString(ack.Result!["responseId"]);
            await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.response.completed" && e.ResponseId == successor));
            Assert.DoesNotContain(events, e => e.EventId == marker.ToString("D"));
            Assert.All(events, e => Assert.Equal(attached.AttachmentId, e.AttachmentId));
            Assert.Equal(0, factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
        }
        finally { model.Release.TrySetResult(); SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
