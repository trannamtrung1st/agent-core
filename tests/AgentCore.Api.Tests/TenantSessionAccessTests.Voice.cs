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
    private static void TenantVoicePlan(IServiceCollection services, bool client)
    {
        var recognizer = client ? null : new SyntheticSpeechRecognizer();
        var synthesizer = client ? null : new SyntheticSpeechSynthesizer();
        services.Configure<AgentCore.Api.Realtime.AgentCoreOptions>(o => o.MaxActiveSessions = 1);
        services.Replace(ServiceDescriptor.Singleton(new SpeechResolution {
            Recognizer = recognizer, Synthesizer = synthesizer,
            Plan = new(client ? SpeechTransport.ClientTranscript : SpeechTransport.ServerAudio,
                client ? SpeechTransport.ClientSpeech : SpeechTransport.ServerAudio, true, true,
                recognizer?.Capabilities ?? ClientSpeechCapabilities.Recognition, synthesizer?.Capabilities ?? ClientSpeechCapabilities.Synthesis) }));
    }
    [Theory(Timeout = 120000)] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_voice_real_JavaScript_MessagePack_uses_owned_server_and_client_transports(bool sqlite, bool client)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-voice-wire-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, new ScriptedLanguageModel(), s => TenantVoicePlan(s, client)) : new NativeTenantMemoryFactory(root, new ScriptedLanguageModel(), s => TenantVoicePlan(s, client));
            factory.UseKestrel(0); using var ready = factory.CreateClient();
            var repo = new DirectoryInfo(AppContext.BaseDirectory); while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "AgentCore.sln"))) repo = repo.Parent; Assert.NotNull(repo);
            var start = new System.Diagnostics.ProcessStartInfo("node", "tenant-voice.mjs") { WorkingDirectory = Path.Combine(repo.FullName, "tests", "realtime-js"), UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["BASE_URL"] = ready.BaseAddress!.ToString().TrimEnd('/'); start.Environment["VOICE_TRANSPORT"] = client ? "client" : "server";
            using var process = System.Diagnostics.Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
            try { await process.WaitForExitAsync(timeout.Token); }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
            Assert.True(process.ExitCode == 0, await stdout + await stderr); Assert.Contains("Tenant voice wire passed", await stdout);
            Assert.Equal(0, factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_voice_actual_human_turn_audio_heard_receipt_and_fresh_unmute_preserve_Run_ownership(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-voice-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, new ScriptedLanguageModel(), s => TenantVoicePlan(s, false)) : new NativeTenantMemoryFactory(root, new ScriptedLanguageModel(), s => TenantVoicePlan(s, false));
            factory.UseKestrel(0); using var ready = factory.CreateClient(); var cookies = new CookieContainer();
            using var owner = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = ready.BaseAddress };
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var principal = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var instance = (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) })))
                .GetProperty("instance").GetProperty("instanceId").GetGuid();
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var events = new ConcurrentQueue<ServerEvent>(); var audio = new ConcurrentQueue<OutputAudioDto>();
            await using var hub = TenantSocket(ready.BaseAddress!, cookies, events); hub.On<OutputAudioDto>("AudioOutput", audio.Enqueue); await hub.StartAsync();
            Assert.True((await hub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(tenant, session))).Accepted);
            var attached = await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "session.ready")); var attachment = attached.AttachmentId!; long sequence = 0;
            ClientCommand<T> Command<T>(string type, T payload, string? response = null) => new() { ProtocolVersion = 1, Type = type, EventId = Guid.NewGuid().ToString("D"),
                SessionId = session.ToString("D"), AttachmentId = attachment, Sequence = ++sequence, ResponseId = response, Payload = payload };
            async Task Invoke<T>(string method, ClientCommand<T> command, bool expected = true)
            {
                var ack = await hub.InvokeAsync<CommandAck>(method, command);
                Assert.True(ack.Accepted == expected, $"{method}: {ack.Accepted}; {ack.Error?.Code}: {ack.Error?.Message}");
            }
            await Invoke("SetMode", Command("session.mode.set", new SetModePayload { Mode = "voice" }));
            var activated = await AwaitTenantWire(() => events.LastOrDefault(e => e.Type == "session.state.changed" && e.Payload["mode"]?.ToString() == "voice" && e.Payload["streamId"] is not null));
            var stream = activated.Payload["streamId"]!.ToString()!;
            var observerEvents = new ConcurrentQueue<ServerEvent>(); var observerAudio = new ConcurrentQueue<OutputAudioDto>();
            await using var observer = TenantSocket(ready.BaseAddress!, cookies, observerEvents); observer.On<OutputAudioDto>("AudioOutput", observerAudio.Enqueue); await observer.StartAsync();
            Assert.True((await observer.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(tenant, session))).Accepted);
            var observed = await AwaitTenantWire(() => observerEvents.FirstOrDefault(e => e.Type == "session.ready"));
            var steal = new ClientCommand<SetModePayload> { ProtocolVersion = 1, Type = "session.mode.set", EventId = Guid.NewGuid().ToString("D"), SessionId = session.ToString("D"),
                AttachmentId = observed.AttachmentId, Sequence = 1, Payload = new() { Mode = "voice" } };
            Assert.False((await observer.InvokeAsync<CommandAck>("SetMode", steal)).Accepted);
            async Task<ServerEvent> Speak(string inputStream)
            {
                var utterance = Guid.NewGuid();
                await Invoke("SpeechStarted", Command("user.speech.started", new SpeechStartedPayload { StreamId = inputStream, UtteranceId = utterance.ToString("D"), SampleOffset = 0, ActivityScore = .9 }));
                await hub.InvokeAsync("SendAudio", new InputAudioDto { ProtocolVersion = 1, SessionId = session.ToString("D"), AttachmentId = attachment, StreamId = inputStream,
                    FrameSequence = 1, SampleOffset = 0, Data = new byte[960] });
                await Invoke("SpeechEnded", Command("user.speech.ended", new SpeechEndedPayload { StreamId = inputStream, UtteranceId = utterance.ToString("D"), SampleOffset = 480, ActivityScore = .9, DurationMs = 300 }));
                return await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "transcript.final" && e.Payload["utteranceId"]?.ToString() == utterance.ToString("D")));
            }
            var transcript = await Speak(stream);
            Assert.Equal("Hello there", transcript.Payload["text"]);
            var started = await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.response.started")); var response = started.ResponseId!;
            var lastAudio = await AwaitTenantWire(() => audio.FirstOrDefault(a => a.ResponseId == response && a.IsFinal));
            Assert.All(audio, a => { Assert.Equal(attachment, a.AttachmentId); Assert.Equal(session.ToString("D"), a.SessionId); });
            var speech = await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.speech.projection" && e.ResponseId == response));
            var speechLength = speech.Payload["text"]!.ToString()!.Length; Assert.True(speechLength > 0);
            var samples = lastAudio.SampleOffset + lastAudio.Data.Length / 2;
            await Invoke("PlaybackProgress", Command("playback.progress", new PlaybackPayload { ConsumedSamples = samples + 1, TextEndExclusive = speechLength }, response), false);
            await Invoke("PlaybackCompleted", Command("playback.completed", new PlaybackPayload { ConsumedSamples = samples, TextEndExclusive = speechLength }, response));
            await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.response.completed" && e.ResponseId == response));
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var runId = Guid.Parse(started.Payload["agentRunId"]!.ToString()!);
            var first = await AwaitCancelledTenantRun(runs, tenant, runId); Assert.Equal(AgentRunStatus.Completed, first.Status);
            Assert.Equal(principal, first.Admission.ExecutionContext!.OriginalInitiatorPrincipalId);
            var history = (await factory.Services.GetRequiredService<ITenantConversationStore>().ReadTenantConversationAsync(tenant, session, null, null, 100))!.History.Items;
            var input = history.Single(e => e.EntryId.ToString("D") == transcript.Payload["entryId"]!.ToString());
            Assert.Equal(SessionMode.Voice, input.DeliveryMode); Assert.Equal(input.Sequence.ToString(), transcript.Payload["entrySequence"]!.ToString());
            Assert.Equal(speechLength, history.Single(e => e.ResponseId?.ToString("D") == response).HeardTextEndExclusive);
            Assert.Empty(observerAudio); Assert.DoesNotContain(observerEvents, e => e.Type == "agent.speech.projection");
            await Invoke("SetMuted", Command("session.mute", new MutePayload { Muted = true }));
            await Invoke("SpeechStarted", Command("user.speech.started", new SpeechStartedPayload { StreamId = stream, UtteranceId = Guid.NewGuid().ToString("D") }), false);
            await Invoke("SetMuted", Command("session.mute", new MutePayload { Muted = false }));
            var fresh = await AwaitTenantWire(() => events.LastOrDefault(e => e.Type == "session.state.changed" && e.Payload["streamId"] is not null && e.Payload["streamId"]!.ToString() != stream));
            var nextStream = fresh.Payload["streamId"]!.ToString()!;
            await Invoke("SpeechStarted", Command("user.speech.started", new SpeechStartedPayload { StreamId = stream, UtteranceId = Guid.NewGuid().ToString("D") }), false);
            var secondTranscript = await Speak(nextStream);
            var secondStarted = await AwaitTenantWire(() => events.LastOrDefault(e => e.Type == "agent.response.started" && e.ResponseId != response));
            await Invoke("SetMode", Command("session.mode.set", new SetModePayload { Mode = "text" }));
            var second = await AwaitCancelledTenantRun(runs, tenant, Guid.Parse(secondStarted.Payload["agentRunId"]!.ToString()!)); Assert.Equal(AgentRunStatus.Completed, second.Status);
            Assert.NotEqual(first.ActivationId, second.ActivationId); Assert.Equal(second.ActivationId, second.Admission.ExecutionContext!.RootActivationId);
            Assert.Equal(principal, second.Admission.ExecutionContext.OriginalInitiatorPrincipalId);
            Assert.Equal(SessionMode.Text, (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, session))!.Snapshot.Mode);
            Assert.Equal(0, factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
