using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using AgentCore.Api.Realtime;
using AgentCore.Application.Ports;
using AgentCore.Contracts.Realtime;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class InterruptedWriteGate(bool refuse = false) : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int held;
        public async ValueTask BeforeInput(TenantHumanInput input, CancellationToken ct)
        {
            if (input.Admission.Interruption is null || Interlocked.Exchange(ref held, 1) != 0) return;
            Entered.TrySetResult(); await Release.Task.WaitAsync(ct);
            if (refuse) throw new InvalidOperationException("Injected interruption commit refusal");
        }
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (data.Context!.ChangeTracker.Entries<AgentRunRecord>().Any(e => e.State == EntityState.Added && e.Entity.PayloadJson.Contains("inputBehavior", StringComparison.Ordinal))
                && Interlocked.Exchange(ref held, 1) == 0)
            { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); if (refuse) throw new InvalidOperationException("Injected interruption commit refusal"); }
            return result;
        }
    }
    private sealed class InterruptionContexts(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    { public AgentCoreDbContext CreateDbContext() => new(options); }

    [Theory(Timeout = 120000)]
    [InlineData(false)] [InlineData(true)]
    public async Task Tenant_interruption_freezes_output_until_the_atomic_write_commits_or_refuses(bool refuse)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-interrupt-output-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var model = new TenantPrefixModel(); var gate = new InterruptedWriteGate(refuse);
        try
        {
            // Hold the Runtime's queued input before opening the SQLite writer.
            // The worker can then finish its current-authority read and actually
            // deliver the output that the mailbox must hold until commit/refusal.
            var storage = new TenantReceiptRaceStore { BeforeInput = gate.BeforeInput };
            await using var factory = new NativeTenantSqliteFactory(root, model, storage.Install);
            try
            {
            factory.UseKestrel(0); using var ready = factory.CreateClient(); var cookies = new CookieContainer();
            using var owner = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = ready.BaseAddress };
            var setup = await StartTenantBackground(factory, owner, submit: false, reportReads: true); var url = $"/api/v2/tenants/{setup.Tenant}/sessions/{setup.Session}";
            var events = new ConcurrentQueue<ServerEvent>(); await using var hub = TenantSocket(ready.BaseAddress!, cookies, events); await hub.StartAsync(); Assert.True((await hub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(setup.Tenant, setup.Session))).Accepted);
            var first = await Json(await owner.PostAsJsonAsync(url + "/messages", new { messageId = Guid.NewGuid(), text = "Hello original atomic source", expectedRevision = await Revision(owner, setup.Tenant), expectedSessionRevision = 1 }));
            var firstId = first.GetProperty("runId").GetGuid(); await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.text.delta"));
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var sessions = factory.Services.GetRequiredService<ITenantSessionStore>(); var source = (await runs.FindTenantRunAsync(setup.Tenant, firstId))!;
            var host = (TenantSessionRuntimeHost)factory.Services.GetRequiredService<ITenantRunDispatcher>();
            var runtime = host.ObserveInterruptionRuntime(setup.Session, firstId, source.Claim!.Generation);
            var submitting = owner.PostAsJsonAsync(url + "/messages", new { messageId = Guid.NewGuid(), text = "Hello atomic replacement", behavior = "interrupt", interruptedRunId = firstId, expectedRunRevision = source.Revision,
                expectedRevision = await Revision(owner, setup.Tenant), expectedSessionRevision = (await sessions.FindTenantSessionAsync(setup.Tenant, setup.Session))!.Snapshot.Revision });
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20)); model.Release.TrySetResult();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); await runtime.WaitForTenantInterruptionOutputHeldAsync(timeout.Token);
            // Observe delivery, rather than opening a second SQLite write transaction
            // while the fixture deliberately holds the atomic writer.
            Assert.Single(events, e => e.Type == "agent.text.delta");
            Assert.DoesNotContain(events, e => e.Type == "agent.response.completed");
            gate.Release.TrySetResult(); using var reply = await submitting;
            Assert.Equal(!refuse, reply.IsSuccessStatusCode);
            if (refuse)
            {
                Assert.Equal(AgentRunStatus.Completed, (await AwaitBrowserRestartRun(runs, setup.Tenant, firstId, false)).Status);
                Assert.Single(await runs.ListTenantRunIdsAsync(setup.Tenant, setup.Session));
            }
            else
            {
                var fact = await Json(reply); Assert.Equal(AgentRunStatus.Completed, (await AwaitBrowserRestartRun(runs, setup.Tenant, fact.GetProperty("runId").GetGuid(), false)).Status);
                Assert.Equal(AgentRunStatus.Cancelled, (await runs.FindTenantRunAsync(setup.Tenant, firstId))!.Status);
            }
            var history = (await factory.Services.GetRequiredService<ITenantConversationStore>().ReadTenantConversationAsync(setup.Tenant, setup.Session, 0, null, 100))!.History.Items;
            Assert.Single(history, e => e.Role == ConversationRole.Assistant && e.Status == EntryStatus.Completed && e.Text == "Hello from synthetic.");
            Assert.Equal(refuse ? 0 : 1, history.Count(e => e.Status == EntryStatus.Interrupted && e.Text == "Hello"));
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
            }
            finally { gate.Release.TrySetResult(); model.Release.TrySetResult(); }
        }
        finally { gate.Release.TrySetResult(); model.Release.TrySetResult(); SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact(Timeout = 120000)]
    public async Task Tenant_interruption_finishes_an_enqueued_write_and_releases_the_generation_after_the_caller_disconnects()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-interrupt-disconnect-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var model = new TenantPrefixModel(); var gate = new InterruptedWriteGate();
        try
        {
            await using var factory = new NativeTenantSqliteFactory(root, model, services => {
                var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={Path.Combine(root, "store.db")}")
                    .AddInterceptors(new SqlitePragmaInterceptor(5000), gate).Options;
                services.Replace(ServiceDescriptor.Singleton<IDbContextFactory<AgentCoreDbContext>>(new InterruptionContexts(options)));
            });
            try
            {
            factory.UseKestrel(0); using var ready = factory.CreateClient(); var cookies = new CookieContainer();
            using var owner = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = ready.BaseAddress };
            var setup = await StartTenantBackground(factory, owner, submit: false, reportReads: true); var url = $"/api/v2/tenants/{setup.Tenant}/sessions/{setup.Session}";
            var events = new ConcurrentQueue<ServerEvent>(); await using var hub = TenantSocket(ready.BaseAddress!, cookies, events); await hub.StartAsync(); Assert.True((await hub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(setup.Tenant, setup.Session))).Accepted);
            var first = await Json(await owner.PostAsJsonAsync(url + "/messages", new { messageId = Guid.NewGuid(), text = "Hello original disconnect source", expectedRevision = await Revision(owner, setup.Tenant), expectedSessionRevision = 1 }));
            var firstId = first.GetProperty("runId").GetGuid(); await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.text.delta"));
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var sessions = factory.Services.GetRequiredService<ITenantSessionStore>(); var source = (await runs.FindTenantRunAsync(setup.Tenant, firstId))!; var message = Guid.NewGuid();
            using var caller = new CancellationTokenSource();
            var submitting = owner.PostAsJsonAsync(url + "/messages", new { messageId = message, text = "Hello exact unacknowledged replacement", behavior = "interrupt", interruptedRunId = firstId, expectedRunRevision = source.Revision,
                expectedRevision = await Revision(owner, setup.Tenant), expectedSessionRevision = (await sessions.FindTenantSessionAsync(setup.Tenant, setup.Session))!.Snapshot.Revision }, caller.Token);
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20)); caller.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => submitting);
            gate.Release.TrySetResult();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await ((TenantSessionRuntimeHost)factory.Services.GetRequiredService<ITenantRunDispatcher>()).WaitForGenerationReleaseAsync(setup.Session, firstId, source.Claim!.Generation, timeout.Token);
            Assert.False(model.Returned.Task.IsCompleted); // The noncooperative old provider is still held; it owns no Runtime slot.
            Assert.Equal(AgentRunStatus.Cancelled, (await runs.FindTenantRunAsync(setup.Tenant, firstId))!.Status);
            var values = new List<AgentRun>(); foreach (var id in await runs.ListTenantRunIdsAsync(setup.Tenant, setup.Session)) values.Add((await runs.FindTenantRunAsync(setup.Tenant, id))!);
            var next = Assert.Single(values, r => r.Admission.Activation.SourceEventId == message); model.Release.TrySetResult();
            Assert.Equal(AgentRunStatus.Completed, (await AwaitBrowserRestartRun(runs, setup.Tenant, next.AgentRunId, false)).Status);
            var replay = await owner.PostAsJsonAsync(url + "/messages", new { messageId = message, text = "Hello exact unacknowledged replacement", behavior = "interrupt", interruptedRunId = firstId, expectedRunRevision = source.Revision,
                expectedRevision = await Revision(owner, setup.Tenant), expectedSessionRevision = (await sessions.FindTenantSessionAsync(setup.Tenant, setup.Session))!.Snapshot.Revision });
            Assert.True(replay.IsSuccessStatusCode, await replay.Content.ReadAsStringAsync()); var fact = await Json(replay); Assert.False(fact.GetProperty("created").GetBoolean()); Assert.Equal(next.AgentRunId, fact.GetProperty("runId").GetGuid());
            var history = (await factory.Services.GetRequiredService<ITenantConversationStore>().ReadTenantConversationAsync(setup.Tenant, setup.Session, 0, null, 100))!.History.Items;
            Assert.Single(history, e => e.Status == EntryStatus.Interrupted && e.Text == "Hello"); Assert.Single(history, e => e.Role == ConversationRole.Assistant && e.Status == EntryStatus.Completed);
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
            }
            finally { gate.Release.TrySetResult(); model.Release.TrySetResult(); }
        }
        finally { gate.Release.TrySetResult(); model.Release.TrySetResult(); SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
