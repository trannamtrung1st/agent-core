using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Api.Http;
using AgentCore.Application.Sessions;
using AgentCore.Infrastructure.Providers;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_voice_slow_audio_authorization_does_not_occupy_control_invocation_slots(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-audio-control-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, new ScriptedLanguageModel(), s => TenantVoicePlan(s, false)) : new NativeTenantMemoryFactory(root, new ScriptedLanguageModel(), s => TenantVoicePlan(s, false));
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
            ClientCommand<T> Command<T>(string type, T payload, long sequence) => new() { ProtocolVersion = 1, Type = type, EventId = Guid.NewGuid().ToString("D"),
                SessionId = session.ToString("D"), AttachmentId = attached.AttachmentId, Sequence = sequence, Payload = payload };
            Assert.True((await hub.InvokeAsync<CommandAck>("SetMode", Command("session.mode.set", new SetModePayload { Mode = "voice" }, 1))).Accepted);
            var activated = await AwaitTenantWire(() => events.LastOrDefault(e => e.Type == "session.state.changed" && e.Payload["streamId"] is not null));
            var stream = activated.Payload["streamId"]!.ToString()!;
            var delivery = factory.Services.GetRequiredService<AgentCore.Api.Realtime.TenantSessionLiveDelivery>();
            delivery.BeforeVoiceAudioAuthorization = async ct => { entered.TrySetResult(); await held.Task.WaitAsync(ct); };
            var audio = Enumerable.Range(0, 4).Select(index => hub.InvokeAsync("SendAudio", new InputAudioDto { ProtocolVersion = 1,
                SessionId = session.ToString("D"), AttachmentId = attached.AttachmentId!, StreamId = stream,
                FrameSequence = index + 1, SampleOffset = index * 480, Data = new byte[960] })).ToArray();
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var muting = hub.InvokeAsync<CommandAck>("SetMuted", Command("session.mute", new MutePayload { Muted = true }, 2));
                var ack = await muting.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(ack.Accepted, ack.Error?.Code);
                Assert.False(held.Task.IsCompleted); // Control must complete while audio authority is still held.
                await AwaitTenantWire(() => events.LastOrDefault(e => e.Type == "session.state.changed" && e.Payload["muted"] is true));
            }
            finally
            {
                held.TrySetResult(); delivery.BeforeVoiceAudioAuthorization = null;
                await Task.WhenAll(audio).WaitAsync(TimeSpan.FromSeconds(10));
            }
            Assert.Equal(0, factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
        }
        finally { held.TrySetResult(); SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
