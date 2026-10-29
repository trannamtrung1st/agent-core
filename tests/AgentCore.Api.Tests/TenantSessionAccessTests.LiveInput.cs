using System.Diagnostics;
using System.Collections.Concurrent;
using AgentCore.Contracts.Realtime;
using Microsoft.AspNetCore.SignalR.Client;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private static async Task ContinueLiveChildInChatAsync(HttpClient owner, string url)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            // Model entry does not mean its earlier runtime checkpoint has committed.
            // Use a coherent public witness pair; only a definitive CAS refusal can retry.
            using var read = await owner.GetAsync(url + "/messages?limit=1", deadline.Token);
            if (read.StatusCode == HttpStatusCode.Conflict && (await Json(read)).GetProperty("reasonCode").GetString() == "stale_revision")
            { await Task.Delay(25, deadline.Token); continue; }
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            var witness = await Json(read);
            using var response = await owner.PostAsJsonAsync(url + "/continue-in-chat", new {
                expectedRevision = witness.GetProperty("securityRevision").GetInt64(),
                expectedSessionRevision = witness.GetProperty("sessionRevision").GetInt64() }, deadline.Token);
            if (response.StatusCode == HttpStatusCode.Conflict && (await Json(response)).GetProperty("reasonCode").GetString() == "stale_revision")
            { await Task.Delay(25, deadline.Token); continue; }
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            return;
        }
    }

    [Theory(Timeout = 90000)] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_live_queue_binds_files_once_and_preserves_running_background_input(bool sqlite, bool cancelSource)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-live-queue-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new BackgroundFileModel();
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantBackgroundSqliteFactory(root, model) : new TenantBackgroundMemoryFactory(root, model);
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
            var setup = await StartTenantBackground(factory, owner, reportReads: true);
            await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var sessions = factory.Services.GetRequiredService<ITenantSessionStore>(); var runs = factory.Services.GetRequiredService<ITenantRunStore>();
            var child = (await sessions.ListTenantSessionsAsync(setup.Tenant, null, 0, 100)).Items.Single(s => s.Snapshot.Origin.OriginatingAgentRunId == setup.Source);
            var childRun = (await runs.FindTenantRunAsync(setup.Tenant, child.Snapshot.Origin.InitialBackgroundAgentRunId!.Value))!;
            var api = $"/api/v2/tenants/{setup.Tenant}"; var url = api + $"/sessions/{child.Owner.SessionId}";
            await ContinueLiveChildInChatAsync(owner, url);
            child = (await sessions.FindTenantSessionAsync(setup.Tenant, child.Owner.SessionId))!;
            using var upload = new MultipartFormDataContent(); var bytes = new ByteArrayContent(Encoding.UTF8.GetBytes("Human queued source, invisible to previous task.")); bytes.Headers.ContentType = new("text/plain"); upload.Add(bytes, "file", "queued.txt");
            var attachmentId = (await Json(await owner.PostAsync(url + $"/attachments?uploadId={Guid.NewGuid()}&expectedRevision={await Revision(owner, setup.Tenant)}&expectedSessionRevision={child.Snapshot.Revision}", upload))).GetProperty("attachmentId").GetGuid();
            var messageId = Guid.NewGuid();
            async Task<System.Text.Json.JsonElement> Send(string text, Guid id, Guid[] files)
            {
                var snapshot = (await sessions.FindTenantSessionAsync(setup.Tenant, child.Owner.SessionId))!.Snapshot;
                var response = await owner.PostAsJsonAsync(url + "/messages", new { messageId = id, text, attachmentIds = files, behavior = "queue", expectedRevision = await Revision(owner, setup.Tenant), expectedSessionRevision = snapshot.Revision });
                Assert.True(response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK, await response.Content.ReadAsStringAsync()); return await Json(response);
            }
            var queued = await Send("Keep the queued file available: first", messageId, [attachmentId]);
            var duplicate = await Send("Keep the queued file available: first", messageId, [attachmentId]);
            Assert.Equal(queued.GetProperty("runId").GetGuid(), duplicate.GetProperty("runId").GetGuid()); Assert.False(duplicate.GetProperty("created").GetBoolean());
            var second = await Send("Keep the queued file available: second", Guid.NewGuid(), []);
            var firstRun = (await runs.FindTenantRunAsync(setup.Tenant, queued.GetProperty("runId").GetGuid()))!;
            var secondRun = (await runs.FindTenantRunAsync(setup.Tenant, second.GetProperty("runId").GetGuid()))!;
            Assert.Equal(AgentRunStatus.Queued, firstRun.Status); Assert.Equal(AgentRunStatus.Queued, secondRun.Status);
            Assert.Equal(firstRun.ActivationId, firstRun.Admission.ExecutionContext!.RootActivationId);
            Assert.NotEqual(childRun.Admission.ExecutionContext!.RootActivationId, firstRun.Admission.ExecutionContext.RootActivationId);
            Assert.NotEqual(firstRun.ActivationId, secondRun.ActivationId);
            var attachment = (await factory.Services.GetRequiredService<ITenantAttachmentStore>().FindTenantAsync(setup.Tenant, child.Owner.SessionId, attachmentId))!;
            Assert.Equal(AttachmentState.Bound, attachment.State); Assert.Equal(queued.GetProperty("entryId").GetGuid(), attachment.EntryId);
            var active = await factory.Services.GetRequiredService<ITenantRuntimeStore>().LoadTenantRuntimeAsync(new(childRun.Owner, childRun.SessionId, childRun.AgentRunId, childRun.Claim!.Generation));
            Assert.NotNull(active); Assert.DoesNotContain(active.Entries, e => e.Text.Contains("Keep the queued", StringComparison.Ordinal));
            Assert.All(model.ChildRequests, r => Assert.DoesNotContain(r.Messages, m => m.Text.Contains("Keep the queued", StringComparison.Ordinal)));
            var nowSnapshot = (await sessions.FindTenantSessionAsync(setup.Tenant, child.Owner.SessionId))!.Snapshot;
            var changed = await owner.PostAsJsonAsync(url + "/messages", new { messageId, text = "Changed replay", attachmentIds = new[] { attachmentId }, behavior = "queue", expectedRevision = await Revision(owner, setup.Tenant), expectedSessionRevision = nowSnapshot.Revision });
            Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
            var interrupt = await owner.PostAsJsonAsync(url + "/messages", new { messageId = Guid.NewGuid(), text = "Do not imply interruption", behavior = "interrupt", expectedRevision = await Revision(owner, setup.Tenant), expectedSessionRevision = nowSnapshot.Revision });
            Assert.Equal(HttpStatusCode.BadRequest, interrupt.StatusCode);
            if (cancelSource)
            {
                var currentRun = (await runs.FindTenantRunAsync(setup.Tenant, childRun.AgentRunId))!;
                Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/agent-runs/{childRun.AgentRunId}/cancel", new { expectedRevision = await Revision(owner, setup.Tenant), expectedRunRevision = currentRun.Revision })).StatusCode);
            }
            model.Release.TrySetResult();
            foreach (var run in new[] { childRun, firstRun, secondRun })
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); AgentRun? saved;
                do { saved = await runs.FindTenantRunAsync(setup.Tenant, run.AgentRunId); if (saved!.IsTerminal) break; await Task.Delay(25, timeout.Token); } while (true);
                Assert.True(saved.Status == (cancelSource && run.AgentRunId == childRun.AgentRunId ? AgentRunStatus.Cancelled : AgentRunStatus.Completed), $"{saved.Status}: {saved.Failure} {string.Join('\n', model.Failures)}");
            }
            var final = (await sessions.FindTenantSessionAsync(setup.Tenant, child.Owner.SessionId))!.Snapshot;
            var history = await Json(await owner.GetAsync(url + "/messages?limit=100"));
            var entries = history.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(entries.Length, entries.Select(e => e.GetProperty("sequence").GetInt64()).Distinct().Count());
            Assert.Equal(2, entries.Count(e => e.GetProperty("text").GetString() == "Queued file noted."));
            Assert.Single(entries, e => e.GetProperty("text").GetString() == "Keep the queued file available: first");
            Assert.All(model.ChildRequests, r => Assert.DoesNotContain(r.Messages, m => m.Text.Contains("Keep the queued", StringComparison.Ordinal)));
            Assert.Equal(child.Snapshot.Origin, final.Origin);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_live_queue_crosses_real_MessagePack_wire_while_background_model_is_held(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-live-wire-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new BackgroundFileModel();
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model) : new NativeTenantMemoryFactory(root, model);
            factory.UseKestrel(0); using var owner = factory.CreateClient(); var setup = await StartTenantBackground(factory, owner, reportReads: true);
            await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var sessions = factory.Services.GetRequiredService<ITenantSessionStore>();
            var child = (await sessions.ListTenantSessionsAsync(setup.Tenant, null, 0, 100)).Items.Single(s => s.Snapshot.Origin.OriginatingAgentRunId == setup.Source);
            await ContinueLiveChildInChatAsync(owner, $"/api/v2/tenants/{setup.Tenant}/sessions/{child.Owner.SessionId}");
            var repo = new DirectoryInfo(AppContext.BaseDirectory); while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "AgentCore.sln"))) repo = repo.Parent; Assert.NotNull(repo);
            var start = new ProcessStartInfo("node", "tenant-live-queue.mjs") { WorkingDirectory = Path.Combine(repo.FullName, "tests", "realtime-js"), UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["BASE_URL"] = owner.BaseAddress!.ToString().TrimEnd('/'); start.Environment["TENANT_ID"] = setup.Tenant.ToString("D"); start.Environment["SESSION_ID"] = child.Owner.SessionId.ToString("D");
            using var process = Process.Start(start)!; var stderr = process.StandardError.ReadToEndAsync(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
            var output = new List<string>();
            try
            {
                while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
                { output.Add(line); if (line == "Queue accepted") model.Release.TrySetResult(); }
                await process.WaitForExitAsync(timeout.Token);
            }
            finally { model.Release.TrySetResult(); if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
            Assert.True(process.ExitCode == 0, string.Join('\n', output) + await stderr);
            Assert.Contains("Tenant live queue wire passed", output);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact(Timeout = 120000)]
    public async Task Tenant_live_queued_file_and_human_Run_survive_actual_SQLite_host_recreation_once()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-live-restart-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        Guid tenant, session, queuedRun, queuedEntry, attachment; var enabled = true;
        try
        {
            var firstModel = new BackgroundFileModel();
            await using (var first = new NativeTenantSqliteFactory(root, firstModel, services => services.Replace(ServiceDescriptor.Singleton<ITenantRunExecution>(p =>
                new MetadataExecutionGate(() => Volatile.Read(ref enabled), new TenantRunExecution(p.GetRequiredService<ITenantRuntimeStore>(), p.GetRequiredService<ITenantRunStore>(), p.GetRequiredService<ITenantRunDispatcher>(), p.GetRequiredService<IIdGenerator>(), p.GetRequiredService<TimeProvider>()))))))
            {
                using var owner = first.CreateClient(); var setup = await StartTenantBackground(first, owner, reportReads: true); tenant = setup.Tenant;
                await firstModel.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30)); var sessions = first.Services.GetRequiredService<ITenantSessionStore>();
                var child = (await sessions.ListTenantSessionsAsync(tenant, null, 0, 100)).Items.Single(s => s.Snapshot.Origin.OriginatingAgentRunId == setup.Source); session = child.Owner.SessionId;
                var api = $"/api/v2/tenants/{tenant}"; var url = api + $"/sessions/{session}";
                Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(url + "/continue-in-chat", new { expectedRevision = await Revision(owner, tenant), expectedSessionRevision = child.Snapshot.Revision })).StatusCode);
                var snapshot = (await sessions.FindTenantSessionAsync(tenant, session))!.Snapshot;
                using var upload = new MultipartFormDataContent(); var bytes = new ByteArrayContent("Durable queued source"u8.ToArray()); bytes.Headers.ContentType = new("text/plain"); upload.Add(bytes, "file", "reopen.txt");
                attachment = (await Json(await owner.PostAsync(url + $"/attachments?uploadId={Guid.NewGuid()}&expectedRevision={await Revision(owner, tenant)}&expectedSessionRevision={snapshot.Revision}", upload))).GetProperty("attachmentId").GetGuid();
                enabled = false;
                var receipt = await Json(await owner.PostAsJsonAsync(url + "/messages", new { messageId = Guid.NewGuid(), text = "Keep the queued file available after restart", attachmentIds = new[] { attachment }, expectedRevision = await Revision(owner, tenant), expectedSessionRevision = (await sessions.FindTenantSessionAsync(tenant, session))!.Snapshot.Revision }));
                queuedRun = receipt.GetProperty("runId").GetGuid(); queuedEntry = receipt.GetProperty("entryId").GetGuid();
                var runs = first.Services.GetRequiredService<ITenantRunStore>(); var active = (await runs.FindTenantRunAsync(tenant, child.Snapshot.Origin.InitialBackgroundAgentRunId!.Value))!;
                Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/agent-runs/{active.AgentRunId}/cancel", new { expectedRevision = await Revision(owner, tenant), expectedRunRevision = active.Revision })).StatusCode);
                Assert.Equal(AgentRunStatus.Queued, (await runs.FindTenantRunAsync(tenant, queuedRun))!.Status);
            }
            var nextModel = new BackgroundFileModel(); nextModel.Release.TrySetResult();
            await using (var second = new NativeTenantSqliteFactory(root, nextModel))
            {
                using var owner = second.CreateClient(); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                var runs = second.Services.GetRequiredService<ITenantRunStore>();
                var completed = await AwaitBrowserRestartRun(runs, tenant, queuedRun, false); Assert.Equal(AgentRunStatus.Completed, completed.Status);
                Assert.Equal(queuedEntry, Assert.Single(completed.Admission.Activation.SourceEntryIds));
                Assert.Equal(completed.ActivationId, completed.Admission.ExecutionContext!.RootActivationId);
                var bound = (await second.Services.GetRequiredService<ITenantAttachmentStore>().FindTenantAsync(tenant, session, attachment))!;
                Assert.Equal(AttachmentState.Bound, bound.State); Assert.Equal(queuedEntry, bound.EntryId);
                Assert.Equal("Durable queued source", Encoding.UTF8.GetString(await second.Services.GetRequiredService<ITenantAttachmentStore>().ReadTenantContentAsync(tenant, session, attachment)));
                var history = await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/sessions/{session}/messages?limit=100"));
                Assert.Single(history.GetProperty("items").EnumerateArray(), e => e.GetProperty("text").GetString() == "Queued file noted.");
                Assert.DoesNotContain(nextModel.ChildRequests, _ => true);
                Assert.Single(await second.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 200), a => a.Action == "agent-runs.admit" && a.Target == queuedRun.ToString("D"));
            }
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_live_queue_during_streaming_and_duplicate_ack_preserves_original_output_and_sequence(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-stream-queue-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); var model = new TenantPrefixModel();
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model) : new NativeTenantMemoryFactory(root, model);
            factory.UseKestrel(0); using var ready = factory.CreateClient(); var cookies = new CookieContainer();
            using var owner = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = ready.BaseAddress };
            var setup = await StartTenantBackground(factory, owner, submit: false, reportReads: true); var url = $"/api/v2/tenants/{setup.Tenant}/sessions/{setup.Session}";
            var events = new ConcurrentQueue<ServerEvent>(); await using var hub = TenantSocket(ready.BaseAddress!, cookies, events); await hub.StartAsync();
            Assert.True((await hub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(setup.Tenant, setup.Session))).Accepted);
            var first = await Json(await owner.PostAsJsonAsync(url + "/messages", new { messageId = Guid.NewGuid(), text = "Hello active source", expectedRevision = await Revision(owner, setup.Tenant), expectedSessionRevision = 1 }));
            await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.text.delta"));
            var sessions = factory.Services.GetRequiredService<ITenantSessionStore>(); var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var id = Guid.NewGuid();
            async Task<System.Text.Json.JsonElement> Queue()
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (true)
                {
                    // The live prefix may still be persisting after its wire delta.
                    // Read the public owner's coherent witness pair and retry only
                    // an explicit optimistic conflict, preserving the same message ID.
                    var current = await Json(await owner.GetAsync(url + "/messages?limit=1", deadline.Token));
                    using var response = await owner.PostAsJsonAsync(url + "/messages", new {
                        messageId = id, text = "Hello queued source", behavior = "queue",
                        expectedRevision = current.GetProperty("securityRevision").GetInt64(),
                        expectedSessionRevision = current.GetProperty("sessionRevision").GetInt64() }, deadline.Token);
                    if (response.StatusCode == HttpStatusCode.Conflict
                        && (await Json(response)).GetProperty("reasonCode").GetString() == "stale_revision")
                    { await Task.Delay(20, deadline.Token); continue; }
                    Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(deadline.Token)); return await Json(response);
                }
            }
            var queued = await Queue(); var replay = await Queue(); Assert.False(replay.GetProperty("created").GetBoolean());
            Assert.Equal(queued.GetProperty("runId").GetGuid(), replay.GetProperty("runId").GetGuid());
            Assert.Equal(AgentRunStatus.Queued, (await runs.FindTenantRunAsync(setup.Tenant, queued.GetProperty("runId").GetGuid()))!.Status);
            var admittedHistory = (await factory.Services.GetRequiredService<ITenantConversationStore>().ReadTenantConversationAsync(setup.Tenant, setup.Session, 0, null, 100))!;
            Assert.True((await sessions.FindTenantSessionAsync(setup.Tenant, setup.Session))!.Snapshot.LastUserActivityAt >= admittedHistory.History.Items.Where(e => e.Role == ConversationRole.User).Max(e => e.CreatedAt));
            var mark = events.Count; Assert.True((await hub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(setup.Tenant, setup.Session))).Accepted);
            var snapshot = await AwaitTenantWire(() => events.Skip(mark).FirstOrDefault(e => e.Type == "session.ready"));
            using (var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(snapshot.Payload)))
                Assert.Equal("Hello", Assert.Single(json.RootElement.GetProperty("history").EnumerateArray(), e => e.GetProperty("role").GetString() == "assistant").GetProperty("text").GetString());
            model.Release.TrySetResult();
            foreach (var receipt in new[] { first, queued })
                Assert.Equal(AgentRunStatus.Completed, (await AwaitBrowserRestartRun(runs, setup.Tenant, receipt.GetProperty("runId").GetGuid(), false)).Status);
            var history = await Json(await owner.GetAsync(url + "/messages?limit=100")); var entries = history.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(2, entries.Count(e => e.GetProperty("text").GetString() == "Hello from synthetic."));
            Assert.Equal(4, entries.Length); Assert.Equal(4, entries.Select(e => e.GetProperty("sequence").GetInt64()).Distinct().Count());
            var retained = (await factory.Services.GetRequiredService<ITenantConversationStore>().ReadTenantConversationAsync(setup.Tenant, setup.Session, 0, null, 100))!;
            Assert.True((await sessions.FindTenantSessionAsync(setup.Tenant, setup.Session))!.Snapshot.LastUserActivityAt >= retained.History.Items.Where(e => e.Role == ConversationRole.User).Max(e => e.CreatedAt));
        }
        finally { model.Release.TrySetResult(); SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

}
