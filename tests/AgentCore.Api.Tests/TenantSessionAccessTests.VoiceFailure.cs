using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Realtime;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class TenantFailingRecognizer : ISpeechRecognizer
    {
        public TaskCompletionSource<bool> Fail { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RecognitionCapabilities Capabilities { get; } = new(true, true, true, true);
        public ValueTask<ISpeechRecognitionSession> OpenAsync(RecognitionOptions options, CancellationToken cancellationToken = default) => ValueTask.FromResult<ISpeechRecognitionSession>(new Stream(this));
        private sealed class Stream(TenantFailingRecognizer owner) : ISpeechRecognitionSession
        {
            public ValueTask PushAudioAsync(AudioFrame frame, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
            public ValueTask ObserveBoundaryAsync(Guid utteranceId, SpeechBoundary boundary, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
            public ValueTask CompleteInputAsync(CancellationToken cancellationToken = default) { owner.Fail.TrySetResult(false); return ValueTask.CompletedTask; }
            public ValueTask DisposeAsync() { owner.Fail.TrySetResult(false); return ValueTask.CompletedTask; }
            public async IAsyncEnumerable<SpeechRecognitionEvent> ReadEventsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            { if (await owner.Fail.Task.WaitAsync(cancellationToken)) throw new InvalidOperationException("Synthetic input worker failed."); yield break; }
        }
    }
    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_voice_failed_recognition_worker_retires_input_and_admits_a_usable_text_successor(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-voice-failure-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); var recognizer = new TenantFailingRecognizer();
        try
        {
            void Configure(IServiceCollection services)
            {
                TenantVoicePlan(services, false); var synth = new SyntheticSpeechSynthesizer();
                services.Replace(ServiceDescriptor.Singleton(new SpeechResolution { Recognizer = recognizer, Synthesizer = synth,
                    Plan = new(SpeechTransport.ServerAudio, SpeechTransport.ServerAudio, true, true, recognizer.Capabilities, synth.Capabilities) }));
            }
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, new ScriptedLanguageModel(), Configure) : new NativeTenantMemoryFactory(root, new ScriptedLanguageModel(), Configure);
            factory.UseKestrel(0); using var ready = factory.CreateClient(); var cookies = new CookieContainer();
            using var owner = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = ready.BaseAddress };
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var instance = (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var events = new ConcurrentQueue<ServerEvent>(); await using var hub = TenantSocket(ready.BaseAddress!, cookies, events); await hub.StartAsync();
            Assert.True((await hub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(tenant, session))).Accepted);
            var attachment = (await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "session.ready"))).AttachmentId;
            Assert.True((await hub.InvokeAsync<CommandAck>("SetMode", new ClientCommand<SetModePayload> { ProtocolVersion = 1, Type = "session.mode.set", SessionId = session.ToString("D"),
                AttachmentId = attachment, Sequence = 1, EventId = Guid.NewGuid().ToString("D"), Payload = new() { Mode = "voice" } })).Accepted);
            var active = await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "session.state.changed" && e.Payload["mode"]?.ToString() == "voice"));
            recognizer.Fail.TrySetResult(true);
            var error = await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "error" && e.Payload["code"]?.ToString() == "VoiceUnavailable")); Assert.NotNull(error);
            await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "session.state.changed" && e.Sequence > active.Sequence && e.Payload["mode"]?.ToString() == "text"));
            var snapshot = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, session))!.Snapshot;
            Assert.Equal(SessionMode.Text, snapshot.Mode); Assert.Empty(await factory.Services.GetRequiredService<ITenantRunStore>().ListTenantRunIdsAsync(tenant, session));
            var next = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Hello usable text", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = snapshot.Revision }));
            Assert.Equal(AgentRunStatus.Completed, (await AwaitCancelledTenantRun(factory.Services.GetRequiredService<ITenantRunStore>(), tenant, next.GetProperty("runId").GetGuid())).Status);
            await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.text.delta"));
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
