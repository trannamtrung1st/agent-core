using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Execution;
using AgentCore.Application.Security;
using AgentCore.Contracts.Realtime;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using AgentCore.Api.Realtime;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class RepeatedInterruptionModel : ILanguageModel
    {
        public TenantPrefixModel First { get; } = new(); public TenantPrefixModel Next { get; } = new();
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public ModelCapabilities Capabilities => First.Capabilities;
        public IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, CancellationToken cancellationToken = default) =>
            (Interlocked.Increment(ref calls) == 1 ? First : Next).GenerateAsync(request, cancellationToken);
    }
    [Theory(Timeout = 120000)] [InlineData(false, "http")] [InlineData(true, "http")]
    [InlineData(false, "wire")] [InlineData(true, "wire")] [InlineData(false, "file")] [InlineData(true, "file")]
    public async Task Tenant_interrupt_atomically_retires_exact_stream_and_admits_a_fresh_human_root(bool sqlite, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-interrupt-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var model = new TenantPrefixModel();
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model) : new NativeTenantMemoryFactory(root, model);
            factory.UseKestrel(0); using var ready = factory.CreateClient(); var cookies = new CookieContainer();
            using var owner = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = ready.BaseAddress };
            var setup = await StartTenantBackground(factory, owner, submit: false, reportReads: true);
            var url = $"/api/v2/tenants/{setup.Tenant}/sessions/{setup.Session}";
            var sessions = factory.Services.GetRequiredService<ITenantSessionStore>(); var runs = factory.Services.GetRequiredService<ITenantRunStore>();
            Guid? file = null;
            if (mode == "file")
            {
                using var upload = new MultipartFormDataContent(); upload.Add(new ByteArrayContent("Exact interrupted input bytes"u8.ToArray()), "file", "new-objective.txt");
                file = (await Json(await owner.PostAsync(url + $"/attachments?uploadId={Guid.NewGuid()}&expectedRevision={await Revision(owner, setup.Tenant)}&expectedSessionRevision=1", upload))).GetProperty("attachmentId").GetGuid();
            }
            var events = new ConcurrentQueue<ServerEvent>(); await using var hub = TenantSocket(ready.BaseAddress!, cookies, events); await hub.StartAsync();
            Assert.True((await hub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(setup.Tenant, setup.Session))).Accepted);
            var first = await Json(await owner.PostAsJsonAsync(url + "/messages", new { messageId = Guid.NewGuid(), text = "Hello initial interrupted source", expectedRevision = await Revision(owner, setup.Tenant), expectedSessionRevision = (await sessions.FindTenantSessionAsync(setup.Tenant, setup.Session))!.Snapshot.Revision }));
            var firstId = first.GetProperty("runId").GetGuid(); var responseId = first.GetProperty("responseId").GetGuid();
            await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.text.delta" && e.ResponseId == responseId.ToString("D")));
            var message = Guid.NewGuid(); var text = mode == "file" ? "" : "Hello new exact human objective"; var files = file is { } attachment ? new[] { attachment } : [];
            var stopped = (await runs.FindTenantRunAsync(setup.Tenant, firstId))!;
            async Task<HttpResponseMessage> Send() => await owner.PostAsJsonAsync(url + "/messages", new {
                messageId = message, text, attachmentIds = files, behavior = "interrupt", interruptedRunId = firstId, expectedRunRevision = stopped.Revision,
                expectedRevision = await Revision(owner, setup.Tenant), expectedSessionRevision = (await sessions.FindTenantSessionAsync(setup.Tenant, setup.Session))!.Snapshot.Revision });
            Guid nextId;
            if (mode == "wire")
            {
                var readyEvent = await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "session.ready"));
                var command = new ClientCommand<UserTextPayload> { ProtocolVersion = 1, EventId = message.ToString("D"), SessionId = setup.Session.ToString("D"),
                    AttachmentId = readyEvent.AttachmentId, Sequence = 1, Type = "user.text", Payload = new() { Text = text, Behavior = "interrupt", InterruptedRunId = firstId.ToString("D"), ExpectedRunRevision = stopped.Revision,
                        ExpectedRevision = await Revision(owner, setup.Tenant), ExpectedSessionRevision = (await sessions.FindTenantSessionAsync(setup.Tenant, setup.Session))!.Snapshot.Revision } };
                var ack = await hub.InvokeAsync<CommandAck>("SendText", command); Assert.True(ack.Accepted, ack.Error?.Code); nextId = Guid.Parse(Convert.ToString(ack.Result!["runId"])!);
                Assert.True((await hub.InvokeAsync<CommandAck>("SendText", command)).Accepted);
            }
            else
            {
                var accepted = await Send(); Assert.True(accepted.StatusCode == HttpStatusCode.Accepted, await accepted.Content.ReadAsStringAsync());
                nextId = (await Json(accepted)).GetProperty("runId").GetGuid();
            }
            Assert.Equal(AgentRunStatus.Cancelled, (await runs.FindTenantRunAsync(setup.Tenant, firstId))!.Status);
            var admitted = (await runs.FindTenantRunAsync(setup.Tenant, nextId))!;
            Assert.Equal(admitted.ActivationId, admitted.Admission.ExecutionContext!.RootActivationId); Assert.NotEqual(stopped.ActivationId, admitted.ActivationId);
            Assert.Null(admitted.Admission.ExecutionContext.ParentRunId); Assert.Equal(stopped.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, admitted.Admission.ExecutionContext.OriginalInitiatorPrincipalId);
            if (file is { } selected)
            {
                var source = (await factory.Services.GetRequiredService<ITenantAttachmentStore>().FindTenantAsync(setup.Tenant, setup.Session, selected))!;
                Assert.Equal(AttachmentState.Bound, source.State); Assert.Equal(admitted.Admission.Activation.SourceEntryIds.Single(), source.EntryId);
            }
            await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.response.interrupted" && e.ResponseId == responseId.ToString("D")));
            var mark = events.Count; model.Release.TrySetResult();
            Assert.Equal(AgentRunStatus.Completed, (await AwaitBrowserRestartRun(runs, setup.Tenant, nextId, false)).Status);
            await model.Returned.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var history = (await factory.Services.GetRequiredService<ITenantConversationStore>().ReadTenantConversationAsync(setup.Tenant, setup.Session, 0, null, 100))!;
            var original = Assert.Single(history.History.Items, e => e.ResponseId == responseId);
            Assert.Equal(EntryStatus.Interrupted, original.Status); Assert.Equal("Hello", original.Text); Assert.Equal("userSteer", original.InterruptReason);
            Assert.DoesNotContain(events.Skip(mark), e => e.ResponseId == responseId.ToString("D") && e.Type is "agent.text.delta" or "agent.response.completed");
            var replay = await Send(); Assert.True(replay.IsSuccessStatusCode, await replay.Content.ReadAsStringAsync());
            var replayed = await Json(replay); Assert.False(replayed.GetProperty("created").GetBoolean()); Assert.Equal(nextId, replayed.GetProperty("runId").GetGuid());
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(url + "/messages", new { messageId = message, text = "Changed exact objective", attachmentIds = files,
                behavior = "interrupt", interruptedRunId = firstId, expectedRunRevision = stopped.Revision,
                expectedRevision = await Revision(owner, setup.Tenant), expectedSessionRevision = (await sessions.FindTenantSessionAsync(setup.Tenant, setup.Session))!.Snapshot.Revision })).StatusCode);
            var audit = await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(setup.Tenant, 200);
            Assert.Single(audit, a => a.Action == "agent-runs.cancel" && a.Target == firstId.ToString("D"));
            Assert.Single(audit, a => a.Action == "agent-runs.admit" && a.Target == nextId.ToString("D"));
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { model.Release.TrySetResult(); SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact(Timeout = 120000)]
    public async Task Tenant_interrupted_admission_survives_actual_SQLite_host_recreation_without_reviving_old_output()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-interrupt-reopen-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var model = new TenantPrefixModel(); var enabled = true; Guid tenant, session, sourceId, newId; string before;
        try
        {
            await using (var first = new NativeTenantSqliteFactory(root, model, services => services.Replace(ServiceDescriptor.Singleton<ITenantRunExecution>(p =>
                new MetadataExecutionGate(() => Volatile.Read(ref enabled), new TenantRunExecution(p.GetRequiredService<ITenantRuntimeStore>(), p.GetRequiredService<ITenantRunStore>(), p.GetRequiredService<ITenantRunDispatcher>(), p.GetRequiredService<IIdGenerator>(), p.GetRequiredService<TimeProvider>()))))))
            {
                first.UseKestrel(0); using var ready = first.CreateClient(); var cookies = new CookieContainer();
                using var owner = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = ready.BaseAddress };
                var setup = await StartTenantBackground(first, owner, submit: false, reportReads: true); tenant = setup.Tenant; session = setup.Session;
                var url = $"/api/v2/tenants/{tenant}/sessions/{session}"; var events = new ConcurrentQueue<ServerEvent>();
                await using var hub = TenantSocket(ready.BaseAddress!, cookies, events); await hub.StartAsync(); Assert.True((await hub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(tenant, session))).Accepted);
                var receipt = await Json(await owner.PostAsJsonAsync(url + "/messages", new { messageId = Guid.NewGuid(), text = "Hello original before recreation", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
                sourceId = receipt.GetProperty("runId").GetGuid(); await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.text.delta"));
                var runs = first.Services.GetRequiredService<ITenantRunStore>(); var sessions = first.Services.GetRequiredService<ITenantSessionStore>(); var source = (await runs.FindTenantRunAsync(tenant, sourceId))!;
                enabled = false;
                var response = await owner.PostAsJsonAsync(url + "/messages", new { messageId = Guid.NewGuid(), text = "Hello durable replacement objective", behavior = "interrupt", interruptedRunId = sourceId, expectedRunRevision = source.Revision,
                    expectedRevision = await Revision(owner, tenant), expectedSessionRevision = (await sessions.FindTenantSessionAsync(tenant, session))!.Snapshot.Revision });
                Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync()); newId = (await Json(response)).GetProperty("runId").GetGuid();
                Assert.Equal(AgentRunStatus.Queued, (await runs.FindTenantRunAsync(tenant, newId))!.Status); model.Release.TrySetResult();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await ((TenantSessionRuntimeHost)first.Services.GetRequiredService<ITenantRunDispatcher>()).WaitForGenerationReleaseAsync(session, sourceId, source.Claim!.Generation, timeout.Token);
                await using var db = first.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContext(); var id = sourceId.ToString("D");
                before = await db.AgentRuns.AsNoTracking().Where(r => r.AgentRunId == id).Select(r => r.PayloadJson).SingleAsync();
            }
            await using (var second = new NativeTenantSqliteFactory(root, new ScriptedLanguageModel()))
            {
                using var owner = second.CreateClient(); await Login(owner, "owner@example.test", "explicit-synthetic-test-password"); var runs = second.Services.GetRequiredService<ITenantRunStore>();
                Assert.Equal(AgentRunStatus.Completed, (await AwaitBrowserRestartRun(runs, tenant, newId, false)).Status);
                Assert.Equal(AgentRunStatus.Cancelled, (await runs.FindTenantRunAsync(tenant, sourceId))!.Status);
                await using var db = second.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContext(); var id = sourceId.ToString("D");
                Assert.Equal(before, await db.AgentRuns.AsNoTracking().Where(r => r.AgentRunId == id).Select(r => r.PayloadJson).SingleAsync());
                var history = (await second.Services.GetRequiredService<ITenantConversationStore>().ReadTenantConversationAsync(tenant, session, 0, null, 100))!.History.Items;
                Assert.Single(history, e => e.Status == EntryStatus.Interrupted && e.Text == "Hello"); Assert.Single(history, e => e.Role == ConversationRole.Assistant && e.Status == EntryStatus.Completed && e.Text == "Hello from synthetic.");
                Assert.Equal(0, second.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
            }
        }
        finally { model.Release.TrySetResult(); SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_interruption_replay_and_changed_intent_never_stop_a_newer_live_response(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-interrupt-replay-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); var model = new RepeatedInterruptionModel();
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model) : new NativeTenantMemoryFactory(root, model);
            factory.UseKestrel(0); using var ready = factory.CreateClient(); var cookies = new CookieContainer();
            using var owner = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = ready.BaseAddress };
            var setup = await StartTenantBackground(factory, owner, submit: false, reportReads: true); var url = $"/api/v2/tenants/{setup.Tenant}/sessions/{setup.Session}";
            var events = new ConcurrentQueue<ServerEvent>(); await using var hub = TenantSocket(ready.BaseAddress!, cookies, events); await hub.StartAsync(); Assert.True((await hub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(setup.Tenant, setup.Session))).Accepted);
            var original = await Json(await owner.PostAsJsonAsync(url + "/messages", new { messageId = Guid.NewGuid(), text = "Hello original replay source", expectedRevision = await Revision(owner, setup.Tenant), expectedSessionRevision = 1 }));
            var sourceId = original.GetProperty("runId").GetGuid(); await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.text.delta"));
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var sessions = factory.Services.GetRequiredService<ITenantSessionStore>(); var source = (await runs.FindTenantRunAsync(setup.Tenant, sourceId))!; var message = Guid.NewGuid();
            async Task<HttpResponseMessage> Send(Guid id, string text, Guid target, long revision) => await owner.PostAsJsonAsync(url + "/messages", new { messageId = id, text,
                behavior = "interrupt", interruptedRunId = target, expectedRunRevision = revision, expectedRevision = await Revision(owner, setup.Tenant),
                expectedSessionRevision = (await sessions.FindTenantSessionAsync(setup.Tenant, setup.Session))!.Snapshot.Revision });
            var accepted = await Send(message, "Hello retained replacement", sourceId, source.Revision); Assert.True(accepted.IsSuccessStatusCode, await accepted.Content.ReadAsStringAsync());
            var next = await Json(accepted); var nextId = next.GetProperty("runId").GetGuid(); var nextResponse = next.GetProperty("responseId").GetString(); model.First.Release.TrySetResult();
            await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.text.delta" && e.ResponseId == nextResponse));
            var current = (await runs.FindTenantRunAsync(setup.Tenant, nextId))!; Assert.Equal(AgentRunStatus.Running, current.Status);
            var replay = await Send(message, "Hello retained replacement", sourceId, source.Revision); Assert.True(replay.IsSuccessStatusCode, await replay.Content.ReadAsStringAsync());
            Assert.False((await Json(replay)).GetProperty("created").GetBoolean());
            Assert.Equal(HttpStatusCode.Conflict, (await Send(message, "Changed replacement", sourceId, source.Revision)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await Send(message, "Hello retained replacement", nextId, current.Revision)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await Send(Guid.NewGuid(), "Must not retarget", sourceId, source.Revision)).StatusCode);
            Assert.Equal(AgentRunStatus.Running, (await runs.FindTenantRunAsync(setup.Tenant, nextId))!.Status);
            model.Next.Release.TrySetResult(); Assert.Equal(AgentRunStatus.Completed, (await AwaitBrowserRestartRun(runs, setup.Tenant, nextId, false)).Status);
            Assert.Equal(2, model.Calls); Assert.Equal(2, (await runs.ListTenantRunIdsAsync(setup.Tenant, setup.Session)).Count);
        }
        finally { model.First.Release.TrySetResult(); model.Next.Release.TrySetResult(); SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_interruption_of_continued_background_work_creates_an_independent_human_objective(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-interrupt-background-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); var model = new BackgroundFileModel();
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantBackgroundSqliteFactory(root, model) : new TenantBackgroundMemoryFactory(root, model);
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); var setup = await StartTenantBackground(factory, owner, reportReads: true);
            await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20)); var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var sessions = factory.Services.GetRequiredService<ITenantSessionStore>();
            var child = (await sessions.ListTenantSessionsAsync(setup.Tenant, null, 0, 100)).Items.Single(s => s.Snapshot.Origin.OriginatingAgentRunId == setup.Source);
            var sourceId = child.Snapshot.Origin.InitialBackgroundAgentRunId!.Value; var url = $"/api/v2/tenants/{setup.Tenant}/sessions/{child.Owner.SessionId}";
            Assert.True((await owner.PostAsJsonAsync(url + "/continue-in-chat", new { expectedRevision = await Revision(owner, setup.Tenant), expectedSessionRevision = child.Snapshot.Revision })).IsSuccessStatusCode);
            var source = (await runs.FindTenantRunAsync(setup.Tenant, sourceId))!;
            var accepted = await owner.PostAsJsonAsync(url + "/messages", new { messageId = Guid.NewGuid(), text = "Keep the queued human objective independent", behavior = "interrupt", interruptedRunId = sourceId, expectedRunRevision = source.Revision,
                expectedRevision = await Revision(owner, setup.Tenant), expectedSessionRevision = (await sessions.FindTenantSessionAsync(setup.Tenant, child.Owner.SessionId))!.Snapshot.Revision });
            Assert.True(accepted.IsSuccessStatusCode, await accepted.Content.ReadAsStringAsync()); var nextId = (await Json(accepted)).GetProperty("runId").GetGuid(); model.Release.TrySetResult();
            var next = await AwaitBrowserRestartRun(runs, setup.Tenant, nextId, false); Assert.Equal(AgentRunStatus.Completed, next.Status); Assert.Equal(next.ActivationId, next.Admission.ExecutionContext!.RootActivationId);
            Assert.Null(next.Admission.ExecutionContext.ParentRunId); Assert.NotEqual(source.Admission.ExecutionContext!.RootActivationId, next.ActivationId);
            Assert.Equal(AgentRunStatus.Cancelled, (await runs.FindTenantRunAsync(setup.Tenant, sourceId))!.Status);
            Assert.Equal(child.Snapshot.Origin, (await sessions.FindTenantSessionAsync(setup.Tenant, child.Owner.SessionId))!.Snapshot.Origin);
        }
        finally { model.Release.TrySetResult(); SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
