using System.Net.Http.Json;
using System.Net;
using System.Collections.Concurrent;
using AgentCore.Api.Realtime;
using AgentCore.Contracts.Realtime;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Reflection;
using AgentCore.Api.Http;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    public class VoiceAuthorityRaceStore : DispatchProxy
    {
        public IIamStore Inner = null!;
        public Guid Actor;
        public string Mode = "once";
        public int Reads, Writes;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IIamStore.ReadTenantAsync))
                return ReadAsync((Guid)args![0]!, (CancellationToken)args[1]!);
            return method.Invoke(Inner, args);
        }
        private async ValueTask<TenantSecurityState?> ReadAsync(Guid tenant, CancellationToken ct)
        {
            var value = await Inner.ReadTenantAsync(tenant, ct);
            var read = Interlocked.Increment(ref Reads);
            if (value is not null && (Mode == "churn" && read % 2 == 1 || Mode == "once" && read == 1))
            {
                Assert.True(await Inner.TrySaveTenantAsync(value with { Tenant = value.Tenant with { Revision = value.Tenant.Revision + 1 } },
                    value.Tenant.Revision, new(Guid.NewGuid(), tenant, Actor, "test.voice.authority", tenant.ToString("D"), "allowed", "fixture", DateTimeOffset.UtcNow), ct));
                Interlocked.Increment(ref Writes);
            }
            if (Mode == "denied") throw new AccessDeniedException("permission_denied");
            return value;
        }
    }

    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_voice_failed_automatic_restart_retires_faulted_stream_and_returns_to_usable_text(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-voice-restart-conflict-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        VoiceAuthorityRaceStore? race = null;
        try
        {
            void Configure(IServiceCollection services)
            {
                TenantVoicePlan(services, false);
                services.Replace(ServiceDescriptor.Singleton<TenantVoiceService>(p =>
                {
                    var proxy = DispatchProxy.Create<IIamStore, VoiceAuthorityRaceStore>(); race = (VoiceAuthorityRaceStore)(object)proxy;
                    race.Inner = p.GetRequiredService<IIamStore>(); race.Mode = "off";
                    return new(proxy, p.GetRequiredService<ITenantSessionStore>(), p.GetRequiredService<ITenantAgentStore>(),
                        p.GetRequiredService<ITenantVoiceModeStore>(), p.GetRequiredService<IdentityService>(), p.GetRequiredService<IamAuthorizer>(),
                        p.GetRequiredService<VoiceAvailability>(), p.GetRequiredService<IIdGenerator>());
                }));
            }
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, new ScriptedLanguageModel(), Configure) : new NativeTenantMemoryFactory(root, new ScriptedLanguageModel(), Configure);
            factory.UseKestrel(0); using var ready = factory.CreateClient(); var cookies = new CookieContainer();
            using var owner = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = ready.BaseAddress };
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var actor = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var instance = (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
            await GrantNativePrivateSpaceRead(owner, tenant, instance);
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var events = new ConcurrentQueue<ServerEvent>(); await using var hub = TenantSocket(ready.BaseAddress!, cookies, events); await hub.StartAsync();
            Assert.True((await hub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(tenant, session))).Accepted);
            var attached = await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "session.ready"));
            Assert.True((await hub.InvokeAsync<CommandAck>("SetMode", new ClientCommand<SetModePayload> { ProtocolVersion = 1, Type = "session.mode.set", SessionId = session.ToString("D"),
                AttachmentId = attached.AttachmentId, Sequence = 1, EventId = Guid.NewGuid().ToString("D"), Payload = new() { Mode = "voice" } })).Accepted);
            var active = await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "session.state.changed" && e.Payload["streamId"] is not null));
            var stream = active.Payload["streamId"]!.ToString()!;
            Assert.NotNull(race); race.Actor = actor; race.Reads = 0; race.Mode = "churn";
            var delivery = factory.Services.GetRequiredService<TenantSessionLiveDelivery>();
            delivery.BeforeVoiceAudioAuthorization = async ct => { entered.TrySetResult(); await held.Task.WaitAsync(ct); };
            for (var index = 0; index < 27; index++)
            {
                await hub.InvokeAsync("SendAudio", new InputAudioDto { ProtocolVersion = 1, SessionId = session.ToString("D"), AttachmentId = attached.AttachmentId!,
                    StreamId = stream, FrameSequence = index + 1, SampleOffset = index * 480, Data = new byte[960] });
                if (index == 0) await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "session.state.changed" && e.Sequence > active.Sequence && e.Payload["mode"]?.ToString() == "text"));
            Assert.Equal(3, race.Writes); Assert.False(held.Task.IsCompleted);
            race.Mode = "off"; held.TrySetResult(); delivery.BeforeVoiceAudioAuthorization = null;
            var snapshot = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, session))!.Snapshot;
            var next = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Hello after failed input restart",
                expectedRevision = await Revision(owner, tenant), expectedSessionRevision = snapshot.Revision }));
            Assert.Equal(AgentCore.Domain.Conversation.AgentRunStatus.Completed, (await AwaitCancelledTenantRun(factory.Services.GetRequiredService<ITenantRunStore>(), tenant, next.GetProperty("runId").GetGuid())).Status);
            await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.text.delta"));
            Assert.Equal(0, factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
        }
        finally { held.TrySetResult(); SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory(Timeout = 120000)]
    [InlineData(false, "once")] [InlineData(true, "once")]
    [InlineData(false, "churn")] [InlineData(true, "churn")]
    [InlineData(false, "denied")] [InlineData(true, "denied")]
    public async Task Tenant_voice_resume_rebuilds_a_conflicted_authority_witness_without_retrying_denial(bool sqlite, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-voice-authority-race-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, new ScriptedLanguageModel(), s => TenantVoicePlan(s, false)) : new NativeTenantMemoryFactory(root, new ScriptedLanguageModel(), s => TenantVoicePlan(s, false));
            using var owner = factory.CreateClient(); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var actor = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var instance = (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
            await GrantNativePrivateSpaceRead(owner, tenant, instance);
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var p = factory.Services; var identity = p.GetRequiredService<IdentityService>();
            var login = (await identity.SignInAsync("owner@example.test", "explicit-synthetic-test-password"))!;
            var original = p.GetRequiredService<TenantVoiceService>();
            await using var lease = await original.PrepareAsync(tenant, session, new(login.Account.AccountId, login.Session.SessionId, actor), Guid.NewGuid(), CancellationToken.None);
            await original.ActivateQuiescentAsync(lease, CancellationToken.None);
            var prior = lease.StreamId; await original.PauseInputAsync(lease, CancellationToken.None);
            var proxy = DispatchProxy.Create<IIamStore, VoiceAuthorityRaceStore>(); var race = (VoiceAuthorityRaceStore)(object)proxy;
            race.Inner = p.GetRequiredService<IIamStore>(); race.Actor = actor; race.Mode = mode;
            var voice = new TenantVoiceService(proxy, p.GetRequiredService<ITenantSessionStore>(), p.GetRequiredService<ITenantAgentStore>(),
                p.GetRequiredService<ITenantVoiceModeStore>(), identity, p.GetRequiredService<IamAuthorizer>(), p.GetRequiredService<VoiceAvailability>(), p.GetRequiredService<IIdGenerator>());
            if (mode == "once")
            {
                var stream = await voice.ResumeInputAsync(lease, CancellationToken.None);
                Assert.NotEqual(prior, stream); Assert.Equal(stream, lease.StreamId); Assert.True(lease.InputActive);
                Assert.Equal(4, race.Reads); Assert.Equal(1, race.Writes);
            }
            else if (mode == "churn")
            {
                var conflict = await Assert.ThrowsAsync<SecurityConflictException>(() => voice.ResumeInputAsync(lease, CancellationToken.None).AsTask());
                Assert.Equal("stale_revision", conflict.ReasonCode); Assert.Equal(6, race.Reads); Assert.Equal(3, race.Writes);
                Assert.False(lease.InputActive); Assert.Equal(prior, lease.StreamId);
            }
            else
            {
                var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => voice.ResumeInputAsync(lease, CancellationToken.None).AsTask());
                Assert.Equal("permission_denied", denied.ReasonCode); Assert.Equal(1, race.Reads); Assert.Equal(0, race.Writes);
                Assert.False(lease.InputActive); Assert.Equal(prior, lease.StreamId);
            }
            Assert.Equal(0, p.GetRequiredService<OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
