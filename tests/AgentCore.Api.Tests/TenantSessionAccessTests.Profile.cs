using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using AgentCore.Application.Context;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Contracts.Realtime;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private static async Task SetRequesterProfile(HttpClient owner, Guid tenant, string name)
    {
        var path = $"/api/v2/tenants/{tenant}/me/profile"; var view = await Json(await owner.GetAsync(path));
        Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync(path, new { expectedRevision = view.GetProperty("securityRevision").GetInt64(),
            expectedProfileRevision = view.GetProperty("profile").GetProperty("revision").GetInt64(), values = new { preferredName = name } })).StatusCode);
    }
    private static async Task GrantProfileReads(WebApplicationFactory<Program> factory, HttpClient owner, Guid tenant, Guid instance)
    {
        var human = (await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/access/me"))).GetProperty("principalId").GetGuid();
        var access = factory.Services.GetRequiredService<TenantAccessService>(); var agent = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
        var role = await access.SaveRoleAsync(human, tenant, null, "Owned Profile reader", ["memory.read", "sessions.read"], await Revision(owner, tenant));
        await access.AssignRoleAsync(human, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
    }

    [Theory(Timeout = 90000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Run_Profile_follows_the_actual_human_root_into_owned_background_and_completion_report(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-profile-background-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new TenantBackgroundModel(); await using WebApplicationFactory<Program> factory = sqlite ? new TenantBackgroundSqliteFactory(root, model) : new TenantBackgroundMemoryFactory(root, model);
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
            var started = await StartTenantBackground(factory, owner, submit: false, reportReads: true);
            var session = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(started.Tenant, started.Session))!;
            await SetRequesterProfile(owner, started.Tenant, "ACTUAL_HUMAN_ROOT_PROFILE"); await GrantProfileReads(factory, owner, started.Tenant, session.Owner.AgentInstanceId);
            var input = await Json(await owner.PostAsJsonAsync($"/api/v2/tenants/{started.Tenant}/sessions/{started.Session}/messages", new { messageId = Guid.NewGuid(), text = "Start the exact owned background check",
                expectedRevision = await Revision(owner, started.Tenant), expectedSessionRevision = session.Snapshot.Revision }));
            var source = input.GetProperty("runId").GetGuid(); var (child, report) = await WaitTenantBackground(factory, started.Tenant, started.Session, source, true);
            var markers = new[] { (await factory.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(started.Tenant, source))!, child }
                .Select(r => AgentRunToolCallCheckpoint.ReadExecutionState(r.Checkpoint).TenantProfileSource).ToArray();
            Assert.All(markers, Assert.NotNull); Assert.All(markers, m => Assert.Equal(markers[0], m));
            Assert.All(model.Requests, r => Assert.Contains(r.Context!.Sections, s => s.Id == ContextSectionId.Profile && s.Layer == ContextLayer.Evidence
                && s.Message.Text.Contains("ACTUAL_HUMAN_ROOT_PROFILE", StringComparison.Ordinal)));
            Assert.Contains(model.Requests, r => TenantBackgroundModel.Origin(r) == ExecutionOrigin.DelegatedTask);
            // Completion uses a factual projection of the child, with no second model/Profile read.
            Assert.Null(AgentRunToolCallCheckpoint.ReadExecutionState(report!.Checkpoint).TenantProfileSource);
            Assert.DoesNotContain(model.Requests, r => TenantBackgroundModel.Origin(r) == ExecutionOrigin.CompletionReport);
            var reportReads = factory.Services.GetRequiredService<TenantReportReadAuthority>();
            await reportReads.RequireAsync(report, default);
            await SetRequesterProfile(owner, started.Tenant, "CHANGED_ROOT_PROFILE");
            await Assert.ThrowsAsync<AccessDeniedException>(() => reportReads.RequireAsync(report, default).AsTask());
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class ProfileVoiceModel : ILanguageModel
    {
        private readonly ScriptedLanguageModel inner = new(["The owned Profile voice result is ready."]);
        public ModelCapabilities Capabilities => inner.Capabilities;
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Enqueue(request); Entered.TrySetResult(); await Release.Task.WaitAsync(ct);
            await foreach (var item in inner.GenerateAsync(request, ct)) yield return item;
        }
    }
    [Theory(Timeout = 90000)] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Run_Profile_real_voice_delivers_current_sources_and_refuses_changed_sources_before_text_or_PCM(bool sqlite, bool changed)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-profile-voice-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); var model = new ProfileVoiceModel();
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model, s => TenantVoicePlan(s, false)) : new NativeTenantMemoryFactory(root, model, s => TenantVoicePlan(s, false));
            factory.UseKestrel(0); using var ready = factory.CreateClient(); var cookies = new CookieContainer();
            using var owner = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = ready.BaseAddress };
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var instance = (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
            await GrantNativePrivateSpaceRead(owner, tenant, instance);
            await SetRequesterProfile(owner, tenant, "OWNED_VOICE_PROFILE"); await GrantProfileReads(factory, owner, tenant, instance);
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var events = new ConcurrentQueue<ServerEvent>(); var audio = new ConcurrentQueue<OutputAudioDto>();
            await using var hub = TenantSocket(ready.BaseAddress!, cookies, events); hub.On<OutputAudioDto>("AudioOutput", audio.Enqueue); await hub.StartAsync();
            Assert.True((await hub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(tenant, session))).Accepted);
            var attached = await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "session.ready")); long sequence = 0;
            ClientCommand<T> Command<T>(string type, T payload, string? response = null) => new() { ProtocolVersion = 1, Type = type, EventId = Guid.NewGuid().ToString("D"),
                SessionId = session.ToString("D"), AttachmentId = attached.AttachmentId, Sequence = ++sequence, ResponseId = response, Payload = payload };
            Assert.True((await hub.InvokeAsync<CommandAck>("SetMode", Command("session.mode.set", new SetModePayload { Mode = "voice" }))).Accepted);
            var active = await AwaitTenantWire(() => events.LastOrDefault(e => e.Type == "session.state.changed" && e.Payload["streamId"] is not null));
            var stream = active.Payload["streamId"]!.ToString()!; var utterance = Guid.NewGuid().ToString("D");
            Assert.True((await hub.InvokeAsync<CommandAck>("SpeechStarted", Command("user.speech.started", new SpeechStartedPayload { StreamId = stream, UtteranceId = utterance, SampleOffset = 0, ActivityScore = .9 }))).Accepted);
            await hub.InvokeAsync("SendAudio", new InputAudioDto { ProtocolVersion = 1, SessionId = session.ToString("D"), AttachmentId = attached.AttachmentId!, StreamId = stream, FrameSequence = 1, SampleOffset = 0, Data = new byte[960] });
            Assert.True((await hub.InvokeAsync<CommandAck>("SpeechEnded", Command("user.speech.ended", new SpeechEndedPayload { StreamId = stream, UtteranceId = utterance, SampleOffset = 480, ActivityScore = .9, DurationMs = 300 }))).Accepted);
            await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20)); Assert.Contains(Assert.Single(model.Requests).Context!.Sections, s => s.Id == ContextSectionId.Profile && s.Message.Text.Contains("OWNED_VOICE_PROFILE", StringComparison.Ordinal));
            var started = await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.response.started")); var responseId = started.ResponseId!; var runId = Guid.Parse(started.Payload["agentRunId"]!.ToString()!);
            if (changed) await SetRequesterProfile(owner, tenant, "CHANGED_VOICE_PROFILE");
            model.Release.TrySetResult();
            if (!changed)
            {
                var last = await AwaitTenantWire(() => audio.FirstOrDefault(a => a.IsFinal));
                var speech = await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.speech.projection" && e.ResponseId == responseId));
                Assert.True((await hub.InvokeAsync<CommandAck>("PlaybackCompleted", Command("playback.completed", new PlaybackPayload { ConsumedSamples = last.SampleOffset + last.Data.Length / 2, TextEndExclusive = speech.Payload["text"]!.ToString()!.Length }, responseId))).Accepted);
            }
            var run = await AwaitCancelledTenantRun(factory.Services.GetRequiredService<ITenantRunStore>(), tenant, runId);
            Assert.Equal(!changed, run.Status == AgentRunStatus.Completed); Assert.NotNull(AgentRunToolCallCheckpoint.ReadExecutionState(run.Checkpoint).TenantProfileSource);
            if (changed)
            {
                Assert.Empty(audio); Assert.DoesNotContain(events, e => e.Type is "agent.text.delta" or "agent.speech.projection");
                var history = (await factory.Services.GetRequiredService<ITenantConversationStore>().ReadTenantConversationAsync(tenant, session, null, null, 100))!.History.Items;
                Assert.DoesNotContain(history, e => e.Text.Contains("The owned Profile voice result", StringComparison.Ordinal));
            }
            else Assert.NotEmpty(audio);
        }
        finally { model.Release.TrySetResult(); SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
