using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Contracts.Realtime;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory(Timeout = 120000)]
    [InlineData(false, "apply")] [InlineData(true, "apply")]
    [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    [InlineData(false, "timeout")] [InlineData(true, "timeout")]
    public async Task Tenant_voice_pending_activation_waits_actual_Run_and_cancel_or_timeout_preserves_its_work(bool sqlite, string boundary)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-voice-pending-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); var model = new TenantPrefixModel();
        try
        {
            void Configure(IServiceCollection s) { TenantVoicePlan(s, false); if (boundary == "timeout") s.Replace(ServiceDescriptor.Singleton(new InteractionPolicy(PendingVoiceTimeoutMs: 500))); }
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            factory.UseKestrel(0); using var ready = factory.CreateClient(); var cookies = new CookieContainer();
            using var owner = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = ready.BaseAddress };
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var instance = (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var events = new ConcurrentQueue<ServerEvent>(); await using var hub = TenantSocket(ready.BaseAddress!, cookies, events); await hub.StartAsync();
            Assert.True((await hub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(tenant, session))).Accepted);
            var readyEvent = await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "session.ready")); long sequence = 0;
            ClientCommand<T> Command<T>(string type, T payload) => new() { ProtocolVersion = 1, Type = type, EventId = Guid.NewGuid().ToString("D"), SessionId = session.ToString("D"), AttachmentId = readyEvent.AttachmentId, Sequence = ++sequence, Payload = payload };
            var admitted = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Hello held text", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 })); var runId = admitted.GetProperty("runId").GetGuid();
            await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.text.delta"));
            var requested = await hub.InvokeAsync<CommandAck>("SetMode", Command("session.mode.set", new SetModePayload { Mode = "voice" })); Assert.True(requested.Accepted, requested.Error?.Code);
            var pending = await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "session.state.changed" && e.Payload["pendingMode"]?.ToString() == "voice"));
            Assert.Equal("text", pending.Payload["mode"]); Assert.Null(pending.Payload["streamId"]);
            var premature = await hub.InvokeAsync<CommandAck>("SpeechStarted", Command("user.speech.started", new SpeechStartedPayload { StreamId = Guid.NewGuid().ToString("D"), UtteranceId = Guid.NewGuid().ToString("D") })); Assert.False(premature.Accepted);
            var runs = factory.Services.GetRequiredService<ITenantRunStore>();
            if (boundary == "cancel")
            {
                var cancelled = await hub.InvokeAsync<CommandAck>("SetMode", Command("session.mode.set", new SetModePayload { Mode = "text" })); Assert.True(cancelled.Accepted, cancelled.Error?.Code);
            }
            if (boundary == "timeout") await AwaitTenantWire(() => events.LastOrDefault(e => e.Type == "session.state.changed" && e.Sequence > pending.Sequence && e.Payload["pendingMode"] is null));
            Assert.Equal(AgentRunStatus.Running, (await runs.FindTenantRunAsync(tenant, runId))!.Status);
            model.Release.TrySetResult(); Assert.Equal(AgentRunStatus.Completed, (await AwaitCancelledTenantRun(runs, tenant, runId)).Status);
            if (boundary == "apply")
            {
                var activated = await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "session.state.changed" && e.Payload["mode"]?.ToString() == "voice")); Assert.NotNull(activated.Payload["streamId"]);
                var off = await hub.InvokeAsync<CommandAck>("SetMode", Command("session.mode.set", new SetModePayload { Mode = "text" })); Assert.True(off.Accepted, off.Error?.Code);
            }
            var snapshot = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, session))!.Snapshot;
            Assert.Equal(SessionMode.Text, snapshot.Mode); Assert.Null(snapshot.PendingMode);
            Assert.DoesNotContain(events, e => e.Type == "agent.response.interrupted");
        }
        finally { model.Release.TrySetResult(); SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact(Timeout = 120000)]
    public async Task Tenant_voice_orphaned_SQLite_mode_requires_fresh_activation_after_actual_host_recreation()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-voice-reopen-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); Guid tenant, session;
        try
        {
            await using (var first = new NativeTenantSqliteFactory(root, new ScriptedLanguageModel(), s => TenantVoicePlan(s, false)))
            {
                using var owner = first.CreateClient(); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
                var principal = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
                var instance = (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
                session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
                var login = (await first.Services.GetRequiredService<IdentityService>().SignInAsync("owner@example.test", "explicit-synthetic-test-password"))!;
                var voice = first.Services.GetRequiredService<TenantVoiceService>();
                var orphan = await voice.PrepareAsync(tenant, session, new(login.Account.AccountId, login.Session.SessionId, principal), Guid.NewGuid(), CancellationToken.None);
                Assert.Equal(SessionMode.Voice, (await voice.ActivateQuiescentAsync(orphan, CancellationToken.None)).Snapshot.Mode);
                // Deliberately leave only the durable mode; no transport or lease is persisted or registered.
            }
            await using (var second = new NativeTenantSqliteFactory(root, new ScriptedLanguageModel(), s => TenantVoicePlan(s, false)))
            {
                second.UseKestrel(0); using var ready = second.CreateClient(); var cookies = new CookieContainer();
                using var owner = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = ready.BaseAddress };
                await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                var events = new ConcurrentQueue<ServerEvent>(); await using var hub = TenantSocket(ready.BaseAddress!, cookies, events); await hub.StartAsync();
                Assert.True((await hub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(tenant, session))).Accepted);
                var restored = await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "session.ready"));
                Assert.Equal("text", restored.Payload["mode"]); Assert.Null(restored.Payload["pendingMode"]); Assert.Null(restored.Payload["streamId"]);
                Assert.Empty((await second.Services.GetRequiredService<ITenantRunStore>().ListTenantRunIdsAsync(tenant, session)));
            }
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
