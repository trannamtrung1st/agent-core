using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using AgentCore.Api.Http;
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
    [Theory(Timeout = 120000)]
    [InlineData(false, "queue")] [InlineData(true, "queue")]
    [InlineData(false, "interrupt")] [InlineData(true, "interrupt")]
    [InlineData(false, "revoke")] [InlineData(true, "revoke")]
    [InlineData(false, "logout")] [InlineData(true, "logout")]
    [InlineData(false, "disconnect")] [InlineData(true, "disconnect")]
    public async Task Tenant_voice_current_human_receipt_fences_and_no_implicit_cancel_preserve_accepted_work(bool sqlite, string boundary)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-voice-authority-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var gate = new TenantReceiptRaceStore();
        try
        {
            void Configure(IServiceCollection s) { TenantVoicePlan(s, true); gate.Install(s); }
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, new ScriptedLanguageModel(), Configure) : new NativeTenantMemoryFactory(root, new ScriptedLanguageModel(), Configure);
            factory.UseKestrel(0); using var ready = factory.CreateClient();
            using var owner = new HttpClient(new HttpClientHandler { CookieContainer = new() }) { BaseAddress = ready.BaseAddress };
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var principal = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var instance = (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var identity = factory.Services.GetRequiredService<IdentityService>(); var access = factory.Services.GetRequiredService<TenantAccessService>();
            var member = await identity.RegisterAsync("voice-reader@example.test", "Voice reader", "strong-voice-password");
            var role = await access.SaveRoleAsync(principal, tenant, null, "Converse without cancellation", boundary == "interrupt" ? ["sessions.read", "agents.chat", "agent-runs.read", "agent-runs.cancel"] : ["sessions.read", "agents.chat"], await Revision(owner, tenant));
            var invitation = await access.InviteAsync(principal, tenant, member.LoginKey, role.RoleId, await Revision(owner, tenant));
            await access.AcceptInvitationAsync(member.AccountId, tenant, invitation.Invitation.InvitationId, invitation.Token);
            var cookies = new CookieContainer(); using var reader = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = ready.BaseAddress };
            await Login(reader, member.LoginKey, "strong-voice-password");
            var readerPrincipal = (await Json(await reader.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var events = new ConcurrentQueue<ServerEvent>(); await using var hub = TenantSocket(ready.BaseAddress!, cookies, events); await hub.StartAsync();
            Assert.True((await hub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(tenant, session))).Accepted);
            var readyEvent = await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "session.ready")); long sequence = 0;
            ClientCommand<T> Command<T>(string type, T payload, string? response = null) => new() { ProtocolVersion = 1, Type = type, EventId = Guid.NewGuid().ToString("D"),
                SessionId = session.ToString("D"), AttachmentId = readyEvent.AttachmentId, Sequence = ++sequence, Payload = payload, ResponseId = response };
            var mode = await hub.InvokeAsync<CommandAck>("SetMode", Command("session.mode.set", new SetModePayload { Mode = "voice" })); Assert.True(mode.Accepted, mode.Error?.Code);
            var activated = await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "session.state.changed" && e.Payload["mode"]?.ToString() == "voice"));
            var stream = activated.Payload["streamId"]!.ToString();
            async Task<ServerEvent> Speak(string text)
            {
                var utterance = Guid.NewGuid().ToString("D");
                foreach (var item in new[] { new ClientSpeechEvidencePayload { StreamId = stream, Kind = "started", UtteranceId = utterance },
                    new ClientSpeechEvidencePayload { StreamId = stream, Kind = "final", UtteranceId = utterance, Text = text, Confidence = .9 } })
                { var ack = await hub.InvokeAsync<CommandAck>("SpeechEvidence", Command("client.speech.evidence", item)); Assert.True(ack.Accepted, ack.Error?.Code); }
                return await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "transcript.final" && e.Payload["utteranceId"]?.ToString() == utterance));
            }
            await Speak("Hello voice authority");
            var started = await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.response.started")); var response = started.ResponseId!;
            var outputDone = await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "speech.output.completed" && e.ResponseId == response));
            var end = Convert.ToInt32(outputDone.Payload["textEndExclusive"]);
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var runId = Guid.Parse(started.Payload["agentRunId"]!.ToString()!);
            async Task<ConversationEntry> Saved() => (await factory.Services.GetRequiredService<ITenantConversationStore>().ReadTenantConversationAsync(tenant, session, null, null, 100))!.History.Items.Single(e => e.ResponseId?.ToString("D") == response);
            if (boundary == "queue")
            {
                await Speak("Stop please, a separate human objective");
                var active = (await runs.FindTenantRunAsync(tenant, runId))!; Assert.Equal(AgentRunStatus.Running, active.Status); Assert.False(active.CancellationRequested);
                var saved = await runs.ListTenantSessionRunsAsync(tenant, session, null, 0, 100);
                var queued = Assert.Single(saved.Items, r => r.AgentRunId != runId); Assert.Equal(AgentRunStatus.Queued, queued.Status);
                Assert.Equal(readerPrincipal, queued.Admission.ExecutionContext!.OriginalInitiatorPrincipalId); Assert.Equal(queued.ActivationId, queued.Admission.ExecutionContext.RootActivationId);
                Assert.NotEqual(active.ActivationId, queued.ActivationId);
                var off = await hub.InvokeAsync<CommandAck>("SetMode", Command("session.mode.set", new SetModePayload { Mode = "text" })); Assert.True(off.Accepted, off.Error?.Code);
                Assert.Equal(AgentRunStatus.Completed, (await AwaitCancelledTenantRun(runs, tenant, runId)).Status);
                Assert.Equal(AgentRunStatus.Completed, (await AwaitCancelledTenantRun(runs, tenant, queued.AgentRunId)).Status);
            }
            else if (boundary == "interrupt")
            {
                var heard = Math.Max(1, end / 2);
                var progress = await hub.InvokeAsync<CommandAck>("PlaybackProgress", Command("playback.progress", new PlaybackPayload { TextEndExclusive = heard }, response)); Assert.True(progress.Accepted, progress.Error?.Code);
                await Speak("Stop please, a separately authorized new objective");
                var cancelled = await AwaitCancelledTenantRun(runs, tenant, runId); Assert.Equal(AgentRunStatus.Cancelled, cancelled.Status);
                var interrupted = await Saved(); Assert.Equal(EntryStatus.Interrupted, interrupted.Status); Assert.Equal(heard, interrupted.HeardTextEndExclusive);
                var following = await AwaitTenantWire(() => events.LastOrDefault(e => e.Type == "agent.response.started" && e.ResponseId != response));
                var nextId = Guid.Parse(following.Payload["agentRunId"]!.ToString()!); var next = (await runs.FindTenantRunAsync(tenant, nextId))!;
                Assert.NotEqual(cancelled.ActivationId, next.ActivationId); Assert.Equal(next.ActivationId, next.Admission.ExecutionContext!.RootActivationId);
                Assert.Equal(readerPrincipal, next.Admission.ExecutionContext.OriginalInitiatorPrincipalId);
                await hub.InvokeAsync<CommandAck>("PlaybackCompleted", Command("playback.completed", new PlaybackPayload { TextEndExclusive = end }, response));
                Assert.Equal(heard, (await Saved()).HeardTextEndExclusive);
                var off = await hub.InvokeAsync<CommandAck>("SetMode", Command("session.mode.set", new SetModePayload { Mode = "text" })); Assert.True(off.Accepted, off.Error?.Code);
                Assert.Equal(AgentRunStatus.Completed, (await AwaitCancelledTenantRun(runs, tenant, nextId)).Status);
            }
            else if (boundary is "revoke" or "logout")
            {
                gate.HoldReceipt = true;
                var pending = hub.InvokeAsync<CommandAck>("PlaybackCompleted", Command("playback.completed", new PlaybackPayload { TextEndExclusive = end }, response));
                await gate.ReceiptEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                if (boundary == "revoke") await access.SaveRoleAsync(principal, tenant, role.RoleId, role.Name, ["sessions.read"], await Revision(owner, tenant));
                else Assert.Equal(HttpStatusCode.NoContent, (await reader.PostAsync("/api/v2/auth/logout", null)).StatusCode);
                gate.ReleaseReceipt.TrySetResult(); var denied = await pending; Assert.False(denied.Accepted);
                Assert.Equal(AgentRunStatus.Completed, (await AwaitCancelledTenantRun(runs, tenant, runId)).Status);
                Assert.Equal(0, (await Saved()).HeardTextEndExclusive);
            }
            else
            {
                await hub.StopAsync();
                Assert.Equal(AgentRunStatus.Completed, (await AwaitCancelledTenantRun(runs, tenant, runId)).Status);
                Assert.Equal(0, (await Saved()).HeardTextEndExclusive);
            }
            var snapshot = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, session))!.Snapshot;
            Assert.Equal(SessionMode.Text, snapshot.Mode); Assert.Null(snapshot.PendingMode); Assert.Equal(SessionStatus.Created, snapshot.Status);
            Assert.Equal(readerPrincipal, (await runs.FindTenantRunAsync(tenant, runId))!.Admission.ExecutionContext!.OriginalInitiatorPrincipalId);
            Assert.Equal(0, factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
        }
        finally { gate.ReleaseReceipt.TrySetResult(); SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
