using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Contracts.Realtime;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class ToolIsolationSynthesizer : ISpeechSynthesizer
    {
        private readonly SyntheticSpeechSynthesizer inner = new();
        public ConcurrentQueue<SpeechRequest> Requests { get; } = new();
        public SynthesisCapabilities Capabilities => inner.Capabilities;
        public IAsyncEnumerable<SpeechSynthesisEvent> SynthesizeAsync(SpeechRequest request, CancellationToken ct = default)
        { Requests.Enqueue(request); return inner.SynthesizeAsync(request, ct); }
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_actual_owned_image_tool_progress_is_visible_but_never_enters_TTS_or_client_speech(bool sqlite, bool clientSpeech)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-voice-tool-isolation-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new AttachmentReadModel(); var synthesis = new ToolIsolationSynthesizer();
            void Configure(IServiceCollection services)
            {
                TenantVoicePlan(services, clientSpeech);
                if (!clientSpeech)
                {
                    var current = (SpeechResolution)services.Last(d => d.ServiceType == typeof(SpeechResolution)).ImplementationInstance!;
                    services.Replace(ServiceDescriptor.Singleton(new SpeechResolution { Plan = current.Plan, Recognizer = current.Recognizer, Synthesizer = synthesis }));
                }
            }
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            factory.UseKestrel(0); using var ready = factory.CreateClient(); var cookies = new CookieContainer();
            using var owner = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = ready.BaseAddress };
            var value = await PrepareAttachmentRead(factory.Services, owner, "png"); var v = value.Execution;
            var runs = factory.Services.GetRequiredService<ITenantRunStore>();
            var warmup = await factory.Services.GetRequiredService<TenantRunService>().SubmitMessageAsync(v.Tenant, v.Session, v.Human, Guid.NewGuid(), "Keep this earlier image", [v.Id], await Revision(owner, v.Tenant), 1);
            Assert.Equal(AgentRunStatus.Completed, (await AttachmentTerminal(runs, v.Tenant, warmup.RunId)).Status);
            model.Requests.Clear(); model.ReadEnabled = true; model.Arguments = JsonSerializer.Serialize(new { attachmentId = v.Id });
            var events = new ConcurrentQueue<ServerEvent>(); var audio = new ConcurrentQueue<OutputAudioDto>();
            await using var hub = TenantSocket(ready.BaseAddress!, cookies, events); hub.On<OutputAudioDto>("AudioOutput", audio.Enqueue); await hub.StartAsync();
            Assert.True((await hub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(v.Tenant, v.Session))).Accepted);
            var attached = await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "session.ready"));
            var mode = new ClientCommand<SetModePayload> { ProtocolVersion = 1, Type = "session.mode.set", EventId = Guid.NewGuid().ToString("D"), SessionId = v.Session.ToString("D"), AttachmentId = attached.AttachmentId,
                Sequence = 1, Payload = new() { Mode = "voice" } };
            var modeAck = await hub.InvokeAsync<CommandAck>("SetMode", mode); Assert.True(modeAck.Accepted, modeAck.Error?.Message);
            await AwaitTenantWire(() => events.LastOrDefault(e => e.Type == "session.state.changed" && e.Payload["mode"]?.ToString() == "voice" && e.Payload["streamId"] is not null));
            var saved = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(v.Tenant, v.Session))!;
            var sent = await Json(await owner.PostAsJsonAsync($"/api/v2/tenants/{v.Tenant}/sessions/{v.Session}/messages", new { messageId = Guid.NewGuid(), text = "Read the exact earlier image",
                expectedRevision = await Revision(owner, v.Tenant), expectedSessionRevision = saved.Snapshot.Revision }));
            var response = sent.GetProperty("responseId").GetGuid().ToString("D");
            await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.progress" && e.ResponseId == response && Equals(e.Payload["kind"], "runningTool") && Equals(e.Payload["state"], "started")));
            var projection = await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.speech.projection" && e.ResponseId == response));
            var spoken = projection.Payload["text"]!.ToString()!; Assert.Contains("observed sanitized image", spoken);
            long samples;
            if (clientSpeech)
            {
                await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "speech.output.completed" && e.ResponseId == response)); samples = 0;
                Assert.Contains(events, e => e.Type == "speech.output.segment" && e.ResponseId == response);
                Assert.Empty(synthesis.Requests);
            }
            else
            {
                var final = await AwaitTenantWire(() => audio.FirstOrDefault(a => a.ResponseId == response && a.IsFinal)); samples = final.SampleOffset + final.Data.Length / 2;
                Assert.NotEmpty(synthesis.Requests);
            }
            var playback = new ClientCommand<PlaybackPayload> { ProtocolVersion = 1, Type = "playback.completed", EventId = Guid.NewGuid().ToString("D"), SessionId = v.Session.ToString("D"), AttachmentId = attached.AttachmentId,
                Sequence = 2, ResponseId = response, Payload = new() { ConsumedSamples = samples, TextEndExclusive = spoken.Length } };
            var ack = await hub.InvokeAsync<CommandAck>("PlaybackCompleted", playback); Assert.True(ack.Accepted, ack.Error?.Message);
            await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.response.completed" && e.ResponseId == response));
            var done = await AttachmentTerminal(runs, v.Tenant, sent.GetProperty("runId").GetGuid()); Assert.Equal(AgentRunStatus.Completed, done.Status); Assert.Equal(2, model.Requests.Count);
            var speechInputs = synthesis.Requests.Select(r => r.Text).Concat(events.Where(e => e.Type == "speech.output.segment").Select(e => e.Payload["text"]!.ToString()!)).Append(spoken);
            foreach (var text in speechInputs)
            foreach (var forbidden in new[] { "Running tools", "Reading attachments", "Preparing response", "Finalizing response", "tenantAttachmentRead", "read.png", Convert.ToBase64String(value.Bytes) })
                Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(events, e => e.Type == "agent.progress" && e.ResponseId == response && e.Payload["kind"]?.ToString() is "preparing" or "finalizing");
            var history = (await Json(await owner.GetAsync($"/api/v2/tenants/{v.Tenant}/sessions/{v.Session}/messages"))).GetProperty("items");
            Assert.DoesNotContain("Running tools", history.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_actual_tool_artifact_reconnect_retains_terminal_text_without_unreceived_blocks(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-artifact-reconnect-unreceived-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new ApprovalRecordingModel { ProposedArtifact = JsonSerializer.Serialize(new { displayName = "unreceived.md", content = "# Actual owned publication" }), EmitArtifactReference = true };
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model) : new NativeTenantMemoryFactory(root, model);
            factory.UseKestrel(0); using var ready = factory.CreateClient(); var cookies = new CookieContainer();
            using var owner = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = ready.BaseAddress };
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "customer-support", version = 3, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid();
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Publish and inspect actual artifact", ["sessions.read", "artifacts.create", "artifacts.read"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            var original = new ConcurrentQueue<ServerEvent>();
            Guid response;
            await using (var hub = TenantSocket(ready.BaseAddress!, cookies, original))
            {
                await hub.StartAsync(); Assert.True((await hub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(tenant, session))).Accepted);
                var sent = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Publish the actual file", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
                response = sent.GetProperty("responseId").GetGuid();
                var block = await AwaitTenantWire(() => original.FirstOrDefault(e => e.Type == "agent.block.upsert" && e.ResponseId == response.ToString("D")));
                Assert.Equal("artifact", block.Payload["kind"]); Assert.NotNull(block.Payload["artifactId"]);
                Assert.Equal(AgentRunStatus.Completed, (await AttachmentTerminal(factory.Services.GetRequiredService<ITenantRunStore>(), tenant, sent.GetProperty("runId").GetGuid())).Status);
                await AwaitTenantWire(() => original.FirstOrDefault(e => e.Type == "agent.response.completed" && e.ResponseId == response.ToString("D")));
                await hub.StopAsync();
            }
            var before = (await Json(await owner.GetAsync(api + $"/sessions/{session}/messages"))).GetProperty("items");
            var retained = Assert.Single(before.EnumerateArray(), e => e.GetProperty("responseId").GetString() == response.ToString("D"));
            Assert.Equal("The file was published.", retained.GetProperty("text").GetString()); Assert.Empty(retained.GetProperty("blocks").EnumerateArray());
            var again = new ConcurrentQueue<ServerEvent>(); await using var reconnected = TenantSocket(ready.BaseAddress!, cookies, again); await reconnected.StartAsync();
            Assert.True((await reconnected.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(tenant, session))).Accepted);
            var snapshot = await AwaitTenantWire(() => again.FirstOrDefault(e => e.Type == "session.ready"));
            var payload = JsonSerializer.SerializeToElement(snapshot.Payload);
            var assistant = Assert.Single(payload.GetProperty("history").EnumerateArray(), e => e.GetProperty("responseId").GetString() == response.ToString("D"));
            Assert.Equal(retained.GetProperty("text").GetString(), assistant.GetProperty("text").GetString()); Assert.Empty(assistant.GetProperty("blocks").EnumerateArray());
            Assert.Equal(session.ToString("D"), snapshot.SessionId); Assert.NotEqual(original.First(e => e.Type == "session.ready").AttachmentId, snapshot.AttachmentId);
            Assert.DoesNotContain(again, e => e.Type == "agent.block.upsert");
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
